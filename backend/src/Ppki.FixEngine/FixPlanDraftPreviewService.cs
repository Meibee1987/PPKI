using Ppki.Application;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed class FixPlanDraftPreviewService(
    IFixPlanDraftRepository repository,
    IFixEligibilityService eligibility,
    IRemediationCapabilityRegistry previewCapabilities,
    FixApplyCapabilityRegistry applyCapabilities,
    IFixPlanConflictAnalyzer? conflictAnalyzer = null) : IFixPlanDraftPreviewService, IFixPlanApprovalPreviewBuilder
{
    public const string SchemaVersion = "fix-plan-draft-preview/1.0";

    public async Task<FixPlanDraftPreviewDto?> PreviewAsync(
        Guid auditId,
        Guid planId,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        var aggregate = await repository.LoadOwnedAsync(auditId, planId, ownerUserId, cancellationToken);
        if (aggregate is null) return null;
        return Build(aggregate, auditId).Preview;
    }

    public FixPlanApprovalEvaluation Build(FixPlanDraftAggregate aggregate, Guid auditId)
    {
        ValidatePlan(aggregate, auditId);

        var sourceByFinding = aggregate.Source.Findings.ToDictionary(value => value.Finding.Id);
        var outcomes = aggregate.Plan.Items
            .OrderBy(value => sourceByFinding[value.FindingId].Snapshot.RuleOrdinal)
            .ThenBy(value => value.FindingId)
            .Select(value => PreviewItem(value, aggregate.Source, sourceByFinding[value.FindingId]))
            .ToArray();
        var items = outcomes.Select(value => value.Item).ToArray();

        var previewable = items.Count(value => value.PreviewState == FixPlanDraftPreviewItemState.Previewable);
        var ineligible = items.Count(value => value.PreviewState == FixPlanDraftPreviewItemState.Ineligible);
        var unavailable = items.Length - previewable - ineligible;
        var state = previewable == items.Length ? FixPlanDraftPreviewState.Ready
            : previewable > 0 ? FixPlanDraftPreviewState.PartiallyAvailable
            : FixPlanDraftPreviewState.Unavailable;

        var analysis = (conflictAnalyzer ?? new DeterministicFixPlanConflictAnalyzer()).Analyze(
            aggregate.Plan.SourceDocumentVersionId, outcomes.Select(value => value.Candidate).ToArray());
        state = analysis.State switch
        {
            FixPlanMutationAnalysisState.Conflict => FixPlanDraftPreviewState.Conflict,
            FixPlanMutationAnalysisState.Stale => FixPlanDraftPreviewState.Stale,
            _ => state
        };
        var preview = new FixPlanDraftPreviewDto(SchemaVersion, aggregate.Plan.Id, auditId, aggregate.Plan.SourceDocumentVersionId,
            aggregate.Source.Audit.DocumentVersion!.Sha256, aggregate.Plan.State, state,
            items.Length, previewable, ineligible, unavailable, items, analysis);
        var analysisByItem = analysis.Items.ToDictionary(value => value.ItemId);
        var materials = outcomes.Where(value => value.Capability is not null && value.Operation is not null
                && value.Change is not null && analysisByItem.ContainsKey(value.Item.ItemId))
            .Select(value => new FixPlanApprovalItemMaterial(value.Item.ItemId, value.Finding.Snapshot,
                value.Finding.Finding.Confidence, value.Finding.Finding.SourceSectionSnapshot,
                value.Finding.Finding.PdfPageSnapshot, value.Finding.Finding.PrintedPageSnapshot,
                value.Item.RequiresExplicitApproval,
                value.Capability!.CapabilityId, value.Capability.CapabilityVersion,
                new(value.Capability.OperationKind, value.Capability.CapabilityId,
                    value.Capability.CapabilityVersion, value.Finding.Snapshot.RuleCode,
                    value.Finding.Snapshot.ValidationKey, [value.Finding.Finding.Id], value.Operation!.Target,
                    value.Operation.PropertyIdentifier, value.Operation.Expected,
                    value.Finding.Snapshot.FixMode == FixMode.Confirm,
                    analysisByItem[value.Item.ItemId].ExecutionOrdinal ?? 0,
                    value.Operation.PreconditionCode, value.Operation.SummaryCode),
                value.Item, value.Change!, analysisByItem[value.Item.ItemId])).ToArray();
        return new(preview, materials);
    }

    private PreviewOutcome PreviewItem(
        FixPlanItemRecord item,
        FixPlanDraftSource source,
        FixPlanDraftFindingSource finding)
    {
        var evaluation = eligibility.Evaluate(new(source.Audit.Id, source.Audit.Status,
            source.SourceDocumentVersionId, finding.Snapshot, finding.Finding.Confidence,
            finding.ResolutionState, finding.ReviewState));
        if (!evaluation.IsEligible)
            return Outcome(item, finding, evaluation, source.SourceDocumentVersionId,
                FixPlanDraftPreviewItemState.Ineligible, "fix-plan-preview-item-ineligible");

        if (!previewCapabilities.TryGet(finding.Snapshot.ValidationKey, out var capability)
            || !capability.DocumentMutationImplementationExists)
            return Outcome(item, finding, evaluation, source.SourceDocumentVersionId,
                FixPlanDraftPreviewItemState.Unavailable, "fix-preview-provider-not-registered");

        var applyAvailability = applyCapabilities.GetAvailability(
            capability.CapabilityId, capability.CapabilityVersion);
        if (applyAvailability != FixApplyProviderAvailability.Available)
            return Outcome(item, finding, evaluation, source.SourceDocumentVersionId,
                FixPlanDraftPreviewItemState.Unavailable,
                applyAvailability == FixApplyProviderAvailability.VersionIncompatible
                    ? "fix-apply-provider-version-incompatible"
                    : "fix-apply-provider-not-registered", capability);

        try
        {
            if (!capability.Provider.TryCreate(finding.Snapshot, out var operation, out _))
                return Outcome(item, finding, evaluation, source.SourceDocumentVersionId,
                    FixPlanDraftPreviewItemState.Unavailable, "fix-preview-provider-rejected-snapshot", capability);
            if (!capability.Provider.TryCreateBeforeAfter(finding.Snapshot, operation, out var change))
                return Outcome(item, finding, evaluation, source.SourceDocumentVersionId,
                    FixPlanDraftPreviewItemState.Unavailable, "fix-preview-before-after-unavailable", capability);

            return Outcome(item, finding, evaluation, source.SourceDocumentVersionId,
                FixPlanDraftPreviewItemState.Previewable, "fix-plan-preview-ready", capability, operation, change);
        }
        catch (Exception)
        {
            return Outcome(item, finding, evaluation, source.SourceDocumentVersionId,
                FixPlanDraftPreviewItemState.Unavailable, "fix-preview-provider-failed", capability);
        }
    }

    private static PreviewOutcome Outcome(
        FixPlanItemRecord item,
        FixPlanDraftFindingSource finding,
        FixEligibilityResult evaluation,
        Guid sourceDocumentVersionId,
        FixPlanDraftPreviewItemState state,
        string reason,
        RemediationCapability? capability = null,
        FixOperationDraft? operation = null,
        FixPlanDraftBeforeAfterDto? change = null) => new(new(
            item.Id, finding.Finding.Id, finding.Snapshot.RuleCode, finding.Snapshot.ValidationKey,
            finding.Snapshot.FixMode, finding.Finding.Confidence, evaluation.Status,
            evaluation.ReasonCode, evaluation.RequiresExplicitApproval, state, reason,
            capability?.CapabilityId, capability?.CapabilityVersion, operation?.PropertyIdentifier,
            operation is null ? null : new(operation.Target.Scope, operation.Target.BodyElementIndex,
                operation.Target.SectionIndex, operation.Target.ParagraphIndex, operation.Target.RunIndex),
            change), new(sourceDocumentVersionId, item.Id, finding.Finding.Id, finding.Snapshot.FixMode,
                state, reason, capability, operation), finding, capability, operation, change);

    private static void ValidatePlan(FixPlanDraftAggregate aggregate, Guid routeAuditId)
    {
        var plan = aggregate.Plan;
        var source = aggregate.Source;
        if (plan.State != FixPlanLifecycleState.Draft)
            throw new FixPlanDraftPreviewException("fix-plan-preview-not-draft");
        if (plan.SourceAuditJobId != routeAuditId || source.Audit.Id != routeAuditId
            || source.Audit.DocumentVersionId != plan.SourceDocumentVersionId
            || source.SourceDocumentVersionId != plan.SourceDocumentVersionId
            || source.Audit.DocumentVersion?.Id != plan.SourceDocumentVersionId)
            throw new FixPlanDraftPreviewException("fix-plan-preview-source-lineage-invalid");
        if (source.StaleReasonCode is not null)
            throw new FixPlanDraftPreviewException(source.StaleReasonCode);
        if (!ValidSha(source.Audit.DocumentVersion.Sha256)
            || !ValidSha(source.Audit.ResolvedRuleSetHash)
            || source.Audit.DocumentKindSnapshot is null)
            throw new FixPlanDraftPreviewException("fix-plan-preview-source-snapshot-invalid");

        var planIds = plan.Items.Select(value => value.FindingId).Order().ToArray();
        var sourceIds = source.Findings.Select(value => value.Finding.Id).Order().ToArray();
        if (planIds.Length == 0 || !planIds.SequenceEqual(sourceIds)
            || source.Findings.Any(value => value.Finding.AuditJobId != routeAuditId))
            throw new FixPlanDraftPreviewException("fix-plan-preview-membership-invalid");
    }

    private static bool ValidSha(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record PreviewOutcome(
        FixPlanDraftPreviewItemDto Item,
        FixPlanMutationCandidate Candidate,
        FixPlanDraftFindingSource Finding,
        RemediationCapability? Capability,
        FixOperationDraft? Operation,
        FixPlanDraftBeforeAfterDto? Change);
}
