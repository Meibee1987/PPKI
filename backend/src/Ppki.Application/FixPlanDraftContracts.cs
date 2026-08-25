using System.Security.Cryptography;
using System.Text;
using Ppki.Domain;

namespace Ppki.Application;

public sealed record FixPlanDraftCreateRequest(string[]? FindingIds);
public sealed record FixPlanDraftUpdateRequest(string[]? FindingIds);

public sealed record FixPlanDraftItemDto(
    Guid Id,
    Guid FindingId,
    string RuleCode,
    string ValidationKey,
    FixMode FixMode,
    decimal? Confidence,
    FixEligibilityStatus Eligibility,
    FixEligibilityReasonCode EligibilityReason,
    bool RequiresExplicitApproval,
    DateTimeOffset CreatedAt);

public sealed record FixPlanDraftDto(
    Guid Id,
    FixPlanLifecycleState State,
    Guid AuditId,
    Guid SourceDocumentVersionId,
    Guid OwnerUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsStale,
    string? StaleReasonCode,
    bool Replayed,
    IReadOnlyList<FixPlanDraftItemDto> Items);

public sealed class FixPlanDraftException(
    string diagnosticCode,
    FixEligibilityReasonCode? eligibilityReason = null) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
    public FixEligibilityReasonCode? EligibilityReason { get; } = eligibilityReason;
}

public sealed record FixPlanDraftFindingSource(
    AuditFinding Finding,
    FixPlanFindingSnapshot Snapshot,
    FindingResolutionState ResolutionState,
    FindingReviewState ReviewState);

public sealed record FixPlanDraftSource(
    AuditJob Audit,
    Guid SourceDocumentVersionId,
    string? StaleReasonCode,
    IReadOnlyList<FixPlanDraftFindingSource> Findings);

public sealed record FixPlanDraftAggregate(FixPlanRecord Plan, FixPlanDraftSource Source);

public sealed record FixPlanDraftWriteResult(
    FixPlanRecord? Plan,
    bool Replayed,
    string? ConflictCode = null);

public interface IFixPlanDraftRepository
{
    Task<FixPlanDraftSource?> LoadSourceAsync(
        Guid auditId,
        FixPlanSelection selection,
        CancellationToken cancellationToken);

    Task<FixPlanDraftAggregate?> LoadOwnedAsync(
        Guid auditId,
        Guid planId,
        Guid ownerUserId,
        CancellationToken cancellationToken);

    Task<FixPlanDraftWriteResult> CreateAsync(
        FixPlanDraftSource source,
        Guid ownerUserId,
        Guid idempotencyKey,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<FixPlanDraftWriteResult> ReplaceAsync(
        Guid auditId,
        Guid planId,
        Guid ownerUserId,
        FixPlanDraftSource source,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<string?> DeleteAsync(
        Guid auditId,
        Guid planId,
        Guid ownerUserId,
        CancellationToken cancellationToken);
}

public interface IFixPlanDraftService
{
    Task<FixPlanDraftDto?> CreateAsync(Guid auditId, Guid ownerUserId, Guid idempotencyKey,
        FixPlanSelection selection, CancellationToken cancellationToken);
    Task<FixPlanDraftDto?> GetAsync(Guid auditId, Guid planId, Guid ownerUserId,
        CancellationToken cancellationToken);
    Task<FixPlanDraftDto?> UpdateAsync(Guid auditId, Guid planId, Guid ownerUserId,
        FixPlanSelection selection, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid auditId, Guid planId, Guid ownerUserId,
        CancellationToken cancellationToken);
}

public sealed class FixPlanDraftService(
    IFixPlanDraftRepository repository,
    IFixEligibilityService eligibility,
    TimeProvider timeProvider) : IFixPlanDraftService
{
    public async Task<FixPlanDraftDto?> CreateAsync(Guid auditId, Guid ownerUserId, Guid idempotencyKey,
        FixPlanSelection selection, CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty)
            throw new FixPlanDraftException("fix-plan-idempotency-key-invalid");
        var source = await repository.LoadSourceAsync(auditId, selection, cancellationToken);
        if (source is null) return null;
        var evaluations = Evaluate(source);
        EnsureWritable(source, evaluations);
        var result = await repository.CreateAsync(source, ownerUserId, idempotencyKey,
            RequestHash(source, selection), timeProvider.GetUtcNow(), cancellationToken);
        if (result.ConflictCode is not null) throw new FixPlanDraftException(result.ConflictCode);
        return Map(result.Plan!, source, evaluations, result.Replayed);
    }

    public async Task<FixPlanDraftDto?> GetAsync(Guid auditId, Guid planId, Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        var aggregate = await repository.LoadOwnedAsync(auditId, planId, ownerUserId, cancellationToken);
        if (aggregate is null) return null;
        var evaluations = Evaluate(aggregate.Source);
        return Map(aggregate.Plan, aggregate.Source, evaluations, false);
    }

    public async Task<FixPlanDraftDto?> UpdateAsync(Guid auditId, Guid planId, Guid ownerUserId,
        FixPlanSelection selection, CancellationToken cancellationToken)
    {
        var owned = await repository.LoadOwnedAsync(auditId, planId, ownerUserId, cancellationToken);
        if (owned is null) return null;
        if (owned.Plan.State != FixPlanLifecycleState.Draft)
            throw new FixPlanDraftException("fix-plan-not-draft");
        var source = await repository.LoadSourceAsync(auditId, selection, cancellationToken);
        if (source is null) throw new FixPlanDraftException("fix-plan-selection-not-found");
        var evaluations = Evaluate(source);
        EnsureWritable(source, evaluations);
        var result = await repository.ReplaceAsync(auditId, planId, ownerUserId, source,
            timeProvider.GetUtcNow(), cancellationToken);
        if (result.ConflictCode is not null) throw new FixPlanDraftException(result.ConflictCode);
        if (result.Plan is null) return null;
        return Map(result.Plan, source, evaluations, result.Replayed);
    }

    public async Task<bool> DeleteAsync(Guid auditId, Guid planId, Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        var conflict = await repository.DeleteAsync(auditId, planId, ownerUserId, cancellationToken);
        if (conflict == "fix-plan-not-found") return false;
        if (conflict is not null) throw new FixPlanDraftException(conflict);
        return true;
    }

    private IReadOnlyList<FixEligibilityResult> Evaluate(FixPlanDraftSource source) => source.Findings
        .Select(value => eligibility.Evaluate(new(source.Audit.Id, source.Audit.Status,
            source.SourceDocumentVersionId, value.Snapshot, value.Finding.Confidence,
            value.ResolutionState, value.ReviewState)))
        .ToArray();

    private static void EnsureWritable(FixPlanDraftSource source, IReadOnlyList<FixEligibilityResult> evaluations)
    {
        if (source.StaleReasonCode is not null) throw new FixPlanDraftException(source.StaleReasonCode);
        var ineligible = evaluations.FirstOrDefault(value => !value.IsEligible);
        if (ineligible is not null)
            throw new FixPlanDraftException("fix-plan-item-ineligible", ineligible.ReasonCode);
    }

    private static FixPlanDraftDto Map(FixPlanRecord plan, FixPlanDraftSource source,
        IReadOnlyList<FixEligibilityResult> evaluations, bool replayed)
    {
        var byFinding = source.Findings.ToDictionary(value => value.Finding.Id);
        var byEligibility = evaluations.ToDictionary(value => value.FindingId);
        var items = plan.Items.OrderBy(value => byFinding[value.FindingId].Snapshot.RuleOrdinal)
            .ThenBy(value => value.FindingId)
            .Select(value =>
            {
                var finding = byFinding[value.FindingId];
                var result = byEligibility[value.FindingId];
                return new FixPlanDraftItemDto(value.Id, value.FindingId, finding.Snapshot.RuleCode,
                    finding.Snapshot.ValidationKey, finding.Snapshot.FixMode, finding.Finding.Confidence,
                    result.Status, result.ReasonCode, result.RequiresExplicitApproval, value.CreatedAt);
            }).ToArray();
        var eligibilityStale = evaluations.Any(value => !value.IsEligible);
        var staleCode = source.StaleReasonCode ?? (eligibilityStale ? "fix-plan-eligibility-changed" : null);
        return new(plan.Id, plan.State, plan.SourceAuditJobId, plan.SourceDocumentVersionId,
            plan.OwnerUserId, plan.CreatedAt, plan.UpdatedAt, staleCode is not null, staleCode, replayed, items);
    }

    private static string RequestHash(FixPlanDraftSource source, FixPlanSelection selection)
    {
        var canonical = string.Join('\n', source.Audit.Id.ToString("D"),
            source.SourceDocumentVersionId.ToString("D"),
            string.Join('\n', selection.FindingIds.Order().Select(value => value.ToString("D"))));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
