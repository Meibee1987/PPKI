using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Ppki.DocxEngine;

public enum ExactTextTargetStatus { Exact, Unsupported, Stale }

public sealed record ExactTextSourceSpan(
    int RunIndex,
    int NodeIndex,
    string NodeKind,
    int CanonicalStart,
    int CanonicalLength,
    int SourceStart,
    int SourceLength,
    bool IsHyperlink,
    bool IsBold,
    bool IsItalic);

public sealed record ExactTextAnchor(
    string ContractVersion,
    string TextModelVersion,
    Guid DocumentVersionId,
    string SourceSha256,
    DocumentElementLocation ParagraphLocation,
    int Start,
    int Length,
    string TargetFingerprint,
    string ParagraphFingerprint,
    string PrefixFingerprint,
    string SuffixFingerprint,
    IReadOnlyList<ExactTextSourceSpan> Spans)
{
    public string SerializeCanonical() => ExactTextAnchorContract.Serialize(this);
    public string AnchorHash => ExactTextAnchorContract.Fingerprint("anchor", SerializeCanonical());
}

public sealed record ExactTextTargetResult(
    ExactTextTargetStatus Status,
    string? SafeReason,
    ExactTextAnchor? Anchor,
    IReadOnlyList<ExactTextSourceSpan> Segments)
{
    public static ExactTextTargetResult Unsupported(string reason) => new(ExactTextTargetStatus.Unsupported, reason, null, []);
    public static ExactTextTargetResult Stale(string reason) => new(ExactTextTargetStatus.Stale, reason, null, []);
}

public sealed class ExactTextTransientExcerpt
{
    internal ExactTextTransientExcerpt(ExactTextTargetStatus status, string? safeReason,
        string? targetText, string? context, bool prefixTruncated, bool suffixTruncated)
    {
        Status = status;
        SafeReason = safeReason;
        TargetText = targetText;
        Context = context;
        PrefixTruncated = prefixTruncated;
        SuffixTruncated = suffixTruncated;
    }

    public ExactTextTargetStatus Status { get; }
    public string? SafeReason { get; }
    public string? TargetText { get; }
    public string? Context { get; }
    public bool PrefixTruncated { get; }
    public bool SuffixTruncated { get; }

    public override string ToString() => $"ExactTextTransientExcerpt(Status={Status},Content=[REDACTED])";
}

/// <summary>
/// Read-only exact targeting over WordprocessingML. Coordinates count Unicode scalar values.
/// The model preserves source Unicode without normalization (NormalizationForm=None), preserves
/// NBSP and soft hyphen, maps tabs to U+0009, line/text-wrapping breaks and CR to U+000A,
/// page breaks to U+000C, and column breaks to U+000B. Run boundaries and hyperlinks do not
/// add characters. Field results are represented but are not safe targets.
/// </summary>
public sealed class ExactTextAnchorMaterializer
{
    public Task<ExactTextTargetResult> BuildAsync(
        string sourceDocxPath,
        Guid documentVersionId,
        string expectedSourceSha256,
        int paragraphIndex,
        int start,
        int length,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        var expectedSha = NormalizeSha(expectedSourceSha256);
        var actualSha = ComputeSha(sourceDocxPath);
        if (!string.Equals(expectedSha, actualSha, StringComparison.Ordinal))
            return Task.FromResult(ExactTextTargetResult.Stale("source-sha-mismatch"));

        using var document = WordprocessingDocument.Open(sourceDocxPath, false, new OpenSettings { AutoSave = false });
        var paragraph = LocateParagraph(document, paragraphIndex);
        if (paragraph is null) return Task.FromResult(ExactTextTargetResult.Stale("paragraph-location-missing"));
        var model = BuildParagraphModel(paragraph.Source, paragraph.Location);
        if (start > model.ScalarLength || length > model.ScalarLength - start)
            return Task.FromResult(ExactTextTargetResult.Stale("target-range-missing"));
        var end = start + length;
        if (model.UnsafeBoundaries.Any(value => value > start && value < end))
            return Task.FromResult(ExactTextTargetResult.Unsupported("unsupported-structure-overlap"));

        var spans = model.Segments
            .Where(value => value.CanonicalStart < end && value.CanonicalStart + value.CanonicalLength > start)
            .Select(value => Slice(value, start, end))
            .ToArray();
        if (spans.Length == 0 || spans.Any(value => !model.SafeSegmentKeys.Contains((value.RunIndex, value.NodeIndex))))
            return Task.FromResult(ExactTextTargetResult.Unsupported("unsupported-content-overlap"));

        var target = ScalarSlice(model.Text, start, length);
        var prefixStart = Math.Max(0, start - ExactTextAnchorContract.ContextScalarLength);
        var suffixLength = Math.Min(ExactTextAnchorContract.ContextScalarLength, model.ScalarLength - end);
        var anchor = new ExactTextAnchor(
            ExactTextAnchorContract.ContractVersion,
            ExactTextAnchorContract.TextModelVersion,
            documentVersionId,
            actualSha,
            paragraph.Location,
            start,
            length,
            ExactTextAnchorContract.Fingerprint("target", target),
            ExactTextAnchorContract.Fingerprint("paragraph", model.Text),
            ExactTextAnchorContract.Fingerprint("prefix", ScalarSlice(model.Text, prefixStart, start - prefixStart)),
            ExactTextAnchorContract.Fingerprint("suffix", ScalarSlice(model.Text, end, suffixLength)),
            spans);
        if (!string.Equals(actualSha, ComputeSha(sourceDocxPath), StringComparison.Ordinal))
            return Task.FromResult(ExactTextTargetResult.Stale("source-changed-during-inspection"));
        return Task.FromResult(new ExactTextTargetResult(ExactTextTargetStatus.Exact, null, anchor, spans));
    }

    public async Task<ExactTextTargetResult> ResolveAsync(
        string sourceDocxPath,
        Guid currentDocumentVersionId,
        ExactTextAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(anchor.ContractVersion, ExactTextAnchorContract.ContractVersion, StringComparison.Ordinal)
            || !string.Equals(anchor.TextModelVersion, ExactTextAnchorContract.TextModelVersion, StringComparison.Ordinal))
            return ExactTextTargetResult.Unsupported("anchor-contract-unsupported");
        if (anchor.DocumentVersionId != currentDocumentVersionId)
            return ExactTextTargetResult.Stale("document-version-mismatch");

        var rebuilt = await BuildAsync(sourceDocxPath, currentDocumentVersionId, anchor.SourceSha256,
            anchor.ParagraphLocation.ParagraphIndex ?? -1, anchor.Start, anchor.Length, cancellationToken);
        if (rebuilt.Status != ExactTextTargetStatus.Exact || rebuilt.Anchor is null) return rebuilt;
        var candidate = rebuilt.Anchor;
        if (!string.Equals(candidate.ParagraphLocation.ToCompactString(), anchor.ParagraphLocation.ToCompactString(), StringComparison.Ordinal))
            return ExactTextTargetResult.Stale("paragraph-location-mismatch");
        if (!string.Equals(candidate.ParagraphFingerprint, anchor.ParagraphFingerprint, StringComparison.Ordinal))
            return ExactTextTargetResult.Stale("paragraph-fingerprint-mismatch");
        if (!string.Equals(candidate.TargetFingerprint, anchor.TargetFingerprint, StringComparison.Ordinal))
            return ExactTextTargetResult.Stale("target-fingerprint-mismatch");
        if (!string.Equals(candidate.PrefixFingerprint, anchor.PrefixFingerprint, StringComparison.Ordinal)
            || !string.Equals(candidate.SuffixFingerprint, anchor.SuffixFingerprint, StringComparison.Ordinal))
            return ExactTextTargetResult.Stale("context-fingerprint-mismatch");
        if (!candidate.Spans.SequenceEqual(anchor.Spans))
            return ExactTextTargetResult.Stale("source-span-mismatch");
        return new ExactTextTargetResult(ExactTextTargetStatus.Exact, null, anchor, candidate.Spans);
    }

    public async Task<ExactTextTransientExcerpt> MaterializeExcerptAsync(
        string sourceDocxPath,
        Guid currentDocumentVersionId,
        ExactTextAnchor anchor,
        int maximumTargetScalars,
        int maximumContextScalars,
        CancellationToken cancellationToken = default)
    {
        if (maximumTargetScalars <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTargetScalars));
        if (maximumContextScalars < 0) throw new ArgumentOutOfRangeException(nameof(maximumContextScalars));
        var resolved = await ResolveAsync(sourceDocxPath, currentDocumentVersionId, anchor, cancellationToken);
        if (resolved.Status != ExactTextTargetStatus.Exact)
            return new(resolved.Status, resolved.SafeReason, null, null, false, false);
        if (anchor.Length > maximumTargetScalars)
            return new(ExactTextTargetStatus.Unsupported, "target-excerpt-too-large", null, null, false, false);

        using var document = WordprocessingDocument.Open(sourceDocxPath, false, new OpenSettings { AutoSave = false });
        var paragraph = LocateParagraph(document, anchor.ParagraphLocation.ParagraphIndex ?? -1);
        if (paragraph is null)
            return new(ExactTextTargetStatus.Stale, "paragraph-location-missing", null, null, false, false);
        var model = BuildParagraphModel(paragraph.Source, paragraph.Location);
        var prefixBudget = maximumContextScalars / 2;
        var suffixBudget = maximumContextScalars - prefixBudget;
        var prefixLength = Math.Min(anchor.Start, prefixBudget);
        var targetEnd = anchor.Start + anchor.Length;
        var suffixAvailable = model.ScalarLength - targetEnd;
        var suffixLength = Math.Min(suffixAvailable, suffixBudget);
        var target = ScalarSlice(model.Text, anchor.Start, anchor.Length);
        var context = ScalarSlice(model.Text, anchor.Start - prefixLength,
            prefixLength + anchor.Length + suffixLength);
        if (!string.Equals(anchor.SourceSha256, ComputeSha(sourceDocxPath), StringComparison.Ordinal))
            return new(ExactTextTargetStatus.Stale, "source-changed-during-inspection", null, null, false, false);
        return new(ExactTextTargetStatus.Exact, null, target, context,
            anchor.Start > prefixLength, suffixAvailable > suffixLength);
    }

    private static LocatedParagraph? LocateParagraph(WordprocessingDocument document, int requestedIndex)
    {
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null || requestedIndex < 0) return null;
        var paragraphIndex = 0;
        var sectionIndex = 0;
        var tableIndex = 0;
        var bodyIndex = 0;
        foreach (var element in body.Elements())
        {
            if (element is Paragraph direct)
            {
                var location = new DocumentElementLocation(DocumentPartKind.MainDocument, "/word/document.xml",
                    sectionIndex, bodyIndex, paragraphIndex, ElementKind: DocumentElementKind.Paragraph);
                if (paragraphIndex == requestedIndex) return new(direct, location);
                paragraphIndex++;
                if (direct.ParagraphProperties?.SectionProperties is not null) sectionIndex++;
            }
            else if (element is Table table)
            {
                var rowIndex = 0;
                foreach (var row in table.Elements<TableRow>())
                {
                    var cellIndex = 0;
                    foreach (var cell in row.Elements<TableCell>())
                    {
                        foreach (var nested in cell.Elements<Paragraph>())
                        {
                            var location = new DocumentElementLocation(DocumentPartKind.MainDocument, "/word/document.xml",
                                sectionIndex, bodyIndex, paragraphIndex, TableIndex: tableIndex, RowIndex: rowIndex,
                                CellIndex: cellIndex, ElementKind: DocumentElementKind.Paragraph);
                            if (paragraphIndex == requestedIndex) return new(nested, location);
                            paragraphIndex++;
                        }
                        cellIndex++;
                    }
                    rowIndex++;
                }
                tableIndex++;
            }
            bodyIndex++;
        }
        return null;
    }

    private static ParagraphModel BuildParagraphModel(Paragraph paragraph, DocumentElementLocation location)
    {
        var text = new StringBuilder();
        var segments = new List<ModelSegment>();
        var safe = new HashSet<(int, int)>();
        var unsafeBoundaries = new List<int>();
        var fieldResults = new Stack<bool>();
        var runIndex = 0;
        foreach (var run in paragraph.Descendants<Run>())
        {
            var unsupportedRun = HasUnsupportedAncestor(run);
            if (unsupportedRun) unsafeBoundaries.Add(ScalarCount(text.ToString()));
            var nodeIndex = 0;
            foreach (var child in run.ChildElements)
            {
                if (child is FieldChar field)
                {
                    unsafeBoundaries.Add(ScalarCount(text.ToString()));
                    var kind = field.FieldCharType?.Value;
                    if (kind == FieldCharValues.Begin) fieldResults.Push(false);
                    else if (kind == FieldCharValues.Separate && fieldResults.Count > 0)
                    {
                        fieldResults.Pop();
                        fieldResults.Push(true);
                    }
                    else if (kind == FieldCharValues.End && fieldResults.Count > 0) fieldResults.Pop();
                    nodeIndex++;
                    continue;
                }
                if (child is FieldCode)
                {
                    unsafeBoundaries.Add(ScalarCount(text.ToString()));
                    nodeIndex++;
                    continue;
                }

                var value = CanonicalValue(child);
                if (value is not null)
                {
                    var start = ScalarCount(text.ToString());
                    text.Append(value);
                    var count = ScalarCount(value);
                    var isSafe = !unsupportedRun && fieldResults.Count == 0 && !run.Ancestors<SimpleField>().Any();
                    var segment = new ModelSegment(runIndex, nodeIndex, NodeKind(child), start, count,
                        IsHyperlink: run.Ancestors<Hyperlink>().Any(), IsBold: IsOn(run.RunProperties?.Bold),
                        IsItalic: IsOn(run.RunProperties?.Italic));
                    segments.Add(segment);
                    if (isSafe) safe.Add((runIndex, nodeIndex));
                }
                else if (child is Drawing || child.LocalName is "pict" or "object" or "sym")
                {
                    unsafeBoundaries.Add(ScalarCount(text.ToString()));
                }
                nodeIndex++;
            }
            runIndex++;
        }
        return new(text.ToString(), ScalarCount(text.ToString()), segments, safe, unsafeBoundaries, location);
    }

    private static bool HasUnsupportedAncestor(Run run) => IsOn(run.RunProperties?.Vanish)
        || run.Ancestors().Any(value => value is InsertedRun or DeletedRun
            || value.LocalName is "moveFrom" or "moveTo" or "txbxContent" or "customXml" or "smartTag" or "sdt");

    private static string? CanonicalValue(OpenXmlElement child) => child switch
    {
        Text value => value.Text,
        TabChar => "\t",
        CarriageReturn => "\n",
        Break value when value.Type?.Value == BreakValues.Page => "\f",
        Break value when value.Type?.Value == BreakValues.Column => "\v",
        Break => "\n",
        SoftHyphen => "\u00ad",
        NoBreakHyphen => "\u2011",
        _ => null
    };

    private static string NodeKind(OpenXmlElement child) => child switch
    {
        Text => "text",
        TabChar => "tab",
        CarriageReturn => "line-break",
        Break value when value.Type?.Value == BreakValues.Page => "page-break",
        Break value when value.Type?.Value == BreakValues.Column => "column-break",
        Break => "line-break",
        SoftHyphen => "soft-hyphen",
        NoBreakHyphen => "nonbreaking-hyphen",
        _ => "unsupported"
    };

    private static bool IsOn(OnOffType? value) => value is not null && value.Val?.Value != false;

    private static ExactTextSourceSpan Slice(ModelSegment value, int targetStart, int targetEnd)
    {
        var intersectionStart = Math.Max(value.CanonicalStart, targetStart);
        var intersectionEnd = Math.Min(value.CanonicalStart + value.CanonicalLength, targetEnd);
        return new(value.RunIndex, value.NodeIndex, value.NodeKind, intersectionStart,
            intersectionEnd - intersectionStart, intersectionStart - value.CanonicalStart,
            intersectionEnd - intersectionStart, value.IsHyperlink, value.IsBold, value.IsItalic);
    }

    private static int ScalarCount(string value) => value.EnumerateRunes().Count();
    private static string ScalarSlice(string value, int start, int length) =>
        string.Concat(value.EnumerateRunes().Skip(start).Take(length).Select(rune => rune.ToString()));

    private static string ComputeSha(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string NormalizeSha(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(value => !Uri.IsHexDigit(value)))
            throw new ArgumentException("Source SHA-256 is invalid.", nameof(value));
        return normalized;
    }

    private sealed record LocatedParagraph(Paragraph Source, DocumentElementLocation Location);
    private sealed record ModelSegment(int RunIndex, int NodeIndex, string NodeKind, int CanonicalStart,
        int CanonicalLength, bool IsHyperlink, bool IsBold, bool IsItalic);
    private sealed record ParagraphModel(string Text, int ScalarLength, IReadOnlyList<ModelSegment> Segments,
        IReadOnlySet<(int, int)> SafeSegmentKeys, IReadOnlyList<int> UnsafeBoundaries, DocumentElementLocation Location);
}

public static class ExactTextAnchorContract
{
    public const string ContractVersion = "text-anchor/1.0";
    public const string TextModelVersion = "wordprocessingml-visible-text/scalar-none/1.0";
    public const int ContextScalarLength = 16;

    public static string Fingerprint(string domain, string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\n" + value)));

    public static string Serialize(ExactTextAnchor anchor)
    {
        var location = anchor.ParagraphLocation;
        var lines = new List<string>
        {
            anchor.ContractVersion, anchor.TextModelVersion, anchor.DocumentVersionId.ToString("D"), anchor.SourceSha256,
            location.PartKind.ToString(), location.PartUri, Number(location.SectionIndex), Number(location.BodyElementIndex),
            Number(location.ParagraphIndex), Number(location.TableIndex), Number(location.RowIndex), Number(location.CellIndex),
            anchor.Start.ToString(CultureInfo.InvariantCulture), anchor.Length.ToString(CultureInfo.InvariantCulture),
            anchor.TargetFingerprint, anchor.ParagraphFingerprint, anchor.PrefixFingerprint, anchor.SuffixFingerprint
        };
        lines.AddRange(anchor.Spans.Select(span => string.Join('|', span.RunIndex, span.NodeIndex, span.NodeKind,
            span.CanonicalStart, span.CanonicalLength, span.SourceStart, span.SourceLength,
            span.IsHyperlink ? 1 : 0, span.IsBold ? 1 : 0, span.IsItalic ? 1 : 0)));
        return string.Join('\n', lines);
    }

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
