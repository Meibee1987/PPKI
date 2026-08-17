using System.Security.Cryptography;
using System.Text;
using Ppki.Domain;

namespace Ppki.Application;

public static class CanonicalDocumentRenderContract
{
    public const string RendererId = "gotenberg-libreoffice";
    public const string RendererVersion = "8.34.0+libreoffice-26.2.4.2";
    public const string RendererContractVersion = "docx-pdf/1.0";
    public const string RendererImageDigest = "sha256:3c23aeb3a027a63d7c71745fc9d83724bd58cf9dfa470396ac82c0896028db2a";
    public const string FontProfileVersion = "ppki-liberation-noto/1.0";
    public const string PageMapSchemaVersion = "page-map/1.0";

    public static string Identity(Guid documentVersionId, string sourceSha256) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            documentVersionId.ToString("D"), NormalizeSha(sourceSha256), RendererId,
            RendererVersion, RendererContractVersion, FontProfileVersion))));

    public static DocumentRenderJob CreateJob(Guid documentVersionId, string sourceSha256) => new()
    {
        DocumentVersionId = documentVersionId,
        SourceSha256 = NormalizeSha(sourceSha256),
        RendererId = RendererId,
        RendererVersion = RendererVersion,
        RendererContractVersion = RendererContractVersion,
        FontProfileVersion = FontProfileVersion,
        PageMapSchemaVersion = PageMapSchemaVersion,
        RenderIdentity = Identity(documentVersionId, sourceSha256)
    };

    private static string NormalizeSha(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Source SHA-256 is invalid.", nameof(value));
        return normalized;
    }
}

public sealed record PageMapRenderEntry(
    string StructuralLocation,
    int? SectionIndex,
    int? BodyElementIndex,
    int? ParagraphIndex,
    int? RunIndex,
    int? TableIndex,
    int? RowIndex,
    int? CellIndex,
    PageMapConfidence Confidence,
    int? PageNumber,
    string? SafeReason);

public sealed record CanonicalDocumentRenderResult(
    byte[] PdfBytes,
    string PdfSha256,
    int PageCount,
    string SourceTextFingerprint,
    IReadOnlyList<PageMapRenderEntry> Entries);

public interface ICanonicalDocumentRenderer
{
    Task<CanonicalDocumentRenderResult> RenderAsync(string sourceDocxPath, CancellationToken cancellationToken);
}

public sealed record DocumentRenderStateDto(
    string State,
    int? PageCount,
    string RendererVersion,
    string RendererContractVersion,
    string FontProfileVersion,
    string PageMapVersion,
    string? SafeFailureCode,
    bool PreviewAvailable);

public sealed record FindingPageLocationDto(int? PageNumber, string Confidence, string? State = null);
