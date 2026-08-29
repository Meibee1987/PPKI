using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

public sealed class ReauditException(string diagnosticCode) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

public sealed record ReauditAccepted(
    Guid AuditId,
    string Status,
    Guid SourceAuditId,
    Guid SourceFixExecutionId,
    Guid DocumentVersionId,
    Guid ProfileVersionId,
    string ResolvedRuleSetHash,
    DocumentKind DocumentKindSnapshot,
    DateTimeOffset QueuedAt,
    bool Replayed);

public interface IReauditService
{
    Task<ReauditAccepted?> CreateAsync(
        Guid sourceFixExecutionId,
        Guid ownerUserId,
        CancellationToken cancellationToken);
}

public interface IResolvedRuleSetHasher
{
    string Hash(IEnumerable<AuditRuleSnapshot> snapshots);
}

[JsonConverter(typeof(JsonStringEnumConverter<AutomaticFindingReconciliationOutcome>))]
public enum AutomaticFindingReconciliationOutcome
{
    Fixed,
    StillFailing,
    PartiallyFixed
}

public sealed record AutomaticReauditChainStatus(
    Guid AuditId,
    AuditJobStatus Status,
    Guid DocumentVersionId,
    Guid ProfileVersionId,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record AutomaticFindingReconciliationStatus(
    Guid SourceFindingId,
    FindingResolutionState State,
    AutomaticFindingReconciliationOutcome? Outcome,
    AuditComparisonStatus? ComparisonStatus,
    Guid? ResultFindingId);

public sealed record FixExecutionStatusChain(
    Guid SourceAuditId,
    Guid? FixPlanId,
    FixPlanLifecycleState? FixPlanState,
    Guid FixExecutionId,
    FixExecutionState FixExecutionState,
    Guid SourceDocumentVersionId,
    Guid? ResultDocumentVersionId,
    AutomaticReauditChainStatus? Reaudit,
    FindingResolutionReconciliationState ReconciliationState,
    IReadOnlyList<AutomaticFindingReconciliationStatus> Findings);

public interface IFixExecutionStatusChainService
{
    Task<FixExecutionStatusChain?> GetAsync(
        Guid fixExecutionId,
        Guid ownerUserId,
        CancellationToken cancellationToken);
}
