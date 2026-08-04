using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;

namespace Ppki.Worker;

public sealed class FixExecutionProcessor(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IFileStorage storage,
    IStorageObjectPathBuilder pathBuilder,
    IOptions<SupabaseOptions> supabase,
    IDocxParser parser,
    FixApplyCapabilityRegistry capabilities)
{
    private const long MaximumBytes = 50L * 1024 * 1024;
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public async Task ProcessAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var source = await LoadAsync(executionId, cancellationToken)
            ?? throw new FixExecutionException("fix-execution-not-found");
        var approved = ApprovedFixExecutionPlanSerializer.Deserialize(source.ApprovedPlanSnapshotJson);
        ValidateApprovedSnapshot(source, approved);

        string? materialized = null;
        string? workspace = null;
        string? existingResult = null;
        StoredFile? uploaded = null;
        var ownsUploadedObject = false;
        try
        {
            materialized = await storage.MaterializeToTempFileAsync(source.StorageBucket, source.StorageKey, cancellationToken);
            var sourceInfo = new FileInfo(materialized);
            if (!sourceInfo.Exists || sourceInfo.Length is <= 0 or > MaximumBytes)
                throw new FixExecutionException("fix-execution-source-size-invalid");
            if (!string.Equals(await Sha256Async(materialized, cancellationToken), source.SourceSha256, StringComparison.Ordinal))
                throw new FixExecutionException("source-hash-mismatch");

            workspace = Path.Combine(Path.GetTempPath(), $"ppki-fix-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspace);
            var working = Path.Combine(workspace, "working.docx");
            File.Copy(materialized, working, false);
            var packageSnapshot = DocxPackageIntegrity.Capture(working);
            var before = await parser.ParseAsync(working, cancellationToken);
            if (before.ParserSchemaVersion != OpenXmlDocxParser.SchemaVersion)
                throw new FixExecutionException("fix-execution-parser-schema-mismatch");

            var changed = 0;
            foreach (var operation in approved.Preview.Operations.OrderBy(value => value.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!capabilities.TryGet(operation, out var provider))
                    throw new FixExecutionException("fix-execution-apply-capability-unavailable");
                if (operation.SourceFindingIds.Count != 1)
                    throw new FixExecutionException("fix-operation-source-snapshot-invalid");
                var finding = approved.Source.Findings.SingleOrDefault(value => value.FindingId == operation.SourceFindingIds[0])
                    ?? throw new FixExecutionException("fix-operation-source-snapshot-invalid");
                if (await provider.ApplyAsync(new(working, before, finding, operation), cancellationToken) == FixApplyOutcome.Changed)
                    changed++;
            }

            var workingInfo = new FileInfo(working);
            if (!workingInfo.Exists || workingInfo.Length is <= 0 or > MaximumBytes)
                throw new FixExecutionException("fix-execution-result-size-invalid");
            DocxPackageIntegrity.ValidateMutation(packageSnapshot, working);
            var after = await parser.ParseAsync(working, cancellationToken);
            ValidatePostconditions(before, after, approved.Preview.Operations);
            var resultId = executionId;
            var objectPath = pathBuilder.BuildVersionPath(source.OwnerUserId, source.DocumentId, resultId);
            var outputSha = await Sha256Async(working, cancellationToken);
            if (changed == 0 || string.Equals(outputSha, source.SourceSha256, StringComparison.Ordinal))
            {
                await CompleteNoChangeAsync(executionId, approved.Preview.Operations.Count, cancellationToken);
                return;
            }
            try
            {
                existingResult = await storage.MaterializeToTempFileAsync(
                    supabase.Value.Storage.VersionBucket, objectPath, cancellationToken);
            }
            catch (InvalidOperationException) { }
            if (existingResult is not null)
            {
                var existingInfo = new FileInfo(existingResult);
                if (!existingInfo.Exists || existingInfo.Length != new FileInfo(working).Length
                    || !string.Equals(await Sha256Async(existingResult, cancellationToken), outputSha, StringComparison.Ordinal))
                    throw new FixExecutionException("fix-execution-existing-result-mismatch");
                uploaded = new(supabase.Value.Storage.VersionBucket, objectPath, source.OriginalFilename,
                    DocxMime, existingInfo.Length, outputSha);
            }
            else
            {
                await using var stream = File.OpenRead(working);
                uploaded = await storage.SaveAsync(stream, source.OriginalFilename, DocxMime,
                    supabase.Value.Storage.VersionBucket, objectPath, cancellationToken);
                ownsUploadedObject = true;
            }
            if (uploaded.SizeBytes is <= 0 or > MaximumBytes)
                throw new FixExecutionException("fix-execution-result-size-invalid");
            await CompleteWithVersion(source, uploaded, resultId, approved.Preview.Operations.Count, cancellationToken);
            uploaded = null;
        }
        finally
        {
            if (uploaded is not null && ownsUploadedObject)
                try { await storage.DeleteAsync(uploaded.StorageBucket, uploaded.StorageKey, CancellationToken.None); } catch { }
            if (materialized is not null) try { File.Delete(materialized); } catch { }
            if (existingResult is not null) try { File.Delete(existingResult); } catch { }
            if (workspace is not null) try { Directory.Delete(workspace, true); } catch { }
        }
    }

    private async Task CompleteWithVersion(SourceRow source, StoredFile stored, Guid resultId, int operationCount,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var document = await db.Documents.FromSqlInterpolated($"select * from public.documents where id = {source.DocumentId} for update")
            .SingleAsync(cancellationToken);
        var job = await db.FixExecutionJobs.SingleAsync(value => value.Id == source.ExecutionId, cancellationToken);
        if (job.State != FixExecutionState.Processing)
            throw new FixExecutionException("fix-execution-state-conflict");
        var nextVersion = await db.DocumentVersions.Where(value => value.DocumentId == source.DocumentId)
            .MaxAsync(value => value.VersionNo, cancellationToken) + 1;
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = resultId, DocumentId = source.DocumentId, VersionNo = nextVersion,
            StorageBucket = stored.StorageBucket, StorageKey = stored.StorageKey,
            OriginalFilename = source.OriginalFilename, MimeType = stored.ContentType,
            SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256,
            CreatedByUserId = source.OwnerUserId, ParentVersionId = source.SourceVersionId
        });
        document.CurrentVersionNo = nextVersion;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        job.State = FixExecutionState.Completed;
        job.ResultDocumentVersionId = resultId;
        job.ResultSha256 = stored.Sha256;
        job.CompletedOperationCount = operationCount;
        job.LeaseExpiresAt = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteNoChangeAsync(Guid executionId, int operationCount, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.FixExecutionJobs.SingleAsync(value => value.Id == executionId, cancellationToken);
        if (job.State != FixExecutionState.Processing) throw new FixExecutionException("fix-execution-state-conflict");
        job.State = FixExecutionState.NoChange;
        job.CompletedOperationCount = operationCount;
        job.LeaseExpiresAt = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SourceRow?> LoadAsync(Guid executionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FixExecutionJobs.AsNoTracking()
            .Where(value => value.Id == executionId && value.State == FixExecutionState.Processing)
            .Select(value => new SourceRow(value.Id, value.ApprovedPlanSnapshotJson,
                value.SourceDocumentVersionId, value.SourceDocumentVersion!.Sha256,
                value.SourceDocumentVersion.StorageBucket, value.SourceDocumentVersion.StorageKey,
                value.SourceDocumentVersion.OriginalFilename, value.SourceDocumentVersion.DocumentId,
                value.SourceDocumentVersion.Document!.OwnerUserId, value.PlanHash, value.PlannerVersion))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static void ValidateApprovedSnapshot(SourceRow source, ApprovedFixExecutionPlan approved)
    {
        if (approved.Source.DocumentVersionId != source.SourceVersionId
            || approved.Source.SourceVersionSha256 != source.SourceSha256
            || approved.Preview.SourceDocumentVersionId != source.SourceVersionId
            || approved.Preview.PlanHash != source.PlanHash
            || approved.Preview.PlannerVersion != source.PlannerVersion
            || approved.Preview.State != FixPlanState.Ready
            || approved.Preview.Operations.Count == 0)
            throw new FixExecutionException("fix-execution-approved-snapshot-mismatch");
    }

    private static void ValidatePostconditions(ParsedDocument before, ParsedDocument after,
        IReadOnlyList<FixPlanOperation> operations)
    {
        if (after.ParserSchemaVersion != OpenXmlDocxParser.SchemaVersion
            || before.PackageType != after.PackageType
            || before.Counts.ExternalRelationships != after.Counts.ExternalRelationships
            || TextDigest(before) != TextDigest(after))
            throw new FixExecutionException("fix-execution-document-integrity-failed");
        foreach (var operation in operations)
        {
            var paragraph = after.Paragraphs.SingleOrDefault(value =>
                value.Location?.PartKind == DocumentPartKind.MainDocument
                && value.Location.BodyElementIndex == operation.Target.BodyElementIndex
                && value.Location.ParagraphIndex == operation.Target.ParagraphIndex);
            if (paragraph?.DirectAlignment != ParsedAlignment.Justified)
                throw new FixExecutionException("fix-operation-postcondition-failed");
        }
    }

    private static string TextDigest(ParsedDocument document)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var paragraph in document.Paragraphs)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(paragraph.Text);
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed record SourceRow(Guid ExecutionId, string ApprovedPlanSnapshotJson, Guid SourceVersionId,
        string SourceSha256, string StorageBucket, string StorageKey, string OriginalFilename,
        Guid DocumentId, Guid OwnerUserId, string PlanHash, string PlannerVersion);
}
