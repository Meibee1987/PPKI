using System.Text.Json.Serialization;

namespace Ppki.Domain;

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

public enum FixExecutionState
{
    Queued,
    Processing,
    Completed,
    Failed,
    NoChange
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
