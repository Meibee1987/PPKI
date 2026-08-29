using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

public sealed record FixExecutionRequest(string[]? FindingIds, string? PlanHash);

[JsonConverter(typeof(JsonStringEnumConverter<FixExecutionSelectionScope>))]
public enum FixExecutionSelectionScope
{
    Manual,
    Automatic
}

public sealed record ApprovedFixExecutionPlan(
    string SchemaVersion,
    FixPlanSource Source,
    FixPlanPreview Preview,
    FixExecutionSelectionScope SelectionScope = FixExecutionSelectionScope.Manual);

public static class ApprovedFixExecutionPlanSerializer
{
    public const string SchemaVersion = "fix-execution-plan/1.1";
    public const string LegacySchemaVersion = "fix-execution-plan/1.0";
    public const int MaximumAutomaticFindingCount = AuditFindingQuery.MaximumFindingCount;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(FixPlanSource source, FixPlanPreview preview,
        FixExecutionSelectionScope selectionScope = FixExecutionSelectionScope.Manual) =>
        JsonSerializer.Serialize(new ApprovedFixExecutionPlan(SchemaVersion, source, preview, selectionScope), Options);

    public static ApprovedFixExecutionPlan Deserialize(string json)
    {
        var result = JsonSerializer.Deserialize<ApprovedFixExecutionPlan>(json, Options)
            ?? throw new FixExecutionException("fix-execution-snapshot-invalid");
        if (!string.Equals(result.SchemaVersion, SchemaVersion, StringComparison.Ordinal)
            && !string.Equals(result.SchemaVersion, LegacySchemaVersion, StringComparison.Ordinal))
            throw new FixExecutionException("fix-execution-snapshot-version-unsupported");
        if (string.Equals(result.SchemaVersion, LegacySchemaVersion, StringComparison.Ordinal)
            && result.SelectionScope != FixExecutionSelectionScope.Manual)
            throw new FixExecutionException("fix-execution-snapshot-invalid");
        return result;
    }

    public static int MaximumSelectionCount(ApprovedFixExecutionPlan plan) =>
        plan.SelectionScope == FixExecutionSelectionScope.Automatic
            ? MaximumAutomaticFindingCount
            : FixPlanSelection.MaximumFindingCount;
}

public sealed class FixExecutionException : Exception
{
    public FixExecutionException(string diagnosticCode) : this(FixFailureCatalog.Classify(diagnosticCode), diagnosticCode) { }
    public FixExecutionException(FixFailureCategory category, string diagnosticCode, Exception? innerException = null)
        : base(diagnosticCode, innerException)
    {
        Category = category;
        DiagnosticCode = diagnosticCode;
    }
    public FixFailureCategory Category { get; }
    public string DiagnosticCode { get; }
    public bool Retryable => Category == FixFailureCategory.TransientInfrastructure;
}

public interface IFixApplyCapabilityResolver
{
    bool CanApply(FixPlanOperation operation);
}

public sealed record FixExecutionCandidate(
    Guid ExecutionId,
    Guid AuditJobId,
    Guid SourceDocumentVersionId,
    Guid RequestedByUserId,
    Guid IdempotencyKey,
    string PlanHash,
    string PlannerVersion,
    string SelectedFindingIdsJson,
    string ApprovedPlanSnapshotJson,
    int PlannedOperationCount,
    DateTimeOffset CreatedAt,
    Guid? FixPlanId = null);

public sealed record FixExecutionEnqueueResult(
    FixExecutionJob? Job,
    bool IsReplay,
    string? ConflictCode = null);

public interface IFixExecutionRepository
{
    Task<FixExecutionEnqueueResult> EnqueueAsync(FixExecutionCandidate candidate, CancellationToken cancellationToken);
    Task<FixExecutionJob?> GetOwnedAsync(Guid executionId, Guid ownerUserId, CancellationToken cancellationToken);
}

public sealed record FixExecutionAccepted(
    Guid Id,
    Guid AuditId,
    Guid SourceDocumentVersionId,
    string PlanHash,
    string PlannerVersion,
    string State,
    int SelectedFindingCount,
    int PlannedOperationCount,
    DateTimeOffset QueuedAt,
    string StatusCode,
    string StatusMessage,
    bool Replayed);

public sealed record FixExecutionStatus(
    Guid Id,
    Guid AuditId,
    Guid SourceDocumentVersionId,
    Guid? ResultDocumentVersionId,
    string PlanHash,
    string State,
    int PlannedOperationCount,
    int CompletedOperationCount,
    int FailedOperationCount,
    string? ResultSha256,
    string? FailureCategory,
    string? SafeFailureCode,
    int AttemptCount,
    int MaxAttempts,
    bool RetryPending,
    string LeaseState,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public interface IFixExecutionService
{
    Task<FixExecutionAccepted?> AcceptAsync(Guid auditId, Guid ownerUserId, Guid idempotencyKey,
        FixPlanSelection selection, string planHash, CancellationToken cancellationToken);
    Task<FixExecutionAccepted?> AcceptAutomaticAsync(Guid auditId, Guid ownerUserId, Guid idempotencyKey,
        FixPlanSelection selection, string planHash, CancellationToken cancellationToken);
    Task<FixExecutionStatus?> GetAsync(Guid executionId, Guid ownerUserId, CancellationToken cancellationToken);
}

public sealed class FixExecutionService(
    IFixPlanSourceReader sourceReader,
    IFixPlanPreviewPlanner planner,
    IFixApplyCapabilityResolver applyCapabilities,
    IFixExecutionRepository repository,
    TimeProvider timeProvider) : IFixExecutionService
{
    public async Task<FixExecutionAccepted?> AcceptAsync(Guid auditId, Guid ownerUserId, Guid idempotencyKey,
        FixPlanSelection selection, string planHash, CancellationToken cancellationToken) =>
        await AcceptAsync(auditId, ownerUserId, idempotencyKey, selection, planHash,
            FixExecutionSelectionScope.Manual, cancellationToken);

    public async Task<FixExecutionAccepted?> AcceptAutomaticAsync(Guid auditId, Guid ownerUserId,
        Guid idempotencyKey, FixPlanSelection selection, string planHash, CancellationToken cancellationToken) =>
        await AcceptAsync(auditId, ownerUserId, idempotencyKey, selection, planHash,
            FixExecutionSelectionScope.Automatic, cancellationToken);

    private async Task<FixExecutionAccepted?> AcceptAsync(Guid auditId, Guid ownerUserId, Guid idempotencyKey,
        FixPlanSelection selection, string planHash, FixExecutionSelectionScope selectionScope,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty) throw new FixExecutionException("fix-execution-idempotency-key-invalid");
        if (!ValidSha(planHash)) throw new FixExecutionException("fix-execution-plan-hash-invalid");

        var source = await sourceReader.LoadAsync(auditId, ownerUserId, selection, cancellationToken);
        if (source is null) return null;
        var preview = planner.Create(source);
        if (preview.State != FixPlanState.Ready)
            throw new FixExecutionException("fix-execution-plan-not-ready");
        if (!FixedTimeEquals(preview.PlanHash, planHash))
            throw new FixExecutionException("fix-plan-stale");
        if (preview.Operations.Count == 0 || preview.Operations.Any(operation => !applyCapabilities.CanApply(operation)))
            throw new FixExecutionException("fix-execution-apply-capability-unavailable");

        var idsJson = JsonSerializer.Serialize(selection.FindingIds.Select(value => value.ToString("D")).ToArray());
        var candidate = new FixExecutionCandidate(Guid.NewGuid(), auditId, source.DocumentVersionId,
            ownerUserId, idempotencyKey, preview.PlanHash, preview.PlannerVersion, idsJson,
            ApprovedFixExecutionPlanSerializer.Serialize(source, preview, selectionScope), preview.Operations.Count,
            timeProvider.GetUtcNow());
        var result = await repository.EnqueueAsync(candidate, cancellationToken);
        if (result.ConflictCode is not null) throw new FixExecutionException(result.ConflictCode);
        var job = result.Job ?? throw new FixExecutionException("fix-execution-persistence-failed");
        return new(job.Id, job.AuditJobId, job.SourceDocumentVersionId, job.PlanHash,
            job.PlannerVersion, job.State.ToString(), selection.FindingIds.Count,
            job.PlannedOperationCount, job.CreatedAt, result.IsReplay ? "fix-execution-replayed" : "fix-execution-queued",
            result.IsReplay ? "Existing fix execution returned." : "Fix execution queued.",
            result.IsReplay);
    }

    public async Task<FixExecutionStatus?> GetAsync(Guid executionId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var job = await repository.GetOwnedAsync(executionId, ownerUserId, cancellationToken);
        return job is null ? null : new(job.Id, job.AuditJobId, job.SourceDocumentVersionId,
            job.ResultDocumentVersionId, job.PlanHash, job.State.ToString(), job.PlannedOperationCount,
            job.CompletedOperationCount, job.FailedOperationCount, job.ResultSha256,
            job.FailureCategory?.ToString(), job.SafeFailureCode, job.AttemptCount, job.MaxAttempts,
            job.State == FixExecutionState.Queued && job.AttemptCount > 0,
            job.State == FixExecutionState.Processing ? "active" : job.State == FixExecutionState.Queued && job.AttemptCount > 0 ? "retry-pending" : "none",
            job.CreatedAt, job.StartedAt, job.CompletedAt);
    }

    private static bool ValidSha(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedTimeEquals(string expected, string supplied) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(supplied));
}
