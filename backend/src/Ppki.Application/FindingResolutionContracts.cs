using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

public sealed class FindingResolutionException(string diagnosticCode) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingResolutionReconciliationState>))]
public enum FindingResolutionReconciliationState { Pending, Completed }

public sealed record FindingResolutionEventDto(
    int Sequence,
    FindingResolutionEventType EventType,
    Guid? FixExecutionId,
    Guid? ReAuditId,
    Guid? ResultDocumentVersionId,
    Guid? ResultFindingId,
    AuditComparisonStatus? ComparisonStatus,
    DateTimeOffset SourceOccurredAt,
    DateTimeOffset CreatedAt);

public sealed record FindingResolutionDto(
    Guid FindingId,
    Guid AuditId,
    FindingResolutionState CurrentState,
    Guid? ResolutionCaseId,
    Guid SourceDocumentVersionId,
    Guid? ResultDocumentVersionId,
    Guid? FixExecutionId,
    Guid? ReAuditId,
    Guid? ResultFindingId,
    AuditComparisonStatus? ComparisonStatus,
    int EventCount,
    DateTimeOffset? LatestEventAt,
    IReadOnlyList<FindingResolutionEventDto> Events);

public sealed record FindingResolutionReconciliationResult(
    Guid FixExecutionId,
    Guid ReAuditId,
    FindingResolutionReconciliationState State,
    int SelectedFindingCount,
    int CaseCount,
    int EventCount,
    int EventsCreated,
    bool Replayed);

public interface IFindingResolutionService
{
    Task<FindingResolutionDto?> GetAsync(Guid auditId, Guid findingId, Guid ownerUserId, CancellationToken cancellationToken);
    Task<FindingResolutionReconciliationResult?> ReconcileAsync(Guid fixExecutionId, Guid ownerUserId, CancellationToken cancellationToken);
}

public static class FindingResolutionProjection
{
    public static FindingResolutionState State(FindingResolutionEventType? eventType) => eventType switch
    {
        FindingResolutionEventType.FixAppliedObserved => FindingResolutionState.Applied,
        FindingResolutionEventType.ReauditPendingObserved => FindingResolutionState.ReauditPending,
        FindingResolutionEventType.VerificationResolvedObserved => FindingResolutionState.VerifiedResolved,
        FindingResolutionEventType.VerificationStillDetectedObserved => FindingResolutionState.VerifiedStillDetected,
        _ => FindingResolutionState.Open
    };

    public static FindingResolutionEventType VerificationEvent(AuditComparisonStatus status) => status switch
    {
        AuditComparisonStatus.NoLongerDetected => FindingResolutionEventType.VerificationResolvedObserved,
        AuditComparisonStatus.StillDetected or AuditComparisonStatus.Changed =>
            FindingResolutionEventType.VerificationStillDetectedObserved,
        _ => throw new FindingResolutionException("resolution-comparison-invalid")
    };
}
