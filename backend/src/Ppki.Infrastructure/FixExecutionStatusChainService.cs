using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class FixExecutionStatusChainService(
    IDbContextFactory<PpkiDbContext> dbFactory) : IFixExecutionStatusChainService
{
    public async Task<FixExecutionStatusChain?> GetAsync(Guid fixExecutionId, Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.FixExecutionJobs.AsNoTracking()
            .Where(value => value.Id == fixExecutionId
                && value.SourceDocumentVersion!.Document!.OwnerUserId == ownerUserId)
            .Select(value => new
            {
                value.Id,
                value.AuditJobId,
                value.FixPlanId,
                FixPlanState = value.FixPlan == null ? (FixPlanLifecycleState?)null : value.FixPlan.State,
                value.State,
                value.SourceDocumentVersionId,
                value.ResultDocumentVersionId,
                value.SelectedFindingIdsJson
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null) return null;

        var reAudit = await db.AuditJobs.AsNoTracking()
            .Where(value => value.SourceFixExecutionId == source.Id)
            .Select(value => new AutomaticReauditChainStatus(value.Id, value.Status,
                value.DocumentVersionId, value.ProfileVersionId, value.CreatedAt,
                value.StartedAt, value.CompletedAt))
            .SingleOrDefaultAsync(cancellationToken);

        var selected = SelectedFindingIds(source.SelectedFindingIdsJson);
        var cases = await db.FindingResolutionCases.AsNoTracking()
            .Where(value => value.SourceAuditJobId == source.AuditJobId
                && selected.Contains(value.SourceAuditFindingId))
            .ToListAsync(cancellationToken);
        var caseIds = cases.Select(value => value.Id).ToArray();
        var events = caseIds.Length == 0 ? [] : await db.FindingResolutionEvents.AsNoTracking()
            .Where(value => caseIds.Contains(value.ResolutionCaseId)
                && value.SourceFixExecutionId == source.Id)
            .OrderBy(value => value.ResolutionCaseId).ThenBy(value => value.Sequence)
            .ToListAsync(cancellationToken);

        var findings = selected.Order().Select(findingId =>
        {
            var resolutionCase = cases.SingleOrDefault(value => value.SourceAuditFindingId == findingId);
            var history = resolutionCase is null ? [] : events
                .Where(value => value.ResolutionCaseId == resolutionCase.Id)
                .OrderBy(value => value.Sequence).ToArray();
            var latest = history.LastOrDefault();
            var verification = history.LastOrDefault(value => value.EventType is
                FindingResolutionEventType.VerificationResolvedObserved
                or FindingResolutionEventType.VerificationStillDetectedObserved);
            var comparison = ParseComparison(verification?.ComparisonStatus);
            return new AutomaticFindingReconciliationStatus(findingId,
                FindingResolutionProjection.State(latest?.EventType), Outcome(comparison), comparison,
                verification?.ResultAuditFindingId);
        }).ToArray();
        var reconciliation = ReconciliationState(reAudit?.Status, findings.Select(value => value.Outcome));

        return new(source.AuditJobId, source.FixPlanId, source.FixPlanState, source.Id,
            source.State, source.SourceDocumentVersionId, source.ResultDocumentVersionId,
            reAudit, reconciliation, findings);
    }

    public static AutomaticFindingReconciliationOutcome? Outcome(AuditComparisonStatus? status) => status switch
    {
        AuditComparisonStatus.NoLongerDetected => AutomaticFindingReconciliationOutcome.Fixed,
        AuditComparisonStatus.StillDetected => AutomaticFindingReconciliationOutcome.StillFailing,
        AuditComparisonStatus.Changed => AutomaticFindingReconciliationOutcome.PartiallyFixed,
        _ => null
    };

    public static FindingResolutionReconciliationState ReconciliationState(
        AuditJobStatus? auditStatus,
        IEnumerable<AutomaticFindingReconciliationOutcome?> outcomes) =>
        auditStatus == AuditJobStatus.Completed && outcomes.Any() && outcomes.All(value => value is not null)
            ? FindingResolutionReconciliationState.Completed
            : FindingResolutionReconciliationState.Pending;

    private static HashSet<Guid> SelectedFindingIds(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json) ?? [];
            var ids = values.Select(Guid.Parse).ToHashSet();
            if (values.Length < 1 || values.Length > AuditFindingQuery.MaximumFindingCount
                || ids.Count != values.Length || ids.Contains(Guid.Empty))
                throw new ReauditException("status-chain-selection-invalid");
            return ids;
        }
        catch (ReauditException) { throw; }
        catch { throw new ReauditException("status-chain-selection-invalid"); }
    }

    private static AuditComparisonStatus? ParseComparison(string? value) =>
        Enum.TryParse<AuditComparisonStatus>(value, out var parsed) ? parsed : null;
}
