using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ppki.Application;

namespace Ppki.Infrastructure;

public sealed class StructuralFindingExcerptService(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IInternalAdminAuthorizationService authorization,
    IFileStorage storage,
    IStorageObjectPathBuilder pathBuilder,
    IOptions<SupabaseOptions> supabase) : IStructuralFindingExcerptService
{
    public async Task<StructuralFindingExcerptDto?> MaterializeAsync(Guid auditId, Guid findingId,
        Guid actorUserId, CancellationToken cancellationToken)
    {
        await authorization.RequirePpkiAdminAsync(actorUserId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.AuditFindings.AsNoTracking()
            .Where(value => value.Id == findingId && value.AuditJobId == auditId)
            .Select(value => new
            {
                FindingId = value.Id,
                AuditId = value.AuditJobId,
                DocumentVersionId = value.AuditJob!.DocumentVersionId,
                LocationJson = value.LocationJson,
                value.AuditJob.DocumentVersion!.StorageBucket,
                value.AuditJob.DocumentVersion.StorageKey,
                value.AuditJob.DocumentVersion.Sha256,
                value.AuditJob.DocumentVersion.ParentVersionId,
                value.AuditJob.DocumentVersion.DocumentId,
                OwnerUserId = value.AuditJob.DocumentVersion.Document!.OwnerUserId
            }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        var pageLocation = await PageLocationAsync(db, row.DocumentVersionId, row.LocationJson, cancellationToken);
        var original = row.ParentVersionId is null;
        var expectedBucket = original ? supabase.Value.Storage.OriginalBucket : supabase.Value.Storage.VersionBucket;
        var expectedPath = original
            ? pathBuilder.BuildOriginalPath(row.OwnerUserId, row.DocumentId, row.DocumentVersionId)
            : pathBuilder.BuildVersionPath(row.OwnerUserId, row.DocumentId, row.DocumentVersionId);
        if (row.StorageBucket != expectedBucket || row.StorageKey != expectedPath)
            return Unavailable(row.FindingId, row.DocumentVersionId, pageLocation);

        string? path = null;
        try
        {
            path = await storage.MaterializeToTempFileAsync(row.StorageBucket, row.StorageKey, cancellationToken);
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!StringComparer.Ordinal.Equals(hash, row.Sha256))
                return Unavailable(row.FindingId, row.DocumentVersionId, pageLocation);

            var materialized = await StructuralFindingExcerptMaterializer.MaterializeAsync(
                path, row.LocationJson, cancellationToken);
            return new(row.FindingId, row.DocumentVersionId, materialized.Status,
                materialized.TargetType, materialized.Excerpt, materialized.TargetText, pageLocation);
        }
        catch (Exception exception) when (exception is FileStorageException or IOException
            or UnauthorizedAccessException)
        {
            return Unavailable(row.FindingId, row.DocumentVersionId, pageLocation);
        }
        finally
        {
            if (path is not null) try { File.Delete(path); } catch { }
        }
    }

    private static async Task<FindingPageLocationDto> PageLocationAsync(PpkiDbContext db,
        Guid documentVersionId, string locationJson, CancellationToken cancellationToken)
    {
        var location = Location(locationJson);
        if ((location.IsSection && location.BodyElementIndex is null)
            || (!location.IsSection && location.ParagraphIndex is null))
            return new(null, "Unavailable", null);
        var entries = db.DocumentPageMapEntries.AsNoTracking()
            .Where(value => value.RenderArtifact!.DocumentVersionId == documentVersionId
                && value.RenderArtifact.RenderJob!.State == Ppki.Domain.DocumentRenderState.Completed);
        entries = location.IsSection
            ? entries.Where(value => value.BodyElementIndex == location.BodyElementIndex)
            : entries.Where(value => value.ParagraphIndex == location.ParagraphIndex
                && (location.RunIndex == null || value.RunIndex == location.RunIndex));
        var entry = await entries
            .OrderBy(value => value.RunIndex == null ? 0 : 1)
            .Select(value => new { value.PageNumber, value.Confidence })
            .FirstOrDefaultAsync(cancellationToken);
        return entry is null ? new(null, "Unavailable", null)
            : new(entry.PageNumber, entry.Confidence.ToString(), "Completed");
    }

    private static StructuralLocation Location(string locationJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(locationJson);
            var root = document.RootElement;
            var compact = Text(root, "CompactLocation", "compactLocation");
            return new(Integer(root, "ParagraphIndex", "paragraphIndex"),
                Integer(root, "RunIndex", "runIndex"), Integer(root, "BodyElementIndex", "bodyElementIndex"),
                compact?.Split('/').Any(value => value == "kind:section") == true);
        }
        catch (System.Text.Json.JsonException) { }
        return new(null, null, null, false);
    }

    private static int? Integer(System.Text.Json.JsonElement root, string first, string second) =>
        (root.TryGetProperty(first, out var value) || root.TryGetProperty(second, out value))
        && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var parsed)
        && parsed >= 0 ? parsed : null;

    private static string? Text(System.Text.Json.JsonElement root, string first, string second) =>
        (root.TryGetProperty(first, out var value) || root.TryGetProperty(second, out value))
        && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

    private static StructuralFindingExcerptDto Unavailable(Guid findingId, Guid documentVersionId,
        FindingPageLocationDto pageLocation) =>
        new(findingId, documentVersionId, "Unavailable", "Other", null, null, pageLocation);

    private sealed record StructuralLocation(int? ParagraphIndex, int? RunIndex,
        int? BodyElementIndex, bool IsSection);
}
