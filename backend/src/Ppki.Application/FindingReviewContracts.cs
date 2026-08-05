using Ppki.Domain;

namespace Ppki.Application;

public sealed class FindingReviewException(string diagnosticCode) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

public sealed record FindingReviewRequest(FindingReviewRequestedDisposition RequestedDisposition, string? Note);
public sealed record FindingReviewDecisionRequest(FindingReviewDecision Decision, string? Note);
public sealed record ManualRemediationReportRequest(string? Note);

public sealed record FindingReviewEventDto(
    int Sequence,
    FindingReviewEventType EventType,
    FindingReviewRequestedDisposition? RequestedDisposition,
    FindingReviewDecision? Decision,
    Guid ActorUserId,
    string? Note,
    DateTimeOffset CreatedAt);

public sealed record FindingReviewPermissions(
    bool CanRequestReview,
    bool CanReportManualRemediation,
    bool CanDecide);

public sealed record FindingReviewDto(
    Guid? ReviewCaseId,
    Guid FindingId,
    Guid AuditId,
    Guid SourceDocumentVersionId,
    FindingResolutionState ResolutionState,
    FindingReviewState ReviewState,
    FindingReviewRequestedDisposition? RequestedDisposition,
    Guid? RequestedByUserId,
    FindingReviewDecision? LatestDecision,
    int EventCount,
    DateTimeOffset? LatestEventAt,
    FindingReviewPermissions Permissions,
    IReadOnlyList<FindingReviewDecision> AllowedDecisions,
    IReadOnlyList<FindingReviewEventDto> Events);

public sealed record FindingReviewCommandResult(FindingReviewDto Review, bool Replayed);

public interface IAdminFindingReviewAuthorizationService
{
    Task<UserRole?> GetAuthoritativeRoleAsync(Guid actorUserId, CancellationToken cancellationToken);
    Task<bool> CanDecideFindingAsync(Guid actorUserId, Guid auditId, Guid findingId, CancellationToken cancellationToken);
}

public interface IFindingReviewService
{
    Task<FindingReviewDto?> GetAsync(Guid auditId, Guid findingId, Guid actorUserId, CancellationToken cancellationToken);
    Task<FindingReviewCommandResult?> RequestAsync(Guid auditId, Guid findingId, Guid actorUserId,
        Guid idempotencyKey, FindingReviewRequest request, CancellationToken cancellationToken);
    Task<FindingReviewCommandResult?> DecideAsync(Guid reviewCaseId, Guid actorUserId,
        Guid idempotencyKey, FindingReviewDecisionRequest request, CancellationToken cancellationToken);
    Task<FindingReviewCommandResult?> ReportManualRemediationAsync(Guid reviewCaseId, Guid actorUserId,
        Guid idempotencyKey, ManualRemediationReportRequest request, CancellationToken cancellationToken);
}

public static class FindingReviewProjection
{
    public static FindingReviewState State(FindingReviewEventType? eventType) => eventType switch
    {
        FindingReviewEventType.ReviewRequested => FindingReviewState.PendingReview,
        FindingReviewEventType.ManualRemediationApproved => FindingReviewState.ManualRemediationApproved,
        FindingReviewEventType.ManualRemediationReported => FindingReviewState.ManualRemediationReported,
        FindingReviewEventType.NeedsRevision => FindingReviewState.NeedsRevision,
        FindingReviewEventType.Rejected => FindingReviewState.Rejected,
        FindingReviewEventType.Ignored => FindingReviewState.Ignored,
        FindingReviewEventType.AcceptedRisk => FindingReviewState.AcceptedRisk,
        _ => FindingReviewState.NoReview
    };

    public static FindingReviewEventType Event(FindingReviewDecision decision) => decision switch
    {
        FindingReviewDecision.ApproveManualRemediation => FindingReviewEventType.ManualRemediationApproved,
        FindingReviewDecision.Ignore => FindingReviewEventType.Ignored,
        FindingReviewDecision.AcceptRisk => FindingReviewEventType.AcceptedRisk,
        FindingReviewDecision.NeedsRevision => FindingReviewEventType.NeedsRevision,
        FindingReviewDecision.Reject => FindingReviewEventType.Rejected,
        _ => throw new FindingReviewException("finding-review-invalid-transition")
    };

    public static IReadOnlyList<FindingReviewDecision> Allowed(FindingReviewRequestedDisposition disposition) => disposition switch
    {
        FindingReviewRequestedDisposition.ManualRemediation =>
            [FindingReviewDecision.ApproveManualRemediation, FindingReviewDecision.NeedsRevision, FindingReviewDecision.Reject],
        FindingReviewRequestedDisposition.Ignore =>
            [FindingReviewDecision.Ignore, FindingReviewDecision.NeedsRevision, FindingReviewDecision.Reject],
        FindingReviewRequestedDisposition.AcceptedRisk =>
            [FindingReviewDecision.AcceptRisk, FindingReviewDecision.NeedsRevision, FindingReviewDecision.Reject],
        _ => []
    };
}
