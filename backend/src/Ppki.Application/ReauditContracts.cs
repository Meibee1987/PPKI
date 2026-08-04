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
