using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;
using Ppki.RenderEngine;

namespace Ppki.Worker;

public sealed class DocumentRenderProcessor(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IFileStorage storage,
    IStorageObjectPathBuilder paths,
    IOptions<SupabaseOptions> supabase,
    ICanonicalDocumentRenderer renderer)
{
    private const string PdfMime = "application/pdf";

    public async Task ProcessAsync(DocumentRenderClaim claim, CancellationToken cancellationToken)
    {
        var source = await LoadAsync(claim, cancellationToken)
            ?? throw new DocumentRenderException("render-lease-lost", retryable: true);
        ValidateContract(source);
        string? sourcePath = null;
        try
        {
            sourcePath = await storage.MaterializeToTempFileAsync(
                source.StorageBucket, source.StorageKey, cancellationToken);
            if (!StringComparer.Ordinal.Equals(await HashFileAsync(sourcePath, cancellationToken), source.SourceSha256))
                throw new DocumentRenderException("render-source-hash-mismatch", retryable: false);
            var result = await renderer.RenderAsync(sourcePath, cancellationToken);
            await EnsureActiveAsync(claim, cancellationToken);
            var objectPath = paths.BuildDocumentPreviewPath(source.OwnerUserId, source.DocumentId, claim.JobId);
            var stored = await PublishAsync(result, source.ReportBucket, objectPath, cancellationToken);
            await CompleteAsync(claim, source, result, stored, cancellationToken);
        }
        catch (FileStorageException exception) when (exception.Kind == FileStorageFailureKind.NotFound)
        { throw new DocumentRenderException("render-source-storage-missing", retryable: false, exception); }
        catch (FileStorageException exception) when (exception.Kind == FileStorageFailureKind.Transient)
        { throw new DocumentRenderException("render-storage-transient", retryable: true, exception); }
        catch (FileStorageException exception)
        { throw new DocumentRenderException("render-storage-terminal", retryable: false, exception); }
        finally
        {
            if (sourcePath is not null)
            {
                try { File.Delete(sourcePath); } catch { }
            }
        }
    }

    private async Task<StoredFile> PublishAsync(
        CanonicalDocumentRenderResult result,
        string bucket,
        string objectPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new MemoryStream(result.PdfBytes, writable: false);
            return await storage.SaveAsync(stream, "preview.pdf", PdfMime, bucket, objectPath, cancellationToken);
        }
        catch (FileStorageException exception) when (exception.Kind == FileStorageFailureKind.Conflict)
        {
            var existing = await storage.ReadBytesAsync(bucket, objectPath, 50L * 1024 * 1024, cancellationToken);
            var sha = Convert.ToHexStringLower(SHA256.HashData(existing));
            if (!StringComparer.Ordinal.Equals(sha, result.PdfSha256) || existing.LongLength != result.PdfBytes.LongLength)
                throw new DocumentRenderException("render-artifact-object-conflict", retryable: false, exception);
            return new(bucket, objectPath, "preview.pdf", PdfMime, existing.LongLength, sha);
        }
    }

    private async Task CompleteAsync(
        DocumentRenderClaim claim,
        SourceRow source,
        CanonicalDocumentRenderResult result,
        StoredFile stored,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.DocumentRenderJobs.FromSqlInterpolated(
            $"select * from public.document_render_jobs where id = {claim.JobId} for update")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DocumentRenderException("render-job-missing", retryable: false);
        if (job.State != DocumentRenderState.Processing || job.ClaimToken != claim.Token
            || job.LeaseExpiresAt <= DateTimeOffset.UtcNow)
            throw new DocumentRenderException("render-lease-lost", retryable: true);
        if (!StringComparer.Ordinal.Equals(stored.Sha256, result.PdfSha256))
            throw new DocumentRenderException("render-artifact-hash-mismatch", retryable: false);
        var artifact = new DocumentRenderArtifact
        {
            RenderJobId = job.Id,
            DocumentVersionId = job.DocumentVersionId,
            StorageBucket = stored.StorageBucket,
            StorageKey = stored.StorageKey,
            PdfSha256 = stored.Sha256,
            SizeBytes = stored.SizeBytes,
            PageCount = result.PageCount,
            RendererId = job.RendererId,
            RendererVersion = job.RendererVersion,
            RendererContractVersion = job.RendererContractVersion,
            FontProfileVersion = job.FontProfileVersion,
            PageMapSchemaVersion = job.PageMapSchemaVersion,
            SourceSha256 = job.SourceSha256,
            SourceTextFingerprint = result.SourceTextFingerprint
        };
        foreach (var entry in result.Entries)
            artifact.PageMapEntries.Add(new DocumentPageMapEntry
            {
                StructuralLocation = entry.StructuralLocation,
                SectionIndex = entry.SectionIndex,
                BodyElementIndex = entry.BodyElementIndex,
                ParagraphIndex = entry.ParagraphIndex,
                RunIndex = entry.RunIndex,
                TableIndex = entry.TableIndex,
                RowIndex = entry.RowIndex,
                CellIndex = entry.CellIndex,
                Confidence = entry.Confidence,
                PageNumber = entry.PageNumber,
                SafeReason = entry.SafeReason
            });
        db.DocumentRenderArtifacts.Add(artifact);
        await db.SaveChangesAsync(cancellationToken);
        job.State = DocumentRenderState.Completed;
        job.ClaimToken = null;
        job.LeaseExpiresAt = null;
        job.NextAttemptAt = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SourceRow?> LoadAsync(DocumentRenderClaim claim, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.DocumentRenderJobs.AsNoTracking()
            .Where(value => value.Id == claim.JobId && value.State == DocumentRenderState.Processing
                && value.ClaimToken == claim.Token && value.LeaseExpiresAt > DateTimeOffset.UtcNow)
            .Select(value => new SourceRow(value.DocumentVersionId, value.SourceSha256,
                value.RendererId, value.RendererVersion, value.RendererContractVersion,
                value.FontProfileVersion, value.PageMapSchemaVersion,
                value.DocumentVersion!.StorageBucket, value.DocumentVersion.StorageKey,
                value.DocumentVersion.DocumentId, value.DocumentVersion.Document!.OwnerUserId,
                supabase.Value.Storage.ReportBucket))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task EnsureActiveAsync(DocumentRenderClaim claim, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.DocumentRenderJobs.AsNoTracking().AnyAsync(value => value.Id == claim.JobId
            && value.State == DocumentRenderState.Processing && value.ClaimToken == claim.Token
            && value.LeaseExpiresAt > DateTimeOffset.UtcNow, cancellationToken))
            throw new DocumentRenderException("render-lease-lost", retryable: true);
    }

    private static void ValidateContract(SourceRow source)
    {
        if (source.RendererId != CanonicalDocumentRenderContract.RendererId
            || source.RendererVersion != CanonicalDocumentRenderContract.RendererVersion
            || source.RendererContractVersion != CanonicalDocumentRenderContract.RendererContractVersion
            || source.FontProfileVersion != CanonicalDocumentRenderContract.FontProfileVersion
            || source.PageMapSchemaVersion != CanonicalDocumentRenderContract.PageMapSchemaVersion)
            throw new DocumentRenderException("renderer-contract-unavailable", retryable: false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed record SourceRow(
        Guid DocumentVersionId, string SourceSha256, string RendererId, string RendererVersion,
        string RendererContractVersion, string FontProfileVersion, string PageMapSchemaVersion,
        string StorageBucket, string StorageKey, Guid DocumentId, Guid OwnerUserId, string ReportBucket);
}
