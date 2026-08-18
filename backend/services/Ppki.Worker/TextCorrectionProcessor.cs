using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.Infrastructure;

namespace Ppki.Worker;

public sealed class TextCorrectionProcessor(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IFileStorage storage,
    DeterministicTextCorrectionDetector detector,
    IReauditService reaudits,
    TimeProvider timeProvider)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        if (await AdvanceBatchAsync(cancellationToken)) return true;
        return await AnalyzeAsync(cancellationToken);
    }

    private async Task<bool> AnalyzeAsync(CancellationToken cancellationToken)
    {
        Guid? auditId;
        Guid? existingAnalysisId;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var recoverable = await db.TextCorrectionAnalyses.AsNoTracking()
                .Where(value => value.State == TextCorrectionAnalysisState.Pending
                    || value.State == TextCorrectionAnalysisState.Processing)
                .OrderBy(value => value.CreatedAt).Select(value => new { value.Id, value.AuditJobId })
                .FirstOrDefaultAsync(cancellationToken);
            existingAnalysisId = recoverable?.Id;
            auditId = recoverable?.AuditJobId;
            if (auditId is null)
            {
                auditId = await db.AuditJobs.AsNoTracking()
                    .Where(value => value.Status == AuditJobStatus.Completed
                        && !db.TextCorrectionAnalyses.Any(item => item.AuditJobId == value.Id)
                        && value.DocumentVersion!.Document!.CurrentVersionNo == value.DocumentVersion.VersionNo
                        && (value.SourceFixExecutionId == null
                            && db.AutomaticRemediationOrchestrations.Any(item =>
                                item.SourceAuditJobId == value.Id
                                && (item.State == AutomaticRemediationState.NoAction
                                    || item.State == AutomaticRemediationState.Completed
                                    || item.State == AutomaticRemediationState.Failed
                                    || item.State == AutomaticRemediationState.Conflict))
                            || value.SourceFixExecutionId != null
                            && (db.AutomaticRemediationOrchestrations.Any(item =>
                                    item.ReauditJobId == value.Id
                                    && item.State == AutomaticRemediationState.Completed)
                                || db.TextCorrectionBatches.Any(item => item.ReauditJobId == value.Id
                                    && item.State == TextCorrectionBatchState.VerificationPending))))
                    .OrderBy(value => value.CompletedAt).ThenBy(value => value.Id)
                    .Select(value => (Guid?)value.Id).FirstOrDefaultAsync(cancellationToken);
            }
        }
        if (auditId is null) return false;

        TextCorrectionAnalysis analysis;
        if (existingAnalysisId is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            analysis = await db.TextCorrectionAnalyses.AsNoTracking().SingleAsync(
                value => value.Id == existingAnalysisId, cancellationToken);
        }
        else await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var source = await db.AuditJobs.AsNoTracking().Where(value => value.Id == auditId)
                .Select(value => new { Audit = value, Version = value.DocumentVersion! })
                .SingleAsync(cancellationToken);
            analysis = new()
            {
                AuditJobId = source.Audit.Id, DocumentVersionId = source.Version.Id,
                SourceSha256 = source.Version.Sha256,
                DetectorId = DeterministicTextCorrectionDetector.DetectorId,
                DetectorVersion = DeterministicTextCorrectionDetector.DetectorVersion,
                CatalogVersion = DeterministicTextCorrectionDetector.CatalogVersion,
                State = TextCorrectionAnalysisState.Pending, CreatedAt = timeProvider.GetUtcNow()
            };
            db.TextCorrectionAnalyses.Add(analysis);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { return true; }
        }

        string? path = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var tracked = await db.TextCorrectionAnalyses.SingleAsync(value => value.Id == analysis.Id, cancellationToken);
            tracked.State = TextCorrectionAnalysisState.Processing;
            tracked.StartedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            var source = await db.DocumentVersions.AsNoTracking().Where(
                    value => value.Id == tracked.DocumentVersionId)
                .Select(value => new { Version = value, value.Document!.CurrentVersionNo })
                .SingleAsync(cancellationToken);
            if (source.Version.VersionNo != source.CurrentVersionNo
                || source.Version.Sha256 != tracked.SourceSha256)
                throw new TextCorrectionException("correction-analysis-source-superseded");
            path = await storage.MaterializeToTempFileAsync(source.Version.StorageBucket,
                source.Version.StorageKey, cancellationToken);
            var detected = await detector.DetectAsync(path, tracked.DocumentVersionId,
                tracked.SourceSha256, cancellationToken);
            var existingIdentities = await db.TextCorrectionProposals.AsNoTracking()
                .Where(value => value.AnalysisId == tracked.Id).Select(value => value.ProposalIdentity)
                .ToHashSetAsync(cancellationToken);
            foreach (var candidate in detected)
            {
                if (!TextCorrectionPrivacyContract.TryValidateReplacement(candidate.SuggestedReplacement,
                        out _, out _))
                    throw new TextCorrectionException("correction-catalog-invalid");
                var identity = ApprovedTextCorrectionExecutionPlanSerializer.HashFields(
                    tracked.AuditJobId.ToString("D"), tracked.DocumentVersionId.ToString("D"),
                    candidate.RuleId, candidate.Anchor.AnchorHash, candidate.SuggestionHash,
                    tracked.DetectorVersion, tracked.CatalogVersion);
                if (existingIdentities.Contains(identity)) continue;
                db.TextCorrectionProposals.Add(new()
                {
                    Id = GuidFromHash(identity),
                    AnalysisId = tracked.Id, AuditJobId = tracked.AuditJobId,
                    DocumentVersionId = tracked.DocumentVersionId, SourceSha256 = tracked.SourceSha256,
                    DetectorId = tracked.DetectorId, DetectorVersion = tracked.DetectorVersion,
                    CatalogVersion = tracked.CatalogVersion, CatalogRuleId = candidate.RuleId,
                    Category = candidate.Category, AnchorContractVersion = candidate.Anchor.ContractVersion,
                    AnchorEvidenceJson = ExactTextAnchorJson.Serialize(candidate.Anchor),
                    AnchorHash = candidate.Anchor.AnchorHash,
                    SuggestedReplacement = candidate.SuggestedReplacement,
                    SuggestionHash = candidate.SuggestionHash,
                    ProposalIdentity = identity,
                    CreatedAt = timeProvider.GetUtcNow()
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            tracked.State = TextCorrectionAnalysisState.Completed;
            tracked.ProposalCount = detected.Count;
            tracked.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var tracked = await db.TextCorrectionAnalyses.SingleOrDefaultAsync(value => value.Id == analysis.Id,
                CancellationToken.None);
            if (tracked is not null)
            {
                tracked.State = TextCorrectionAnalysisState.Failed;
                tracked.SafeFailureCode = "correction-analysis-failed";
                tracked.CompletedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(CancellationToken.None);
            }
            return true;
        }
        finally
        {
            if (path is not null) try { File.Delete(path); } catch { }
        }
    }

    private async Task<bool> AdvanceBatchAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await db.TextCorrectionBatches.FromSqlRaw("""
            select * from public.text_correction_batches
            where state in ('Queued','Processing','ReauditPending','VerificationPending')
            order by updated_at, created_at for update skip locked limit 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var progressed = true;
        if (batch.State is TextCorrectionBatchState.Queued or TextCorrectionBatchState.Processing)
            await ObserveExecutionAsync(db, batch, cancellationToken);
        else if (batch.State == TextCorrectionBatchState.ReauditPending)
            await ObserveReauditAsync(db, batch, cancellationToken);
        else
            progressed = await VerifyAsync(db, batch, cancellationToken);
        batch.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return progressed;
    }

    private async Task ObserveExecutionAsync(PpkiDbContext db, TextCorrectionBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.FixExecutionId is null) { Fail(batch, false, "correction-execution-missing"); return; }
        var execution = await db.FixExecutionJobs.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == batch.FixExecutionId, cancellationToken);
        if (execution is null || execution.AuditJobId != batch.SourceAuditJobId
            || execution.SourceDocumentVersionId != batch.SourceDocumentVersionId)
        { Fail(batch, false, "correction-execution-lineage-invalid"); return; }
        if (execution.State == FixExecutionState.Queued) return;
        if (execution.State == FixExecutionState.Processing) { batch.State = TextCorrectionBatchState.Processing; return; }
        if (execution.State == FixExecutionState.Failed)
        { Fail(batch, execution.FailureCategory == FixFailureCategory.Conflict, "correction-execution-failed"); return; }
        if (execution.State != FixExecutionState.Completed || execution.ResultDocumentVersionId is null)
        { Fail(batch, false, "correction-execution-result-invalid"); return; }
        try
        {
            var reaudit = await reaudits.CreateAsync(execution.Id, batch.ActorUserId, cancellationToken);
            if (reaudit is null) { Fail(batch, false, "correction-reaudit-create-failed"); return; }
            batch.ResultDocumentVersionId = execution.ResultDocumentVersionId;
            batch.ReauditJobId = reaudit.AuditId;
            batch.State = TextCorrectionBatchState.ReauditPending;
            await db.TextCorrectionBatchItems.Where(value => value.BatchId == batch.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.VerificationState, TextCorrectionVerificationState.ReauditPending),
                    cancellationToken);
        }
        catch (ReauditException) { Fail(batch, false, "correction-reaudit-create-failed"); }
    }

    private static async Task ObserveReauditAsync(PpkiDbContext db, TextCorrectionBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.ReauditJobId is null || batch.ResultDocumentVersionId is null)
        { Fail(batch, false, "correction-reaudit-lineage-missing"); return; }
        var audit = await db.AuditJobs.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == batch.ReauditJobId, cancellationToken);
        if (audit is null || audit.SourceFixExecutionId != batch.FixExecutionId
            || audit.DocumentVersionId != batch.ResultDocumentVersionId)
        { Fail(batch, false, "correction-reaudit-lineage-invalid"); return; }
        if (audit.Status is AuditJobStatus.Queued or AuditJobStatus.Processing) return;
        if (audit.Status != AuditJobStatus.Completed) { Fail(batch, false, "correction-reaudit-failed"); return; }
        batch.State = TextCorrectionBatchState.VerificationPending;
    }

    private async Task<bool> VerifyAsync(PpkiDbContext db, TextCorrectionBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.ReauditJobId is null) { Fail(batch, false, "correction-verification-lineage-missing"); return true; }
        var analysis = await db.TextCorrectionAnalyses.AsNoTracking().Include(value => value.Proposals)
            .SingleOrDefaultAsync(value => value.AuditJobId == batch.ReauditJobId, cancellationToken);
        if (analysis is null || analysis.State is TextCorrectionAnalysisState.Pending or TextCorrectionAnalysisState.Processing) return false;
        var items = await db.TextCorrectionBatchItems.Include(value => value.DecisionEvent)!
            .ThenInclude(value => value!.Proposal).Where(value => value.BatchId == batch.Id)
            .ToListAsync(cancellationToken);
        if (analysis.State != TextCorrectionAnalysisState.Completed)
        {
            foreach (var item in items)
            {
                item.VerificationState = TextCorrectionVerificationState.VerificationUnavailable;
                item.VerifiedAt = timeProvider.GetUtcNow();
            }
            batch.State = TextCorrectionBatchState.Completed;
            return true;
        }
        var newEvidence = analysis.Proposals.Select(value => new
        {
            value.CatalogRuleId,
            Location = ExactTextAnchorJson.Deserialize(value.AnchorEvidenceJson).ParagraphLocation.ToCompactString(),
            ExactTextAnchorJson.Deserialize(value.AnchorEvidenceJson).Start
        }).ToHashSet();
        var applied = items.Select(value =>
        {
            var proposal = value.DecisionEvent!.Proposal!;
            var anchor = ExactTextAnchorJson.Deserialize(proposal.AnchorEvidenceJson);
            var raw = value.DecisionEvent.Action == TextCorrectionDecisionAction.UseSuggestion
                ? proposal.SuggestedReplacement : value.DecisionEvent.ManualReplacement;
            if (!TextCorrectionPrivacyContract.TryValidateReplacement(raw, out var replacement, out _))
                throw new TextCorrectionException("correction-verification-evidence-invalid");
            return new { Anchor = anchor, ReplacementLength = replacement!.ScalarLength };
        }).ToArray();
        foreach (var item in items)
        {
            var old = item.DecisionEvent!.Proposal!;
            var oldAnchor = ExactTextAnchorJson.Deserialize(old.AnchorEvidenceJson);
            var oldLocation = oldAnchor.ParagraphLocation.ToCompactString();
            var translatedStart = oldAnchor.Start + applied.Where(value =>
                    value.Anchor.ParagraphLocation.ToCompactString() == oldLocation
                    && value.Anchor.Start < oldAnchor.Start)
                .Sum(value => value.ReplacementLength - value.Anchor.Length);
            item.VerificationState = newEvidence.Contains(new
                { old.CatalogRuleId, Location = oldLocation, Start = translatedStart })
                ? TextCorrectionVerificationState.VerifiedStillDetected
                : TextCorrectionVerificationState.VerifiedResolved;
            item.VerifiedAt = timeProvider.GetUtcNow();
        }
        batch.State = TextCorrectionBatchState.Completed;
        return true;
    }

    private static void Fail(TextCorrectionBatch batch, bool conflict, string code)
    {
        batch.State = conflict ? TextCorrectionBatchState.Conflict : TextCorrectionBatchState.Failed;
        batch.SafeFailureCode = code;
    }

    private static Guid GuidFromHash(string value)
    {
        var bytes = Convert.FromHexString(value)[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}

public sealed class TextCorrectionWorker(
    ILogger<TextCorrectionWorker> logger,
    IConfiguration configuration,
    TextCorrectionProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Max(1, int.TryParse(configuration["Worker:PollSeconds"], out var value) ? value : 2);
        logger.LogInformation("Text correction lifecycle worker started with {PollSeconds}s polling.", pollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await processor.ProcessNextAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var safeCode = exception is TextCorrectionException correction
                    ? correction.DiagnosticCode
                    : exception is Microsoft.EntityFrameworkCore.DbUpdateException
                        ? "correction-database-write-failed"
                        : exception.GetType().Name;
                logger.LogError("Text correction lifecycle iteration failed safely; Code={SafeCode}.", safeCode);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
        }
    }
}
