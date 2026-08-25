using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

[JsonConverter(typeof(JsonStringEnumConverter<FixEligibilityStatus>))]
public enum FixEligibilityStatus
{
    Eligible,
    Ineligible
}

[JsonConverter(typeof(JsonStringEnumConverter<FixEligibilityReasonCode>))]
public enum FixEligibilityReasonCode
{
    Eligible,
    SourceContextInvalid,
    AuditNotCompleted,
    FindingNotOpen,
    ResolutionStateBlocksFix,
    ReviewStateBlocksFix,
    ManualFixMode,
    ReportFixMode,
    FixModeUnsupported,
    ValidationKeyUnsupported,
    FixerNotRegistered,
    FixerVersionIncompatible,
    FindingContractUnsupported
}

public sealed record FixEligibilityInput(
    Guid AuditId,
    AuditJobStatus AuditStatus,
    Guid SourceDocumentVersionId,
    FixPlanFindingSnapshot Finding,
    decimal? Confidence,
    FindingResolutionState ResolutionState = FindingResolutionState.Open,
    FindingReviewState ReviewState = FindingReviewState.NoReview);

public sealed record FixEligibilityResult(
    Guid FindingId,
    FixMode FixMode,
    decimal? Confidence,
    FixEligibilityStatus Status,
    FixEligibilityReasonCode ReasonCode,
    bool RequiresExplicitApproval)
{
    public bool IsEligible => Status == FixEligibilityStatus.Eligible;
}

public interface IFixEligibilityService
{
    FixEligibilityResult Evaluate(FixEligibilityInput input);
}
