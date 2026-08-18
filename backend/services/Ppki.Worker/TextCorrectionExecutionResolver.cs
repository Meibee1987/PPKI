using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;

namespace Ppki.Worker;

public sealed class TextCorrectionExecutionResolver(IDbContextFactory<PpkiDbContext> dbFactory)
{
    public async Task<IReadOnlyList<ExactTextReplacementOperation>> ResolveAsync(
        Guid executionId,
        Guid sourceDocumentVersionId,
        string sourceSha256,
        string planHash,
        string snapshotJson,
        CancellationToken cancellationToken)
    {
        ApprovedTextCorrectionExecutionPlan plan;
        try { plan = ApprovedTextCorrectionExecutionPlanSerializer.Deserialize(snapshotJson); }
        catch (Exception exception) when (exception is TextCorrectionException or System.Text.Json.JsonException)
        { throw new FixExecutionException(FixFailureCategory.InvalidPlan, "correction-plan-invalid", exception); }
        if (plan.SourceDocumentVersionId != sourceDocumentVersionId || plan.SourceSha256 != sourceSha256
            || ApprovedTextCorrectionExecutionPlanSerializer.Hash(plan) != planHash)
            throw new FixExecutionException(FixFailureCategory.InvalidPlan, "correction-plan-invalid");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.TextCorrectionBatches.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == plan.BatchId, cancellationToken);
        if (batch is null || batch.FixExecutionId != executionId
            || batch.SourceAuditJobId != plan.SourceAuditId
            || batch.SourceDocumentVersionId != sourceDocumentVersionId
            || batch.DecisionCount != plan.Operations.Count
            || batch.State is not (TextCorrectionBatchState.Queued or TextCorrectionBatchState.Processing))
            throw new FixExecutionException(FixFailureCategory.Conflict, "correction-batch-stale");

        var decisionIds = plan.Operations.Select(value => value.DecisionId).ToArray();
        var decisions = await db.TextCorrectionDecisionEvents.AsNoTracking()
            .Include(value => value.Proposal)
            .Where(value => decisionIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken);
        var latest = await db.TextCorrectionDecisionEvents.AsNoTracking()
            .Where(value => decisions.Values.Select(item => item.ProposalId).Contains(value.ProposalId))
            .GroupBy(value => value.ProposalId)
            .Select(value => new { ProposalId = value.Key, Sequence = value.Max(item => item.Sequence) })
            .ToDictionaryAsync(value => value.ProposalId, value => value.Sequence, cancellationToken);
        var operations = new List<ExactTextReplacementOperation>(plan.Operations.Count);
        foreach (var reference in plan.Operations.OrderBy(value => value.Ordinal))
        {
            if (!decisions.TryGetValue(reference.DecisionId, out var decision) || decision.Proposal is null
                || decision.Action == TextCorrectionDecisionAction.Ignore
                || decision.SourceDocumentVersionId != sourceDocumentVersionId
                || decision.Proposal.DocumentVersionId != sourceDocumentVersionId
                || decision.Proposal.SourceSha256 != sourceSha256
                || decision.AnchorHash != reference.AnchorHash
                || decision.Proposal.AnchorHash != reference.AnchorHash
                || decision.ReplacementHash != reference.ReplacementHash
                || latest.GetValueOrDefault(decision.ProposalId) != decision.Sequence)
                throw new FixExecutionException(FixFailureCategory.Conflict, "correction-decision-stale");
            var raw = decision.Action == TextCorrectionDecisionAction.UseSuggestion
                ? decision.Proposal.SuggestedReplacement : decision.ManualReplacement;
            if (!TextCorrectionPrivacyContract.TryValidateReplacement(raw, out var replacement, out _))
                throw new FixExecutionException(FixFailureCategory.InvalidPlan,
                    TextCorrectionPrivacyContract.ReplacementInvalidCode);
            if (decision.Action == TextCorrectionDecisionAction.UseSuggestion)
            {
                if (decision.ManualReplacement is not null
                    || decision.ReplacementHash != decision.Proposal.SuggestionHash)
                    throw new FixExecutionException(FixFailureCategory.InvalidPlan, "correction-decision-invalid");
            }
            else if (replacement!.Fingerprint != decision.ReplacementHash)
                throw new FixExecutionException(FixFailureCategory.InvalidPlan, "correction-decision-invalid");
            ExactTextAnchor anchor;
            try { anchor = ExactTextAnchorJson.Deserialize(decision.Proposal.AnchorEvidenceJson); }
            catch (InvalidDataException exception)
            { throw new FixExecutionException(FixFailureCategory.InvalidPlan, "correction-anchor-invalid", exception); }
            if (anchor.AnchorHash != reference.AnchorHash)
                throw new FixExecutionException(FixFailureCategory.InvalidPlan, "correction-anchor-invalid");
            operations.Add(new(decision.Id, anchor, replacement!));
        }
        return operations;
    }
}
