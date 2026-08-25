using Ppki.Application;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed class FixPlanApprovalService(
    IFixPlanApprovalRepository repository,
    IFixPlanApprovalPreviewBuilder previewBuilder,
    IFixPlanApprovalApplyQueue applyQueue,
    TimeProvider timeProvider) : IFixPlanApprovalService
{
    public async Task<FixPlanApprovalDto?> ApproveAsync(Guid auditId, Guid planId, Guid ownerUserId,
        IReadOnlyList<Guid> approvedConfirmItemIds, CancellationToken cancellationToken)
    {
        if (auditId == Guid.Empty || planId == Guid.Empty || ownerUserId == Guid.Empty)
            throw new FixPlanApprovalException("fix-plan-approval-identity-invalid");
        if (approvedConfirmItemIds.Any(value => value == Guid.Empty)
            || approvedConfirmItemIds.Distinct().Count() != approvedConfirmItemIds.Count)
            throw new FixPlanApprovalException("fix-plan-confirm-approval-invalid");
        var approved = approvedConfirmItemIds.ToHashSet();
        var requestHash = FixPlanApprovalSnapshotSerializer.ApprovalRequestHash(approved);
        var now = timeProvider.GetUtcNow();
        var result = await repository.ApproveAsync(auditId, planId, ownerUserId, requestHash, now,
            aggregate => Prepare(aggregate, auditId, ownerUserId, approved, now), cancellationToken);
        if (result.ConflictCode is not null) throw new FixPlanApprovalException(result.ConflictCode);
        if (result.Plan is null || result.Snapshot is null) return null;
        var enqueue = await applyQueue.EnqueueAsync(result.Plan, result.Snapshot, cancellationToken);
        if (enqueue.ConflictCode is not null) throw new FixPlanApprovalException(enqueue.ConflictCode);
        var applyJob = enqueue.Job ?? throw new FixPlanApprovalException("fix-plan-approval-apply-queue-failed");
        return new(result.Plan.Id, result.Plan.SourceAuditJobId, result.Plan.SourceDocumentVersionId,
            result.Plan.State, result.Snapshot.SchemaVersion, result.Snapshot.PlanHash,
            result.Snapshot.SourceVersionSha256, result.Snapshot.ApprovedByUserId,
            result.Snapshot.ApprovedAt, result.Plan.Items.Count, applyJob.Id,
            applyJob.State.ToString(), enqueue.IsReplay, result.Replayed);
    }

    private FixPlanApprovalPrepared Prepare(FixPlanDraftAggregate aggregate, Guid auditId,
        Guid ownerUserId, IReadOnlySet<Guid> approved, DateTimeOffset now)
    {
        var evaluation = previewBuilder.Build(aggregate, auditId);
        var preview = evaluation.Preview;
        if (preview.State != FixPlanDraftPreviewState.Ready || preview.PreviewableCount != preview.ItemCount
            || preview.Items.Any(value => value.Eligibility != FixEligibilityStatus.Eligible
                || value.PreviewState != FixPlanDraftPreviewItemState.Previewable))
            throw new FixPlanApprovalException("fix-plan-approval-preview-not-ready");
        if (preview.MutationAnalysis is null || preview.MutationAnalysis.State != FixPlanMutationAnalysisState.Ready
            || preview.MutationAnalysis.ConflictItemCount != 0 || preview.MutationAnalysis.StaleItemCount != 0
            || preview.MutationAnalysis.Items.Any(value => value.Status is FixPlanMutationItemStatus.Conflicting
                or FixPlanMutationItemStatus.DependencyCycle or FixPlanMutationItemStatus.Stale
                or FixPlanMutationItemStatus.Ineligible or FixPlanMutationItemStatus.Unavailable))
            throw new FixPlanApprovalException("fix-plan-approval-mutation-analysis-not-ready");
        if (evaluation.Items.Count != preview.ItemCount)
            throw new FixPlanApprovalException("fix-plan-approval-operation-missing");
        if (evaluation.Items.Any(value => value.Finding.FixMode is FixMode.Manual or FixMode.Report))
            throw new FixPlanApprovalException("fix-plan-approval-fix-mode-unsupported");
        var required = evaluation.Items.Where(value => value.Finding.FixMode == FixMode.Confirm)
            .Select(value => value.ItemId).ToHashSet();
        if (!required.SetEquals(approved))
            throw new FixPlanApprovalException("fix-plan-confirm-approval-required");
        return FixPlanApprovalSnapshotSerializer.Create(aggregate, evaluation, approved,
            ownerUserId, now);
    }
}
