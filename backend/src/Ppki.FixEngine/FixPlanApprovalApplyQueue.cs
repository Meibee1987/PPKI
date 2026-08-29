using System.Text.Json;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed class FixPlanApprovalApplyQueue(
    IFixExecutionRepository executions,
    TimeProvider timeProvider) : IFixPlanApprovalApplyQueue
{
    public const string PlannerVersion = "fix-plan-approved-snapshot/1.0";

    public Task<FixExecutionEnqueueResult> EnqueueAsync(FixPlanRecord plan,
        FixPlanApprovalSnapshotRecord persisted, CancellationToken cancellationToken)
    {
        if (plan.State != FixPlanLifecycleState.Approved || plan.ApproverUserId is null
            || plan.ApprovedAt is null || plan.Id != persisted.FixPlanId)
            throw new FixPlanApprovalException("fix-plan-approval-not-committed");
        var approved = FixPlanApprovalSnapshotSerializer.Deserialize(persisted.SnapshotJson);
        if (approved.PlanId != plan.Id || approved.AuditId != plan.SourceAuditJobId
            || approved.SourceDocumentVersionId != plan.SourceDocumentVersionId
            || approved.PlanHash != persisted.PlanHash
            || approved.SourceVersionSha256 != persisted.SourceVersionSha256
            || approved.ApprovedByUserId != plan.ApproverUserId
            || !SameDatabaseTimestamp(approved.ApprovedAt, plan.ApprovedAt.Value)
            || approved.Items.Count == 0)
            throw new FixPlanApprovalException("fix-plan-approval-snapshot-invalid");
        if (!Enum.TryParse<DocumentKind>(approved.DocumentKindSnapshot, out var documentKind))
            throw new FixPlanApprovalException("fix-plan-approval-snapshot-invalid");

        var orderedItems = approved.Items.OrderBy(value => value.RuleOrdinal)
            .ThenBy(value => value.FindingId).ToArray();
        if (orderedItems.Select(value => value.ItemId).Distinct().Count() != orderedItems.Length
            || orderedItems.Select(value => value.FindingId).Distinct().Count() != orderedItems.Length
            || orderedItems.Any(value => !value.ExplicitlyApproved))
            throw new FixPlanApprovalException("fix-plan-approval-snapshot-invalid");
        var operations = orderedItems.GroupBy(value => value.Operation.Ordinal)
            .OrderBy(value => value.Key).Select(group =>
            {
                if (group.Key <= 0) throw new FixPlanApprovalException("fix-plan-approval-snapshot-invalid");
                var first = group.First().Operation;
                if (group.Any(value => !Equivalent(first, value.Operation)))
                    throw new FixPlanApprovalException("fix-plan-approval-snapshot-invalid");
                return first with
                {
                    SourceFindingIds = group.Select(value => value.FindingId).Order().ToArray()
                };
            }).ToArray();

        var source = new FixPlanSource(approved.AuditId, AuditJobStatus.Completed,
            approved.SourceDocumentVersionId, approved.SourceVersionSha256,
            approved.ResolvedRuleSetHash, documentKind, orderedItems.Select(value => new FixPlanFindingSnapshot(
                value.FindingId, value.RuleOrdinal, value.RuleCode, string.Empty, string.Empty,
                value.ValidationKey, value.Severity, value.FixMode, value.FindingState,
                value.ActualValueJson, value.ExpectedValueJson, value.LocationJson,
                value.RuleSnapshotSchemaVersion, value.SourceReferenceJson)).ToArray());
        var preview = new FixPlanPreview(approved.AuditId, approved.SourceDocumentVersionId,
            approved.SourceVersionSha256, approved.ResolvedRuleSetHash, approved.DocumentKindSnapshot,
            PlannerVersion, orderedItems.Length, orderedItems.Length, 0, 0, 0,
            orderedItems.Select(value => new FixPlanItem(value.FindingId, value.RuleCode,
                value.ValidationKey, value.RuleOrdinal, FixPlanItemDisposition.Planned,
                "fix-plan-approved")).ToArray(), operations, [], persisted.PlanHash,
            FixPlanState.Ready, []);
        var selected = orderedItems.Select(value => value.FindingId).Order().ToArray();
        var candidate = new FixExecutionCandidate(Guid.NewGuid(), approved.AuditId,
            approved.SourceDocumentVersionId, approved.ApprovedByUserId, plan.Id, persisted.PlanHash,
            PlannerVersion, JsonSerializer.Serialize(selected.Select(value => value.ToString("D")).ToArray()),
            ApprovedFixExecutionPlanSerializer.Serialize(source, preview, FixExecutionSelectionScope.Manual),
            operations.Length, timeProvider.GetUtcNow(), plan.Id);
        return executions.EnqueueAsync(candidate, cancellationToken);
    }

    private static bool Equivalent(FixPlanOperation left, FixPlanOperation right) =>
        left.OperationKind == right.OperationKind
        && left.CapabilityId == right.CapabilityId
        && left.CapabilityVersion == right.CapabilityVersion
        && left.Target == right.Target
        && left.PropertyIdentifier == right.PropertyIdentifier
        && left.Expected == right.Expected
        && left.RequiresConfirmation == right.RequiresConfirmation
        && left.PreconditionCode == right.PreconditionCode
        && left.SummaryCode == right.SummaryCode;

    private static bool SameDatabaseTimestamp(DateTimeOffset left, DateTimeOffset right) =>
        left.ToUniversalTime().Ticks / 10 == right.ToUniversalTime().Ticks / 10;
}
