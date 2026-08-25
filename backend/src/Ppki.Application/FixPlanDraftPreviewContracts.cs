using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanDraftPreviewState>))]
public enum FixPlanDraftPreviewState
{
    Ready,
    PartiallyAvailable,
    Conflict,
    Stale,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanDraftPreviewItemState>))]
public enum FixPlanDraftPreviewItemState
{
    Previewable,
    Ineligible,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanMutationAnalysisState>))]
public enum FixPlanMutationAnalysisState
{
    Ready,
    PartiallyAvailable,
    Conflict,
    Stale,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanMutationItemStatus>))]
public enum FixPlanMutationItemStatus
{
    Independent,
    Ordered,
    DuplicateEquivalent,
    Conflicting,
    Stale,
    Ineligible,
    Unavailable,
    DependencyCycle
}

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanMutationRelationshipKind>))]
public enum FixPlanMutationRelationshipKind
{
    Independent,
    RequiresBefore,
    RequiresAfter,
    DuplicateEquivalent,
    Conflicting
}

public sealed record FixPlanMutationKeyDto(
    Guid SourceDocumentVersionId,
    string Scope,
    int? BodyElementIndex,
    int? SectionIndex,
    int? ParagraphIndex,
    int? RunIndex,
    string PropertyIdentifier);

public sealed record FixPlanMutationAnalysisItemDto(
    Guid ItemId,
    Guid FindingId,
    FixPlanMutationItemStatus Status,
    string ReasonCode,
    FixPlanMutationKeyDto? MutationKey,
    int? ExecutionOrdinal,
    IReadOnlyList<Guid> RelatedItemIds);

public sealed record FixPlanMutationRelationshipDto(
    Guid ItemId,
    Guid RelatedItemId,
    FixPlanMutationRelationshipKind Kind,
    Guid? BeforeItemId,
    Guid? AfterItemId,
    string ReasonCode);

public sealed record FixPlanMutationConflictDto(
    FixPlanMutationKeyDto? MutationKey,
    IReadOnlyList<Guid> ItemIds,
    string ReasonCode);

public sealed record FixPlanMutationAnalysisDto(
    string SchemaVersion,
    FixPlanMutationAnalysisState State,
    int AnalyzableItemCount,
    int IndependentItemCount,
    int OrderedItemCount,
    int DuplicateEquivalentItemCount,
    int ConflictItemCount,
    int StaleItemCount,
    IReadOnlyList<FixPlanMutationAnalysisItemDto> Items,
    IReadOnlyList<FixPlanMutationRelationshipDto> Relationships,
    IReadOnlyList<FixPlanMutationConflictDto> Conflicts,
    IReadOnlyList<string> ReasonCodes);

public sealed record FixPlanDraftBeforeAfterDto(
    string Kind,
    string PropertyLabel,
    string BeforeLabel,
    string? BeforeValue,
    string AfterLabel,
    string? AfterValue,
    string EvidenceState);

public sealed record FixPlanDraftPreviewLocationDto(
    string Scope,
    int? BodyElementIndex,
    int? SectionIndex,
    int? ParagraphIndex,
    int? RunIndex);

public sealed record FixPlanDraftPreviewItemDto(
    Guid ItemId,
    Guid FindingId,
    string RuleCode,
    string ValidationKey,
    FixMode FixMode,
    decimal? Confidence,
    FixEligibilityStatus Eligibility,
    FixEligibilityReasonCode EligibilityReason,
    bool RequiresExplicitApproval,
    FixPlanDraftPreviewItemState PreviewState,
    string ReasonCode,
    string? CapabilityId,
    string? CapabilityVersion,
    string? PropertyIdentifier,
    FixPlanDraftPreviewLocationDto? Location,
    FixPlanDraftBeforeAfterDto? Change);

public sealed record FixPlanDraftPreviewDto(
    string SchemaVersion,
    Guid PlanId,
    Guid AuditId,
    Guid SourceDocumentVersionId,
    string SourceVersionSha256,
    FixPlanLifecycleState PlanState,
    FixPlanDraftPreviewState State,
    int ItemCount,
    int PreviewableCount,
    int IneligibleCount,
    int UnavailableCount,
    IReadOnlyList<FixPlanDraftPreviewItemDto> Items,
    FixPlanMutationAnalysisDto? MutationAnalysis = null);

public sealed class FixPlanDraftPreviewException(string diagnosticCode) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

public interface IFixPlanDraftPreviewService
{
    Task<FixPlanDraftPreviewDto?> PreviewAsync(
        Guid auditId,
        Guid planId,
        Guid ownerUserId,
        CancellationToken cancellationToken);
}
