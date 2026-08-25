using System.Text.Json.Serialization;

namespace Ppki.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<UserRole>))]
public enum UserRole
{
    Student,
    Reviewer,
    PPKIAdmin,
    UnitAdmin
}

public static class UserRoleDatabase
{
    public static UserRole ParseExact(string value) => TryParseExact(value, out var role)
        ? role : throw new ArgumentOutOfRangeException(nameof(value), "Unknown user role.");

    public static bool TryParseExact(string? value, out UserRole role)
    {
        role = value switch
        {
            "Student" => UserRole.Student,
            "Reviewer" => UserRole.Reviewer,
            "PPKIAdmin" => UserRole.PPKIAdmin,
            "UnitAdmin" => UserRole.UnitAdmin,
            _ => (UserRole)(-1)
        };
        return value is "Student" or "Reviewer" or "PPKIAdmin" or "UnitAdmin";
    }
}

public enum DocumentKind
{
    LaporanAkhir,
    Skripsi,
    Tesis,
    Disertasi
}

public enum DocumentStatus
{
    Active,
    Archived
}

public enum AuditJobStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled
}

public enum FindingStatus
{
    Open,
    Fixed,
    Ignored,
    ManualReview
}

public enum RuleSeverity
{
    Error,
    Warning,
    Info
}

public enum FixMode
{
    Auto,
    Confirm,
    Manual,
    Report
}

/// <summary>
/// PPKI Smart Formatter product policy for review readiness. This is not an
/// official IPB rule classification.
/// </summary>
public enum ReviewBlockingPolicy
{
    Unknown,
    Blocking,
    NonBlocking,
    PendingApproval
}

public enum FixExecutionState
{
    Queued,
    Processing,
    Completed,
    Failed,
    NoChange
}

public enum FixPlanLifecycleState
{
    Draft,
    Approved,
    Applying,
    Completed,
    Failed
}

public enum AutomaticRemediationState
{
    Pending,
    NoAction,
    Queued,
    Processing,
    ReauditPending,
    Completed,
    Failed,
    Conflict
}

public enum DocumentRenderState { Pending, Processing, Completed, Failed }
public enum PageMapConfidence { Exact, Estimated, Unavailable }

public enum TextCorrectionAnalysisState { Pending, Processing, Completed, Failed, Skipped }
public enum TextCorrectionDecisionAction { UseSuggestion, EditManual, Ignore }
public enum TextCorrectionBatchState
{
    Pending, Queued, Processing, ReauditPending, VerificationPending, Completed, Failed, Conflict
}
public enum TextCorrectionVerificationState
{
    Applied, ReauditPending, VerifiedResolved, VerifiedStillDetected, VerificationUnavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<FixFailureCategory>))]
public enum FixFailureCategory
{
    Conflict,
    InvalidInput,
    InvalidSource,
    InvalidPlan,
    CapabilityUnavailable,
    TransientInfrastructure,
    TerminalInfrastructure
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingResolutionState>))]
public enum FindingResolutionState
{
    Open,
    Applied,
    ReauditPending,
    VerifiedResolved,
    VerifiedStillDetected
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingResolutionEventType>))]
public enum FindingResolutionEventType
{
    FixAppliedObserved,
    ReauditPendingObserved,
    VerificationResolvedObserved,
    VerificationStillDetectedObserved
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingReviewRequestedDisposition>))]
public enum FindingReviewRequestedDisposition
{
    ManualRemediation,
    Ignore,
    AcceptedRisk
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingReviewDecision>))]
public enum FindingReviewDecision
{
    ApproveManualRemediation,
    Ignore,
    AcceptRisk,
    NeedsRevision,
    Reject
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingReviewEventType>))]
public enum FindingReviewEventType
{
    ReviewRequested,
    ManualRemediationApproved,
    ManualRemediationReported,
    NeedsRevision,
    Rejected,
    Ignored,
    AcceptedRisk
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingReviewState>))]
public enum FindingReviewState
{
    NoReview,
    PendingReview,
    NeedsRevision,
    ManualRemediationApproved,
    ManualRemediationReported,
    Rejected,
    Ignored,
    AcceptedRisk
}
