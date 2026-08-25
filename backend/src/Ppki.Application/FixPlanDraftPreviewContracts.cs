using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanDraftPreviewState>))]
public enum FixPlanDraftPreviewState
{
    Ready,
    PartiallyAvailable,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanDraftPreviewItemState>))]
public enum FixPlanDraftPreviewItemState
{
    Previewable,
    Ineligible,
    Unavailable
}

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
    IReadOnlyList<FixPlanDraftPreviewItemDto> Items);

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
