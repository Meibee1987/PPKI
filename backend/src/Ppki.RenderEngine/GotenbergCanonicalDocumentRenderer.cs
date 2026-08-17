using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.RenderEngine;

public sealed class GotenbergCanonicalDocumentRenderer(
    IHttpClientFactory clients,
    IOptions<DocumentRendererOptions> options,
    IDocxParser parser) : ICanonicalDocumentRenderer
{
    public const string ClientName = "canonical-document-renderer";
    private readonly DocumentRendererOptions settings = options.Value;

    public async Task<CanonicalDocumentRenderResult> RenderAsync(
        string sourceDocxPath,
        CancellationToken cancellationToken)
    {
        ValidateSource(sourceDocxPath);
        var sourceHash = await HashFileAsync(sourceDocxPath, cancellationToken);
        var workspace = Path.Combine(Path.GetTempPath(), $"ppki-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var renderCopy = Path.Combine(workspace, "document.docx");
        try
        {
            File.Copy(sourceDocxPath, renderCopy, overwrite: false);
            var parsed = await parser.ParseAsync(sourceDocxPath, cancellationToken);
            var textFingerprint = VisibleTextFingerprint(parsed);
            var anchors = InjectAnchors(renderCopy, parsed);
            if (!StringComparer.Ordinal.Equals(textFingerprint,
                    VisibleTextFingerprint(await parser.ParseAsync(renderCopy, cancellationToken))))
                throw new DocumentRenderException("render-anchor-content-changed", retryable: false);

            var pdf = await ConvertAsync(renderCopy, cancellationToken);
            var sourceHashAfter = await HashFileAsync(sourceDocxPath, cancellationToken);
            if (!StringComparer.Ordinal.Equals(sourceHash, sourceHashAfter))
                throw new DocumentRenderException("render-source-mutated", retryable: false);

            var map = ReadPageMap(pdf, anchors);
            return new(pdf, Convert.ToHexStringLower(SHA256.HashData(pdf)), map.PageCount,
                textFingerprint, map.Entries);
        }
        catch (DocumentRenderException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new DocumentRenderException("render-workspace-failed", retryable: true, exception); }
        catch (Exception exception)
        { throw new DocumentRenderException("render-package-invalid", retryable: false, exception); }
        finally
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true); }
            catch { }
        }
    }

    private void ValidateSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new DocumentRenderException("render-source-path-invalid", retryable: false);
        var info = new FileInfo(path);
        if (!info.Exists) throw new DocumentRenderException("render-source-missing", retryable: false);
        if (info.Length is <= 0 || info.Length > settings.MaximumInputBytes)
            throw new DocumentRenderException("render-source-size-invalid", retryable: false);
    }

    private async Task<byte[]> ConvertAsync(string renderCopy, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + '/'), "forms/libreoffice/convert"));
        using var multipart = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(renderCopy));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        multipart.Add(file, "files", "document.docx");
        multipart.Add(new StringContent("true"), "exportBookmarksToPdfDestination");
        multipart.Add(new StringContent("false"), "updateIndexes");
        multipart.Add(new StringContent("false"), "exportFormFields");
        request.Content = multipart;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 120)));
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new DocumentRenderException("render-timeout", retryable: true, exception); }
        catch (HttpRequestException exception)
        { throw new DocumentRenderException("renderer-unavailable", retryable: true, exception); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new DocumentRenderException(response.StatusCode is >= System.Net.HttpStatusCode.InternalServerError
                    ? "renderer-transient-failure" : "renderer-rejected-document",
                    retryable: response.StatusCode is >= System.Net.HttpStatusCode.InternalServerError);
            if (response.Content.Headers.ContentLength is > 0
                && response.Content.Headers.ContentLength > settings.MaximumPdfBytes)
                throw new DocumentRenderException("render-pdf-size-invalid", retryable: false);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > settings.MaximumPdfBytes)
                    throw new DocumentRenderException("render-pdf-size-invalid", retryable: false);
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            var bytes = output.ToArray();
            if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                throw new DocumentRenderException("render-pdf-invalid", retryable: false);
            return bytes;
        }
    }

    private static IReadOnlyList<Anchor> InjectAnchors(string path, ParsedDocument parsed)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var main = document.MainDocumentPart
            ?? throw new DocumentRenderException("render-source-package-invalid", retryable: false);
        var body = main.Document?.Body
            ?? throw new DocumentRenderException("render-source-package-invalid", retryable: false);
        var paragraphs = MainParagraphs(body).ToArray();
        if (paragraphs.Length != parsed.Paragraphs.Count)
            throw new DocumentRenderException("render-anchor-structure-mismatch", retryable: false);
        uint bookmarkId = body.Descendants<BookmarkStart>()
            .Select(value => uint.TryParse(value.Id?.Value, out var id) ? id : 0U)
            .DefaultIfEmpty().Max() + 1;
        var anchors = new List<Anchor>();
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var paragraph = paragraphs[index];
            var parsedParagraph = parsed.Paragraphs[index];
            var runs = paragraph.Descendants<Run>().ToArray();
            if (runs.Length != parsedParagraph.RunList.Count)
                throw new DocumentRenderException("render-anchor-structure-mismatch", retryable: false);
            var paragraphName = $"PPKIP{index:X8}";
            Insert(paragraph, paragraph.ChildElements.FirstOrDefault(value => value is not ParagraphProperties), paragraphName, bookmarkId++);
            anchors.Add(Anchor.From(paragraphName, parsedParagraph.Location, runIndex: null));
            for (var runIndex = 0; runIndex < runs.Length; runIndex++)
            {
                var runName = $"PPKIR{index:X8}{runIndex:X8}";
                Insert((OpenXmlCompositeElement)runs[runIndex].Parent!, runs[runIndex], runName, bookmarkId++);
                anchors.Add(Anchor.From(runName, parsedParagraph.RunList[runIndex].Location, runIndex));
            }
        }
        main.Document.Save();
        return anchors;
    }

    private static IEnumerable<Paragraph> MainParagraphs(Body body)
    {
        foreach (var child in body.ChildElements)
        {
            if (child is Paragraph paragraph) yield return paragraph;
            if (child is not Table table) continue;
            foreach (var row in table.Elements<TableRow>())
            foreach (var cell in row.Elements<TableCell>())
            foreach (var cellParagraph in cell.Elements<Paragraph>())
                yield return cellParagraph;
        }
    }

    private static void Insert(OpenXmlCompositeElement parent, OpenXmlElement? before, string name, uint id)
    {
        var start = new BookmarkStart { Name = name, Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var end = new BookmarkEnd { Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (before is null) { parent.Append(start, end); return; }
        parent.InsertBefore(start, before);
        parent.InsertBefore(end, before);
    }

    private static PageMapResult ReadPageMap(byte[] bytes, IReadOnlyList<Anchor> anchors)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        var destinations = pdf.Internals.Catalog.Elements.GetDictionary("/Dests");
        var pages = Enumerable.Range(0, pdf.PageCount)
            .Where(index => pdf.Pages[index].Reference is not null)
            .ToDictionary(index => pdf.Pages[index].Reference!.ObjectID, index => index + 1);
        var mapped = new Dictionary<string, int>(StringComparer.Ordinal);
        if (destinations is not null)
        {
            foreach (var item in destinations.Elements)
            {
                var array = item.Value as PdfArray ?? (item.Value as PdfReference)?.Value as PdfArray;
                if (array is null || array.Elements.Count < 1 || array.Elements[0] is not PdfReference reference
                    || !pages.TryGetValue(reference.ObjectID, out var page)) continue;
                mapped[item.Key.TrimStart('/')] = page;
            }
        }
        var entries = anchors.Select(anchor => mapped.TryGetValue(anchor.Name, out var page)
            ? anchor.Entry(PageMapConfidence.Exact, page, null)
            : anchor.Entry(PageMapConfidence.Unavailable, null, "structural-anchor-unavailable")).ToArray();
        return new(pdf.PageCount, entries);
    }

    private static string VisibleTextFingerprint(ParsedDocument document)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var paragraph in document.Paragraphs.OrderBy(value => value.Index))
        {
            var bytes = Encoding.UTF8.GetBytes(paragraph.Text);
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed record Anchor(string Name, DocumentElementLocation Location, int? RunIndex)
    {
        public static Anchor From(string name, DocumentElementLocation? location, int? runIndex) =>
            new(name, location ?? throw new DocumentRenderException("render-anchor-location-missing", false), runIndex);

        public PageMapRenderEntry Entry(PageMapConfidence confidence, int? pageNumber, string? reason) => new(
            Location.ToCompactString(), Location.SectionIndex, Location.BodyElementIndex,
            Location.ParagraphIndex, RunIndex, Location.TableIndex, Location.RowIndex,
            Location.CellIndex, confidence, pageNumber, reason);
    }

    private sealed record PageMapResult(int PageCount, IReadOnlyList<PageMapRenderEntry> Entries);
}
