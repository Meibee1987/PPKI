using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class TextCorrectionService(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IInternalAdminAuthorizationService authorization,
    IFileStorage storage,
    ITextCorrectionContextMaterializationService contextMaterializer,
    TimeProvider timeProvider) : ITextCorrectionService
{
    public const int MaximumBatchSize = 100;

    public async Task<TextCorrectionProposalPage?> ListAsync(Guid auditId, Guid actorUserId,
        TextCorrectionProposalQuery query, CancellationToken cancellationToken)
    {
        await authorization.RequirePpkiAdminAsync(actorUserId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var analysis = await db.TextCorrectionAnalyses.AsNoTracking()
            .Where(value => value.AuditJobId == auditId && value.State == TextCorrectionAnalysisState.Completed)
            .Select(value => new { value.Id, value.DocumentVersionId }).SingleOrDefaultAsync(cancellationToken);
        if (analysis is null) return null;
        var source = db.TextCorrectionProposals.AsNoTracking().Where(value => value.AnalysisId == analysis.Id);
        var total = await source.CountAsync(cancellationToken);
        var currentVersion = await db.DocumentVersions.AsNoTracking()
            .Where(value => value.Id == analysis.DocumentVersionId)
            .Select(value => value.Document!.CurrentVersionNo == value.VersionNo)
            .SingleAsync(cancellationToken);
        var effectiveActions = await source.Select(value => value.Decisions
                .OrderByDescending(item => item.Sequence)
                .Select(item => (TextCorrectionDecisionAction?)item.Action).FirstOrDefault())
            .ToListAsync(cancellationToken);
        var useSuggestion = effectiveActions.Count(value => value == TextCorrectionDecisionAction.UseSuggestion);
        var editManual = effectiveActions.Count(value => value == TextCorrectionDecisionAction.EditManual);
        var ignored = effectiveActions.Count(value => value == TextCorrectionDecisionAction.Ignore);
        var summary = new TextCorrectionProposalSummary(
            effectiveActions.Count(value => value is null), useSuggestion, editManual, ignored,
            currentVersion ? useSuggestion + editManual : 0, currentVersion ? 0 : total);
        var activeBatch = await db.TextCorrectionBatches.AsNoTracking().Include(value => value.Items)
            .Where(value => value.SourceAuditJobId == auditId)
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var rows = await source.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(value => new
            {
                Proposal = value,
                Latest = value.Decisions.OrderByDescending(item => item.Sequence).FirstOrDefault()
            }).ToListAsync(cancellationToken);
        var pages = await PageLocationsAsync(db, analysis.DocumentVersionId,
            rows.Select(value => value.Proposal).ToArray(), cancellationToken);
        return new(auditId, analysis.DocumentVersionId, query.Page, query.PageSize, total,
            rows.Select(row => new TextCorrectionProposalItem(row.Proposal.Id,
                row.Proposal.CatalogRuleId, row.Proposal.Category, "Actionable", true,
                pages.GetValueOrDefault(row.Proposal.Id), "Exact",
                row.Latest is null ? null : new(row.Latest.Id, row.Latest.Sequence,
                    row.Latest.Action.ToString(), row.Latest.ActorUserId))).ToArray(), summary,
            activeBatch is null ? null : BatchStatus(activeBatch));
    }

    public async Task<TextCorrectionProposalContext?> ContextAsync(Guid proposalId, Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await authorization.RequirePpkiAdminAsync(actorUserId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.TextCorrectionProposals.AsNoTracking()
            .Where(value => value.Id == proposalId && value.Analysis!.State == TextCorrectionAnalysisState.Completed)
            .Select(value => new
            {
                Proposal = value,
                value.DocumentVersion!.StorageBucket,
                value.DocumentVersion.StorageKey
            }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var anchor = DeserializeAnchor(row.Proposal);
        var pages = await PageLocationsAsync(db, row.Proposal.DocumentVersionId, [row.Proposal], cancellationToken);
        string? path = null;
        try
        {
            path = await storage.MaterializeToTempFileAsync(row.StorageBucket, row.StorageKey, cancellationToken);
            var result = await contextMaterializer.MaterializeAsync(actorUserId, path,
                new(row.Proposal.AuditJobId, row.Proposal.Id, row.Proposal.DocumentVersionId,
                    row.Proposal.SourceSha256, anchor, pages.GetValueOrDefault(proposalId)?.PageNumber), cancellationToken);
            return result.Context is null
                ? new(proposalId, row.Proposal.DocumentVersionId, result.Status.ToString(), result.SafeFailureCode,
                    null, null, row.Proposal.SuggestedReplacement, null, false, false,
                    pages.GetValueOrDefault(proposalId))
                : new(proposalId, row.Proposal.DocumentVersionId, result.Status.ToString(), null,
                    result.Context.TargetText, result.Context.Context, row.Proposal.SuggestedReplacement,
                    result.Context.TargetOffsetInContext, result.Context.PrefixTruncated,
                    result.Context.SuffixTruncated, pages.GetValueOrDefault(proposalId));
        }
        finally
        {
            if (path is not null) try { File.Delete(path); } catch { }
        }
    }

    public async Task<TextCorrectionDecisionAccepted?> DecideAsync(Guid proposalId, Guid actorUserId,
        Guid idempotencyKey, TextCorrectionDecisionRequest request, CancellationToken cancellationToken)
    {
        await authorization.RequirePpkiAdminAsync(actorUserId, cancellationToken);
        if (idempotencyKey == Guid.Empty) throw new TextCorrectionException("correction-idempotency-key-invalid");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var proposal = await db.TextCorrectionProposals.FromSqlInterpolated(
                $"select * from public.text_correction_proposals where id = {proposalId} for update")
            .SingleOrDefaultAsync(cancellationToken);
        if (proposal is null) return null;
        var (manual, replacementHash) = DecisionEvidence(proposal, request);
        var semanticHash = ApprovedTextCorrectionExecutionPlanSerializer.HashFields(
            proposal.Id.ToString("D"), request.Action.ToString(), replacementHash ?? "-");
        var replay = await db.TextCorrectionDecisionEvents.AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProposalId == proposalId
                && value.IdempotencyKey == idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.SemanticHash != semanticHash) throw new TextCorrectionException("correction-idempotency-conflict");
            await transaction.CommitAsync(cancellationToken);
            return Accepted(replay, true);
        }
        var sequence = (await db.TextCorrectionDecisionEvents.Where(value => value.ProposalId == proposalId)
            .MaxAsync(value => (int?)value.Sequence, cancellationToken) ?? 0) + 1;
        var created = new TextCorrectionDecisionEvent
        {
            ProposalId = proposalId, Sequence = sequence, ActorUserId = actorUserId,
            Action = request.Action, SourceDocumentVersionId = proposal.DocumentVersionId,
            AnchorHash = proposal.AnchorHash, ManualReplacement = manual,
            ReplacementHash = replacementHash, IdempotencyKey = idempotencyKey,
            SemanticHash = semanticHash, CreatedAt = timeProvider.GetUtcNow()
        };
        db.TextCorrectionDecisionEvents.Add(created);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Accepted(created, false);
    }

    public async Task<TextCorrectionBatchAccepted?> CreateBatchAsync(Guid auditId, Guid actorUserId,
        Guid idempotencyKey, TextCorrectionBatchRequest request, CancellationToken cancellationToken)
    {
        await authorization.RequirePpkiAdminAsync(actorUserId, cancellationToken);
        if (idempotencyKey == Guid.Empty) throw new TextCorrectionException("correction-idempotency-key-invalid");
        var requestedIds = request.DecisionIds?.Distinct().Order().ToArray();
        if (requestedIds is { Length: > MaximumBatchSize }) throw new TextCorrectionException("correction-batch-size-invalid");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var historicalReplay = await db.TextCorrectionBatches.AsNoTracking().Include(value => value.Items)
            .SingleOrDefaultAsync(value => value.SourceAuditJobId == auditId
                && value.ActorUserId == actorUserId && value.IdempotencyKey == idempotencyKey, cancellationToken);
        if (historicalReplay is not null)
        {
            if (requestedIds is not null && !requestedIds.ToHashSet()
                    .SetEquals(historicalReplay.Items.Select(value => value.DecisionEventId)))
                throw new TextCorrectionException("correction-idempotency-conflict");
            await transaction.CommitAsync(cancellationToken);
            return BatchAccepted(historicalReplay, true);
        }
        var source = await db.AuditJobs.AsNoTracking().Where(value => value.Id == auditId
                && value.Status == AuditJobStatus.Completed
                && value.DocumentVersion!.Document!.CurrentVersionNo == value.DocumentVersion.VersionNo)
            .Select(value => new { Audit = value, Version = value.DocumentVersion! })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null) return null;
        var proposals = await db.TextCorrectionProposals.AsNoTracking()
            .Where(value => value.AuditJobId == auditId && value.Analysis!.State == TextCorrectionAnalysisState.Completed)
            .Select(value => new
            {
                Proposal = value,
                Latest = value.Decisions.OrderByDescending(item => item.Sequence).FirstOrDefault()
            }).ToListAsync(cancellationToken);
        var decisions = proposals.Where(value => value.Latest is not null
                && value.Latest.Action != TextCorrectionDecisionAction.Ignore
                && (requestedIds is null || requestedIds.Contains(value.Latest.Id)))
            .OrderBy(value => value.Latest!.Id).ToArray();
        if (requestedIds is not null && decisions.Length != requestedIds.Length)
            throw new TextCorrectionException("correction-decision-stale");
        if (decisions.Length is < 1 or > MaximumBatchSize)
            throw new TextCorrectionException("correction-batch-size-invalid");
        foreach (var item in decisions) ValidateDecision(item.Proposal, item.Latest!);

        var decisionSetHash = ApprovedTextCorrectionExecutionPlanSerializer.HashFields(
            source.Version.Id.ToString("D"), string.Join("\n", decisions.Select(value => value.Latest!.Id.ToString("D"))));
        var sameSet = await db.TextCorrectionBatches.AsNoTracking().SingleOrDefaultAsync(value =>
            value.SourceDocumentVersionId == source.Version.Id && value.DecisionSetHash == decisionSetHash,
            cancellationToken);
        if (sameSet is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return BatchAccepted(sameSet, true);
        }

        var now = timeProvider.GetUtcNow();
        var batch = new TextCorrectionBatch
        {
            SourceAuditJobId = auditId, SourceDocumentVersionId = source.Version.Id,
            ActorUserId = actorUserId, IdempotencyKey = idempotencyKey,
            DecisionSetHash = decisionSetHash, DecisionCount = decisions.Length,
            State = TextCorrectionBatchState.Pending, UpdatedAt = now, CreatedAt = now
        };
        db.TextCorrectionBatches.Add(batch);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (Exception exception) when (ConcurrencyConflict(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return await ReplayBatchAsync(auditId, actorUserId, idempotencyKey,
                source.Version.Id, decisionSetHash, cancellationToken);
        }
        var references = decisions.Select((value, ordinal) => new TextCorrectionExecutionReference(
            ordinal + 1, value.Latest!.Id, value.Proposal.AnchorHash, value.Latest.ReplacementHash!)).ToArray();
        var approved = new ApprovedTextCorrectionExecutionPlan(
            ApprovedTextCorrectionExecutionPlanSerializer.SchemaVersion, batch.Id, auditId,
            source.Version.Id, source.Version.Sha256, references);
        var executionId = batch.Id;
        var executionIdempotencyKey = GuidFromHash(ApprovedTextCorrectionExecutionPlanSerializer.HashFields(
            "text-correction-fix-execution", batch.Id.ToString("D")));
        var planHash = ApprovedTextCorrectionExecutionPlanSerializer.Hash(approved);
        var job = new FixExecutionJob
        {
            Id = executionId, AuditJobId = auditId, SourceDocumentVersionId = source.Version.Id,
            RequestedByUserId = actorUserId, IdempotencyKey = executionIdempotencyKey, PlanHash = planHash,
            PlannerVersion = ApprovedTextCorrectionExecutionPlanSerializer.PlannerVersion,
            SelectedFindingIdsJson = JsonSerializer.Serialize(references.Select(value => value.DecisionId)),
            ApprovedPlanSnapshotJson = ApprovedTextCorrectionExecutionPlanSerializer.Serialize(approved),
            PlannedOperationCount = references.Length, State = FixExecutionState.Queued, CreatedAt = now
        };
        db.FixExecutionJobs.Add(job);
        db.TextCorrectionBatchItems.AddRange(references.Select(value => new TextCorrectionBatchItem
        {
            BatchId = batch.Id, DecisionEventId = value.DecisionId, Ordinal = value.Ordinal,
            VerificationState = TextCorrectionVerificationState.Applied, CreatedAt = now
        }));
        batch.FixExecutionId = executionId;
        batch.State = TextCorrectionBatchState.Queued;
        batch.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (ConcurrencyConflict(exception))
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            return await ReplayBatchAsync(auditId, actorUserId, idempotencyKey,
                source.Version.Id, decisionSetHash, cancellationToken);
        }
        return BatchAccepted(batch, false);
    }

    public async Task<TextCorrectionBatchStatus?> GetBatchAsync(Guid batchId, Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await authorization.RequirePpkiAdminAsync(actorUserId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.TextCorrectionBatches.AsNoTracking().Include(value => value.Items)
            .SingleOrDefaultAsync(value => value.Id == batchId, cancellationToken);
        if (batch is null) return null;
        return new(batch.Id, batch.SourceAuditJobId, batch.SourceDocumentVersionId, batch.FixExecutionId,
            batch.ResultDocumentVersionId, batch.ReauditJobId, batch.State.ToString(), batch.DecisionCount,
            batch.SafeFailureCode, batch.Items.GroupBy(value => value.VerificationState.ToString())
                .ToDictionary(value => value.Key, value => value.Count(), StringComparer.Ordinal));
    }

    private static (string? Manual, string? Hash) DecisionEvidence(TextCorrectionProposal proposal,
        TextCorrectionDecisionRequest request)
    {
        if (request.Action == TextCorrectionDecisionAction.Ignore)
        {
            if (request.ManualReplacement is not null) throw new TextCorrectionException("correction-decision-invalid");
            return (null, null);
        }
        if (request.Action == TextCorrectionDecisionAction.UseSuggestion)
        {
            if (request.ManualReplacement is not null
                || !TextCorrectionPrivacyContract.TryValidateReplacement(proposal.SuggestedReplacement,
                    out _, out _)) throw new TextCorrectionException("correction-decision-invalid");
            return (null, proposal.SuggestionHash);
        }
        if (request.Action == TextCorrectionDecisionAction.EditManual
            && TextCorrectionPrivacyContract.TryValidateReplacement(request.ManualReplacement,
                out var replacement, out _)) return (replacement!.Value, replacement.Fingerprint);
        throw new TextCorrectionException(TextCorrectionPrivacyContract.ReplacementInvalidCode);
    }

    private static void ValidateDecision(TextCorrectionProposal proposal, TextCorrectionDecisionEvent decision)
    {
        if (decision.SourceDocumentVersionId != proposal.DocumentVersionId || decision.AnchorHash != proposal.AnchorHash)
            throw new TextCorrectionException("correction-decision-stale");
        if (decision.Action == TextCorrectionDecisionAction.UseSuggestion)
        {
            if (decision.ManualReplacement is not null || decision.ReplacementHash != proposal.SuggestionHash
                || !TextCorrectionPrivacyContract.TryValidateReplacement(proposal.SuggestedReplacement, out _, out _))
                throw new TextCorrectionException("correction-decision-invalid");
        }
        else if (decision.Action == TextCorrectionDecisionAction.EditManual)
        {
            if (!TextCorrectionPrivacyContract.TryValidateReplacement(decision.ManualReplacement,
                    out var replacement, out _) || replacement!.Fingerprint != decision.ReplacementHash)
                throw new TextCorrectionException("correction-decision-invalid");
        }
        else throw new TextCorrectionException("correction-decision-stale");
    }

    private static ExactTextAnchor DeserializeAnchor(TextCorrectionProposal proposal)
    {
        var anchor = ExactTextAnchorJson.Deserialize(proposal.AnchorEvidenceJson);
        if (anchor.AnchorHash != proposal.AnchorHash || anchor.DocumentVersionId != proposal.DocumentVersionId
            || !string.Equals(anchor.SourceSha256, proposal.SourceSha256, StringComparison.Ordinal))
            throw new TextCorrectionException(TextCorrectionPrivacyContract.EvidenceConflictCode);
        return anchor;
    }

    private static TextCorrectionDecisionAccepted Accepted(TextCorrectionDecisionEvent value, bool replayed) =>
        new(value.Id, value.ProposalId, value.Sequence, value.Action.ToString(), value.ActorUserId,
            value.CreatedAt, replayed);
    private static TextCorrectionBatchAccepted BatchAccepted(TextCorrectionBatch value, bool replayed) =>
        new(value.Id, value.SourceAuditJobId, value.SourceDocumentVersionId, value.FixExecutionId,
            value.State.ToString(), value.DecisionCount, replayed);
    private static TextCorrectionBatchStatus BatchStatus(TextCorrectionBatch value) =>
        new(value.Id, value.SourceAuditJobId, value.SourceDocumentVersionId, value.FixExecutionId,
            value.ResultDocumentVersionId, value.ReauditJobId, value.State.ToString(), value.DecisionCount,
            value.SafeFailureCode, value.Items.GroupBy(item => item.VerificationState.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

    private static Guid GuidFromHash(string value)
    {
        var bytes = Convert.FromHexString(value)[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private async Task<TextCorrectionBatchAccepted> ReplayBatchAsync(Guid auditId, Guid actorUserId,
        Guid idempotencyKey, Guid sourceVersionId, string decisionSetHash,
        CancellationToken cancellationToken)
    {
        await using var replayDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var replay = await replayDb.TextCorrectionBatches.AsNoTracking().SingleOrDefaultAsync(value =>
            value.SourceAuditJobId == auditId && value.ActorUserId == actorUserId
                && value.IdempotencyKey == idempotencyKey, cancellationToken)
            ?? await replayDb.TextCorrectionBatches.AsNoTracking().SingleOrDefaultAsync(value =>
                value.SourceDocumentVersionId == sourceVersionId
                    && value.DecisionSetHash == decisionSetHash, cancellationToken)
            ?? throw new TextCorrectionException("correction-batch-concurrency-conflict");
        if (replay.DecisionSetHash != decisionSetHash)
            throw new TextCorrectionException("correction-idempotency-conflict");
        return BatchAccepted(replay, true);
    }

    private static bool ConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.SerializationFailure }) return true;
        return false;
    }

    private static async Task<IReadOnlyDictionary<Guid, TextCorrectionPageLocation>> PageLocationsAsync(
        PpkiDbContext db, Guid versionId, IReadOnlyList<TextCorrectionProposal> proposals,
        CancellationToken cancellationToken)
    {
        var artifactId = await db.DocumentRenderArtifacts.AsNoTracking()
            .Where(value => value.DocumentVersionId == versionId && value.RenderJob!.State == DocumentRenderState.Completed)
            .Select(value => (Guid?)value.Id).SingleOrDefaultAsync(cancellationToken);
        if (artifactId is null) return new Dictionary<Guid, TextCorrectionPageLocation>();
        var locations = proposals.Select(value => new
        {
            value.Id,
            Paragraph = DeserializeAnchor(value).ParagraphLocation.ParagraphIndex
        }).Where(value => value.Paragraph is not null).ToArray();
        var indexes = locations.Select(value => value.Paragraph!.Value).Distinct().ToArray();
        var entries = await db.DocumentPageMapEntries.AsNoTracking()
            .Where(value => value.RenderArtifactId == artifactId && value.ParagraphIndex != null
                && indexes.Contains(value.ParagraphIndex.Value) && value.RunIndex == null)
            .Select(value => new { value.ParagraphIndex, value.PageNumber, value.Confidence })
            .ToListAsync(cancellationToken);
        return locations.Join(entries, location => location.Paragraph, entry => entry.ParagraphIndex,
                (location, entry) => new { location.Id, Page = new TextCorrectionPageLocation(entry.PageNumber,
                    entry.Confidence.ToString()) })
            .GroupBy(value => value.Id).ToDictionary(value => value.Key, value => value.First().Page);
    }
}
