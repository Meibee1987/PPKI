using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using DocumentFormat.OpenXml.Packaging;
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
    FixApplyCapabilityRegistry capabilities,
    IRemediationFaultInjector faults)
{
    private const long MaximumBytes = 50L * 1024 * 1024;
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public async Task ProcessAsync(FixExecutionClaim claim, CancellationToken cancellationToken)
    {
        var source = await LoadAsync(claim, cancellationToken)
            ?? throw new FixExecutionException(FixFailureCategory.TransientInfrastructure, "worker-lease-lost");
        if (source.CurrentVersionNo != source.SourceVersionNo)
            throw new FixExecutionException(FixFailureCategory.Conflict, "fix-source-version-superseded");
        ApprovedFixExecutionPlan approved;
        try { approved = ApprovedFixExecutionPlanSerializer.Deserialize(source.ApprovedPlanSnapshotJson); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or FixExecutionException)
        { throw new FixExecutionException(FixFailureCategory.InvalidPlan, "approved-plan-invalid", exception); }
        ValidateApprovedSnapshot(source, approved);

        string? materialized = null;
        string? workspace = null;
        string? existingResult = null;
        StoredFile? uploaded = null;
        var ownsUploadedObject = false;
        try
        {
            await faults.CheckpointAsync(RemediationCheckpoint.BeforeSourceDownload, claim.ExecutionId,
                claim.AttemptNumber, cancellationToken);
            try { materialized = await storage.MaterializeToTempFileAsync(source.StorageBucket, source.StorageKey, cancellationToken); }
            catch (FileStorageException exception) { throw DownloadFailure(exception); }
            await faults.CheckpointAsync(RemediationCheckpoint.AfterSourceDownload, claim.ExecutionId,
                claim.AttemptNumber, cancellationToken);
            var sourceInfo = new FileInfo(materialized);
            if (!sourceInfo.Exists || sourceInfo.Length is <= 0 or > MaximumBytes)
                throw new FixExecutionException(FixFailureCategory.InvalidSource, "source-size-invalid");
            if (!string.Equals(await Sha256Async(materialized, cancellationToken), source.SourceSha256, StringComparison.Ordinal))
                throw new FixExecutionException("source-hash-mismatch");

            workspace = Path.Combine(Path.GetTempPath(), $"ppki-fix-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspace);
            var working = Path.Combine(workspace, "working.docx");
            File.Copy(materialized, working, false);
            DocxPackageIntegritySnapshot packageSnapshot;
            ParsedDocument before;
            try
            {
                packageSnapshot = DocxPackageIntegrity.Capture(working);
                before = await parser.ParseAsync(working, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { throw new FixExecutionException(FixFailureCategory.InvalidSource, "source-package-invalid", exception); }
            if (before.ParserSchemaVersion != OpenXmlDocxParser.SchemaVersion)
                throw new FixExecutionException("fix-execution-parser-schema-mismatch");

            var changed = 0;
            await faults.CheckpointAsync(RemediationCheckpoint.BeforeApply, claim.ExecutionId,
                claim.AttemptNumber, cancellationToken);
            using (var package = WordprocessingDocument.Open(working, true, new OpenSettings { AutoSave = false }))
            {
                foreach (var operation in approved.Preview.Operations.OrderBy(value => value.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!capabilities.TryGet(operation, out var provider))
                        throw new FixExecutionException(FixFailureCategory.CapabilityUnavailable, "fix-provider-version-unavailable");
                    if (operation.SourceFindingIds.Count != 1)
                        throw new FixExecutionException(FixFailureCategory.InvalidPlan, "approved-plan-operation-invalid");
                    var finding = approved.Source.Findings.SingleOrDefault(value => value.FindingId == operation.SourceFindingIds[0])
                        ?? throw new FixExecutionException(FixFailureCategory.InvalidPlan, "approved-plan-selection-invalid");
                    try
                    {
                        if (await provider.ApplyAsync(new(working, before, finding, operation, package), cancellationToken) == FixApplyOutcome.Changed)
                            changed++;
                    }
                    catch (FixExecutionException) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    { throw new FixExecutionException(FixFailureCategory.CapabilityUnavailable, "fix-provider-unavailable", exception); }
                }
                if (changed > 0) package.MainDocumentPart?.Document?.Save();
            }
            await faults.CheckpointAsync(RemediationCheckpoint.AfterApply, claim.ExecutionId,
                claim.AttemptNumber, cancellationToken);

            var workingInfo = new FileInfo(working);
            if (!workingInfo.Exists || workingInfo.Length is <= 0 or > MaximumBytes)
                throw new FixExecutionException("fix-execution-result-size-invalid");
            try
            {
                DocxPackageIntegrity.ValidateMutation(packageSnapshot, working);
                var after = await parser.ParseAsync(working, cancellationToken);
                ValidatePostconditions(before, after, approved.Preview.Operations);
            }
            catch (FixExecutionException) { throw; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { throw new FixExecutionException(FixFailureCategory.InvalidSource, "source-package-invalid", exception); }
            var resultId = claim.ExecutionId;
            var objectPath = pathBuilder.BuildVersionPath(source.OwnerUserId, source.DocumentId, resultId);
            var outputSha = await Sha256Async(working, cancellationToken);
            if (changed == 0 || string.Equals(outputSha, source.SourceSha256, StringComparison.Ordinal))
            {
                try { await CompleteNoChangeAsync(claim, source, approved.Preview.Operations.Count, cancellationToken); }
                catch (Exception exception) when (DatabaseTransient(exception))
                { throw new FixExecutionException(FixFailureCategory.TransientInfrastructure, "database-transient", exception); }
                return;
            }
            await EnsureActiveAsync(claim, source, cancellationToken);
            await faults.CheckpointAsync(RemediationCheckpoint.BeforeResultUpload, claim.ExecutionId,
                claim.AttemptNumber, cancellationToken);
            await using (var stream = File.OpenRead(working))
            {
                try
                {
                    uploaded = await storage.SaveAsync(stream, source.OriginalFilename, DocxMime,
                        supabase.Value.Storage.VersionBucket, objectPath, cancellationToken);
                    ownsUploadedObject = true;
                }
                catch (FileStorageException exception) when (exception.Kind == FileStorageFailureKind.Conflict) { }
                catch (FileStorageException exception)
                { throw UploadFailure(exception); }
            }
            if (uploaded is null)
            {
                try
                {
                    existingResult = await storage.MaterializeToTempFileAsync(
                        supabase.Value.Storage.VersionBucket, objectPath, cancellationToken);
                }
                catch (FileStorageException exception) { throw DownloadFailure(exception); }
                var existingInfo = new FileInfo(existingResult);
                if (!existingInfo.Exists || existingInfo.Length != new FileInfo(working).Length
                    || !string.Equals(await Sha256Async(existingResult, cancellationToken), outputSha, StringComparison.Ordinal))
                    throw new FixExecutionException(FixFailureCategory.Conflict, "fix-result-object-conflict");
                uploaded = new(supabase.Value.Storage.VersionBucket, objectPath, source.OriginalFilename,
                    DocxMime, existingInfo.Length, outputSha);
            }
            await faults.CheckpointAsync(RemediationCheckpoint.AfterResultUpload, claim.ExecutionId,
                claim.AttemptNumber, cancellationToken);
            if (uploaded.SizeBytes is <= 0 or > MaximumBytes)
                throw new FixExecutionException("fix-execution-result-size-invalid");
            if (!string.Equals(uploaded.Sha256, outputSha, StringComparison.Ordinal)
                || uploaded.SizeBytes != new FileInfo(working).Length)
                throw new FixExecutionException(FixFailureCategory.TerminalInfrastructure, "storage-upload-terminal");
            try
            {
                await faults.CheckpointAsync(RemediationCheckpoint.BeforeDatabaseFinalization, claim.ExecutionId,
                    claim.AttemptNumber, cancellationToken);
                await CompleteWithVersion(claim, source, uploaded, resultId, approved.Preview.Operations.Count,
                    ownsUploadedObject, cancellationToken);
                // Database commit made this object canonical; a lost response must not delete it.
                uploaded = null;
                await faults.CheckpointAsync(RemediationCheckpoint.AfterDatabaseFinalization, claim.ExecutionId,
                    claim.AttemptNumber, cancellationToken);
            }
            catch (Exception exception) when (DatabaseTransient(exception))
            { throw new FixExecutionException(FixFailureCategory.TransientInfrastructure, "database-transient", exception); }
            catch (DbUpdateException exception) when (UniqueViolation(exception))
            { throw new FixExecutionException(FixFailureCategory.Conflict, "fix-concurrent-publish-conflict", exception); }
        }
        finally
        {
            if (uploaded is not null && ownsUploadedObject)
            {
                try
                {
                    if (!await IsCanonicalResultAsync(claim.ExecutionId, uploaded, CancellationToken.None))
                    {
                        await faults.CheckpointAsync(RemediationCheckpoint.BeforeOrphanCleanup, claim.ExecutionId,
                            claim.AttemptNumber, CancellationToken.None);
                        await storage.DeleteAsync(uploaded.StorageBucket, uploaded.StorageKey, CancellationToken.None);
                    }
                }
                catch (Exception exception)
                { throw new FixExecutionException(FixFailureCategory.TerminalInfrastructure, "result-cleanup-failed", exception); }
            }
            if (materialized is not null) try { File.Delete(materialized); } catch { }
            if (existingResult is not null) try { File.Delete(existingResult); } catch { }
            if (workspace is not null) try { Directory.Delete(workspace, true); } catch { }
        }
    }

    private async Task CompleteWithVersion(FixExecutionClaim claim, SourceRow source, StoredFile stored,
        Guid resultId, int operationCount, bool objectCreatedByAttempt, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var document = await db.Documents.FromSqlInterpolated($"select * from public.documents where id = {source.DocumentId} for update")
            .SingleAsync(cancellationToken);
        var job = await db.FixExecutionJobs.FromSqlInterpolated($"select * from public.fix_execution_jobs where id = {source.ExecutionId} for update")
            .SingleAsync(cancellationToken);
        if (job.State != FixExecutionState.Processing || job.ClaimToken != claim.Token
            || job.LeaseExpiresAt <= DateTimeOffset.UtcNow)
            throw new FixExecutionException(FixFailureCategory.TransientInfrastructure, "worker-lease-lost");
        if (document.CurrentVersionNo != source.SourceVersionNo)
            throw new FixExecutionException(FixFailureCategory.Conflict, "fix-source-version-superseded");
        var canonical = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == resultId, cancellationToken);
        if (canonical is not null)
        {
            if (canonical.ParentVersionId != source.SourceVersionId || canonical.Sha256 != stored.Sha256
                || canonical.SizeBytes != stored.SizeBytes)
                throw new FixExecutionException(FixFailureCategory.Conflict, "fix-concurrent-publish-conflict");
            throw new FixExecutionException(FixFailureCategory.Conflict, "fix-execution-conflict");
        }
        var nextVersion = await db.DocumentVersions.Where(value => value.DocumentId == source.DocumentId)
            .MaxAsync(value => value.VersionNo, cancellationToken) + 1;
        var resultVersion = new DocumentVersion
        {
            Id = resultId, DocumentId = source.DocumentId, VersionNo = nextVersion,
            StorageBucket = stored.StorageBucket, StorageKey = stored.StorageKey,
            OriginalFilename = source.OriginalFilename, MimeType = stored.ContentType,
            SizeBytes = stored.SizeBytes, Sha256 = stored.Sha256,
            CreatedByUserId = source.RequestedByUserId, ParentVersionId = source.SourceVersionId
        };
        db.DocumentVersions.Add(resultVersion);
        document.CurrentVersionNo = nextVersion;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        job.State = FixExecutionState.Completed;
        job.ResultDocumentVersionId = resultId;
        job.ResultSha256 = stored.Sha256;
        job.ResultObjectSize = stored.SizeBytes;
        job.ObjectCreatedByAttempt = objectCreatedByAttempt ? claim.AttemptNumber : null;
        job.CompletedOperationCount = operationCount;
        job.ClaimToken = null;
        job.LeaseExpiresAt = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteNoChangeAsync(FixExecutionClaim claim, SourceRow source, int operationCount,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var document = await db.Documents.FromSqlInterpolated($"select * from public.documents where id = {source.DocumentId} for update")
            .SingleAsync(cancellationToken);
        var job = await db.FixExecutionJobs.FromSqlInterpolated($"select * from public.fix_execution_jobs where id = {claim.ExecutionId} for update")
            .SingleAsync(cancellationToken);
        if (job.State != FixExecutionState.Processing || job.ClaimToken != claim.Token
            || job.LeaseExpiresAt <= DateTimeOffset.UtcNow)
            throw new FixExecutionException(FixFailureCategory.TransientInfrastructure, "worker-lease-lost");
        if (document.CurrentVersionNo != source.SourceVersionNo)
            throw new FixExecutionException(FixFailureCategory.Conflict, "fix-source-version-superseded");
        job.State = FixExecutionState.NoChange;
        job.CompletedOperationCount = operationCount;
        job.ClaimToken = null;
        job.LeaseExpiresAt = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SourceRow?> LoadAsync(FixExecutionClaim claim, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FixExecutionJobs.AsNoTracking()
            .Where(value => value.Id == claim.ExecutionId && value.State == FixExecutionState.Processing
                && value.ClaimToken == claim.Token && value.LeaseExpiresAt > DateTimeOffset.UtcNow)
            .Select(value => new SourceRow(value.Id, value.ApprovedPlanSnapshotJson,
                value.SourceDocumentVersionId, value.SourceDocumentVersion!.Sha256,
                value.SourceDocumentVersion.StorageBucket, value.SourceDocumentVersion.StorageKey,
                value.SourceDocumentVersion.OriginalFilename, value.SourceDocumentVersion.DocumentId,
                value.SourceDocumentVersion.Document!.OwnerUserId, value.RequestedByUserId,
                value.SourceDocumentVersion.VersionNo, value.SourceDocumentVersion.Document.CurrentVersionNo,
                value.PlanHash, value.PlannerVersion, value.SelectedFindingIdsJson, value.PlannedOperationCount))
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
            || approved.Preview.Operations.Count != source.PlannedOperationCount
            || approved.Preview.Operations.Count == 0)
            throw new FixExecutionException(FixFailureCategory.InvalidPlan, "approved-plan-hash-invalid");
        Guid[] selected;
        try { selected = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(source.SelectedFindingIdsJson) ?? []; }
        catch (System.Text.Json.JsonException exception)
        { throw new FixExecutionException(FixFailureCategory.InvalidPlan, "approved-plan-selection-invalid", exception); }
        if (selected.Length is < 1 or > FixPlanSelection.MaximumFindingCount || selected.Contains(Guid.Empty)
            || !selected.ToHashSet().SetEquals(approved.Source.Findings.Select(value => value.FindingId))
            || approved.Preview.Operations.Select(value => value.Ordinal).Distinct().Count() != approved.Preview.Operations.Count)
            throw new FixExecutionException(FixFailureCategory.InvalidPlan, "approved-plan-selection-invalid");
    }

    private async Task EnsureActiveAsync(FixExecutionClaim claim, SourceRow source, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var active = await db.FixExecutionJobs.AsNoTracking().AnyAsync(value => value.Id == claim.ExecutionId
            && value.State == FixExecutionState.Processing && value.ClaimToken == claim.Token
            && value.LeaseExpiresAt > DateTimeOffset.UtcNow, cancellationToken);
        if (!active) throw new FixExecutionException(FixFailureCategory.TransientInfrastructure, "worker-lease-lost");
        var current = await db.Documents.AsNoTracking().Where(value => value.Id == source.DocumentId)
            .Select(value => value.CurrentVersionNo).SingleAsync(cancellationToken);
        if (current != source.SourceVersionNo)
            throw new FixExecutionException(FixFailureCategory.Conflict, "fix-source-version-superseded");
    }

    private async Task<bool> IsCanonicalResultAsync(Guid executionId, StoredFile stored,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FixExecutionJobs.AsNoTracking().AnyAsync(value => value.Id == executionId
            && value.State == FixExecutionState.Completed && value.ResultDocumentVersionId == executionId
            && value.ResultSha256 == stored.Sha256 && value.ResultObjectSize == stored.SizeBytes,
            cancellationToken);
    }

    private static FixExecutionException DownloadFailure(FileStorageException exception) => exception.Kind switch
    {
        FileStorageFailureKind.NotFound => new(FixFailureCategory.InvalidSource, "source-storage-object-missing", exception),
        FileStorageFailureKind.SizeLimit => new(FixFailureCategory.InvalidSource, "source-size-invalid", exception),
        FileStorageFailureKind.Transient => new(FixFailureCategory.TransientInfrastructure, "storage-download-transient", exception),
        _ => new(FixFailureCategory.TerminalInfrastructure, "database-finalization-terminal", exception)
    };

    private static FixExecutionException UploadFailure(FileStorageException exception) => exception.Kind == FileStorageFailureKind.Transient
        ? new(FixFailureCategory.TransientInfrastructure, "storage-upload-transient", exception)
        : new(FixFailureCategory.TerminalInfrastructure, "storage-upload-terminal", exception);

    private static bool DatabaseTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.ConnectionException }) return true;
        return false;
    }

    private static bool UniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) return true;
        return false;
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
            if (paragraph is null || !OperationPostcondition(paragraph, operation))
                throw new FixExecutionException("fix-operation-postcondition-failed");
        }
    }

    private static bool OperationPostcondition(ParsedParagraph paragraph, FixPlanOperation operation)
    {
        var expected = operation.Expected.Value;
        return operation.PropertyIdentifier switch
        {
            "paragraph.alignment" => expected switch
            {
                "justified" => paragraph.DirectAlignment == ParsedAlignment.Justified,
                "centered" => paragraph.DirectAlignment == ParsedAlignment.Center,
                _ => false
            },
            "paragraph.line-spacing-value" => paragraph.DirectLineSpacingValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) == expected,
            "paragraph.line-spacing-rule" => string.Equals(paragraph.DirectLineSpacingRule, expected, StringComparison.OrdinalIgnoreCase),
            "paragraph.spacing-before" => paragraph.DirectSpacingBeforeTwips?.ToString(System.Globalization.CultureInfo.InvariantCulture) == expected,
            "paragraph.spacing-after" => paragraph.DirectSpacingAfterTwips?.ToString(System.Globalization.CultureInfo.InvariantCulture) == expected,
            "paragraph.first-line-indent" => paragraph.DirectFirstLineIndentTwips?.ToString(System.Globalization.CultureInfo.InvariantCulture) == expected
                && paragraph.DirectHangingIndentTwips is null,
            "run.font-family-ascii" => Run(operation) is { } asciiRun
                && string.Equals(asciiRun.DirectFontAscii, expected, StringComparison.OrdinalIgnoreCase),
            "run.font-family-high-ansi" => Run(operation) is { } highAnsiRun
                && string.Equals(highAnsiRun.DirectFontHighAnsi, expected, StringComparison.OrdinalIgnoreCase),
            "run.font-size" => Run(operation)?.DirectFontSizeHalfPoints?.ToString(System.Globalization.CultureInfo.InvariantCulture) == expected,
            _ => false
        };

        ParsedRun? Run(FixPlanOperation value) => paragraph.RunList.SingleOrDefault(run => run.Index == value.Target.RunIndex);
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
        Guid DocumentId, Guid OwnerUserId, Guid RequestedByUserId, int SourceVersionNo,
        int CurrentVersionNo, string PlanHash, string PlannerVersion, string SelectedFindingIdsJson,
        int PlannedOperationCount);
}
