using System.Text.RegularExpressions;
using Ppki.Application;

namespace Ppki.Infrastructure;

public sealed class StorageObjectPathBuilder : IStorageObjectPathBuilder
{
    public const string OriginalBucket = "documents-original";
    public const string VersionBucket = "documents-versions";
    public const string ReportBucket = "audit-reports";

    private static readonly Regex OriginalPath = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/original\\.docx$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPath = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/document\\.docx$", RegexOptions.CultureInvariant);
    private static readonly Regex ReportPath = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.(pdf|json)$", RegexOptions.CultureInvariant);

    public string BuildOriginalPath(Guid ownerUserId, Guid documentId, Guid documentVersionId) =>
        $"{Canonical(ownerUserId)}/{Canonical(documentId)}/{Canonical(documentVersionId)}/original.docx";

    public string BuildVersionPath(Guid ownerUserId, Guid documentId, Guid documentVersionId) =>
        $"{Canonical(ownerUserId)}/{Canonical(documentId)}/{Canonical(documentVersionId)}/document.docx";

    public string BuildAuditReportPath(Guid ownerUserId, Guid documentId, Guid auditJobId, string extension)
    {
        var normalizedExtension = NormalizeReportExtension(extension);
        return $"{Canonical(ownerUserId)}/{Canonical(documentId)}/{Canonical(auditJobId)}.{normalizedExtension}";
    }

    public string BuildDocumentPreviewPath(Guid ownerUserId, Guid documentId, Guid renderJobId) =>
        $"{Canonical(ownerUserId)}/{Canonical(documentId)}/{Canonical(renderJobId)}.pdf";

    public void ValidateStoredPath(string bucket, string objectPath)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectPath)
            || objectPath.StartsWith("/", StringComparison.Ordinal)
            || objectPath.Contains('\\')
            || objectPath.Contains("..", StringComparison.Ordinal)
            || objectPath.Contains("://", StringComparison.Ordinal)
            || objectPath.Contains('?')
            || objectPath.Contains('#')
            || objectPath.Split('/').Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("Storage object path is invalid.", nameof(objectPath));
        }

        var valid = bucket switch
        {
            OriginalBucket => OriginalPath.IsMatch(objectPath),
            VersionBucket => VersionPath.IsMatch(objectPath),
            ReportBucket => ReportPath.IsMatch(objectPath),
            _ => false
        };
        if (!valid) throw new ArgumentException("Storage object path does not match its bucket contract.", nameof(objectPath));
    }

    private static string Canonical(Guid value) => value.ToString("D").ToLowerInvariant();

    private static string NormalizeReportExtension(string extension)
    {
        var normalized = extension.Trim().TrimStart('.').ToLowerInvariant();
        return normalized is "pdf" or "json"
            ? normalized
            : throw new ArgumentException("Audit report extension is not supported.", nameof(extension));
    }
}
