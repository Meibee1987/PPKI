using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed record FindingResolutionSourceContext(
    Guid ExecutionId, FixExecutionState ExecutionState, Guid SourceAuditId,
    AuditJobStatus SourceAuditStatus, Guid SourceAuditDocumentVersionId,
    Guid SourceDocumentVersionId, Guid SourceDocumentId, Guid? ResultDocumentVersionId,
    Guid? ResultDocumentId, Guid SourceProfileVersionId, DocumentKind? SourceDocumentKind,
    string? SourceRuleSetHash, int SourceApplicableRuleCount, string SelectedFindingIdsJson,
    string ApprovedPlanSnapshotJson, string PlanHash, string PlannerVersion,
    string SourceVersionSha256, string? ResultVersionSha256, string? ExecutionResultSha256,
    Guid? ResultParentVersionId, Guid RequestedByUserId, Guid OwnerUserId,
    DateTimeOffset? ExecutionCompletedAt);

public sealed class FindingResolutionService(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IResolvedRuleSetHasher ruleSetHasher,
    TimeProvider timeProvider) : IFindingResolutionService
{
    private const int MaximumEvents = 100;

    public async Task<FindingResolutionDto?> GetAsync(Guid auditId, Guid findingId,
        Guid ownerUserId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var finding = await AuditReadQueries.OwnedFindings(db, auditId, ownerUserId, findingId: findingId)
            .Select(value => new { value.Id, value.AuditId, value.DocumentVersionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (finding is null) return null;

        var resolutionCase = await db.FindingResolutionCases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.SourceAuditFindingId == findingId, cancellationToken);
        if (resolutionCase is null)
            return new(findingId, auditId, FindingResolutionState.Open, null,
                finding.DocumentVersionId, null, null, null, null, null, 0, null, []);

        var eventCount = await db.FindingResolutionEvents.AsNoTracking()
            .CountAsync(value => value.ResolutionCaseId == resolutionCase.Id, cancellationToken);
        var events = await db.FindingResolutionEvents.AsNoTracking()
            .Where(value => value.ResolutionCaseId == resolutionCase.Id)
            .OrderBy(value => value.Sequence).Take(MaximumEvents).ToListAsync(cancellationToken);
        var latest = events.LastOrDefault();
        return new(findingId, auditId, FindingResolutionProjection.State(latest?.EventType), resolutionCase.Id,
            finding.DocumentVersionId, events.LastOrDefault(value => value.ResultDocumentVersionId != null)?.ResultDocumentVersionId,
            events.LastOrDefault(value => value.SourceFixExecutionId != null)?.SourceFixExecutionId,
            events.LastOrDefault(value => value.SourceReauditJobId != null)?.SourceReauditJobId,
            latest?.ResultAuditFindingId, ParseStatus(latest?.ComparisonStatus), eventCount,
            latest?.CreatedAt, events.Select(ToDto).ToArray());
    }

    public async Task<FindingResolutionReconciliationResult?> ReconcileAsync(Guid fixExecutionId,
        Guid ownerUserId, CancellationToken cancellationToken)
    {
        const int maximumAttempts = 5;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try { return await ReconcileAttemptAsync(fixExecutionId, ownerUserId, cancellationToken); }
            catch (Exception exception) when (ConcurrencyConflict(exception))
            {
                if (attempt == maximumAttempts - 1)
                    throw new FindingResolutionException("resolution-conflict");
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
        }
        throw new FindingResolutionException("resolution-conflict");
    }

    private async Task<FindingResolutionReconciliationResult?> ReconcileAttemptAsync(Guid executionId,
        Guid ownerUserId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await OwnedExecution(db, executionId, ownerUserId).SingleOrDefaultAsync(cancellationToken);
        if (source is null) { await transaction.CommitAsync(cancellationToken); return null; }
        ValidateSource(source);

        var reAudit = await AuditComparisonService.OwnedResultAudit(db, executionId, ownerUserId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new FindingResolutionException("resolution-reaudit-missing");
        ValidateLineage(source, reAudit);

        var sourceSnapshots = await db.AuditRuleSnapshots.AsNoTracking()
            .Where(value => value.AuditJobId == source.SourceAuditId)
            .OrderBy(value => value.Ordinal).ThenBy(value => value.RuleCode).ToListAsync(cancellationToken);
        var resultSnapshots = await db.AuditRuleSnapshots.AsNoTracking()
            .Where(value => value.AuditJobId == reAudit.ResultAuditId)
            .OrderBy(value => value.Ordinal).ThenBy(value => value.RuleCode).ToListAsync(cancellationToken);
        ValidateSnapshots(source, reAudit, sourceSnapshots, resultSnapshots);

        var selectedIds = SelectedFindingIds(source);
        var sourceQuery = AuditReadQueries.OwnedFindings(db, source.SourceAuditId, ownerUserId);
        if (await sourceQuery.CountAsync(cancellationToken) > AuditFindingQuery.MaximumFindingCount)
            throw new FindingResolutionException("resolution-selection-invalid");
        var sourceRows = await sourceQuery.Take(AuditFindingQuery.MaximumFindingCount).ToListAsync(cancellationToken);
        if (selectedIds.Any(id => sourceRows.All(value => value.Id != id)))
            throw new FindingResolutionException("resolution-selection-invalid");

        IReadOnlyDictionary<Guid, AuditComparisonItemDto>? verified = null;
        var pending = reAudit.ResultAuditStatus is AuditJobStatus.Queued or AuditJobStatus.Processing;
        if (!pending)
        {
            if (reAudit.ResultAuditStatus != AuditJobStatus.Completed)
                throw new FindingResolutionException("resolution-comparison-invalid");
            var resultRows = await AuditReadQueries.OwnedFindings(db, reAudit.ResultAuditId, ownerUserId)
                .Take(AuditFindingQuery.MaximumFindingCount).ToListAsync(cancellationToken);
            try
            {
                verified = AuditComparisonEngine.Compare(sourceRows.Select(AuditComparisonService.ToSnapshot),
                        resultRows.Select(AuditComparisonService.ToSnapshot))
                    .Where(value => value.Before is not null && selectedIds.Contains(value.Before.Id))
                    .ToDictionary(value => value.Before!.Id);
            }
            catch (Exception exception) when (exception is AuditComparisonException or ArgumentException)
            { throw new FindingResolutionException("resolution-comparison-invalid"); }
            if (verified.Count != selectedIds.Count)
                throw new FindingResolutionException("resolution-comparison-invalid");
        }

        var cases = await LoadCasesAsync(db, selectedIds, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var created = 0;
        foreach (var findingId in selectedIds.Order())
        {
            var resolutionCase = cases.SingleOrDefault(value => value.SourceAuditFindingId == findingId);
            if (resolutionCase is null)
            {
                resolutionCase = new FindingResolutionCase
                {
                    SourceAuditFindingId = findingId, SourceAuditJobId = source.SourceAuditId,
                    SourceDocumentVersionId = source.SourceDocumentVersionId, CreatedAt = now
                };
                db.FindingResolutionCases.Add(resolutionCase);
                cases.Add(resolutionCase);
            }
        }
        // Persist phases inside the same transaction. PostgreSQL validates the
        // per-case sequence on each row, while EF does not promise scalar-based
        // ordering for independent inserts in one batch.
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        cases = await LoadCasesAsync(db, selectedIds, cancellationToken);
        var existingEvents = await LoadEventsAsync(db, cases, cancellationToken);

        foreach (var findingId in selectedIds.Order())
        {
            var resolutionCase = cases.Single(value => value.SourceAuditFindingId == findingId);
            created += AddEvent(db, resolutionCase, existingEvents,
                FindingResolutionEventType.FixAppliedObserved,
                $"fix-applied:{executionId:D}:{findingId:D}", executionId, null,
                source.ResultDocumentVersionId, null, null, source.ExecutionCompletedAt!.Value, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        cases = await LoadCasesAsync(db, selectedIds, cancellationToken);
        existingEvents = await LoadEventsAsync(db, cases, cancellationToken);

        var verificationOccurredAt = pending
            ? await ReauditOccurredAt(db, reAudit.ResultAuditId, cancellationToken)
            : (await ReauditCompletedAt(db, reAudit.ResultAuditId, cancellationToken))!.Value;
        foreach (var findingId in selectedIds.Order())
        {
            var resolutionCase = cases.Single(value => value.SourceAuditFindingId == findingId);
            if (pending)
            {
                created += AddEvent(db, resolutionCase, existingEvents,
                    FindingResolutionEventType.ReauditPendingObserved,
                    $"reaudit-pending:{reAudit.ResultAuditId:D}:{findingId:D}", executionId,
                    reAudit.ResultAuditId, source.ResultDocumentVersionId, null, null,
                    verificationOccurredAt, now);
            }
            else
            {
                var item = verified![findingId];
                var resolved = item.Status == AuditComparisonStatus.NoLongerDetected;
                if (!resolved && item.Status is not (AuditComparisonStatus.StillDetected or AuditComparisonStatus.Changed))
                    throw new FindingResolutionException("resolution-comparison-invalid");
                created += AddEvent(db, resolutionCase, existingEvents,
                    FindingResolutionProjection.VerificationEvent(item.Status),
                    $"verification:{reAudit.ResultAuditId:D}:{findingId:D}", executionId,
                    reAudit.ResultAuditId, source.ResultDocumentVersionId,
                    resolved ? null : item.After?.Id, item.Status.ToString(),
                    verificationOccurredAt, now);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var caseIds = cases.Select(value => value.Id).ToArray();
        var eventCount = await db.FindingResolutionEvents.AsNoTracking()
            .CountAsync(value => caseIds.Contains(value.ResolutionCaseId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(executionId, reAudit.ResultAuditId,
            pending ? FindingResolutionReconciliationState.Pending : FindingResolutionReconciliationState.Completed,
            selectedIds.Count, cases.Count, eventCount, created, created == 0);
    }

    public static IQueryable<FindingResolutionSourceContext> OwnedExecution(PpkiDbContext db,
        Guid executionId, Guid ownerUserId) => db.FixExecutionJobs.AsNoTracking()
        .Where(value => value.Id == executionId)
        .Select(value => new FindingResolutionSourceContext(value.Id, value.State, value.AuditJobId,
            value.AuditJob!.Status, value.AuditJob.DocumentVersionId, value.SourceDocumentVersionId,
            value.SourceDocumentVersion!.DocumentId, value.ResultDocumentVersionId,
            value.ResultDocumentVersion == null ? null : value.ResultDocumentVersion.DocumentId,
            value.AuditJob.ProfileVersionId, value.AuditJob.DocumentKindSnapshot,
            value.AuditJob.ResolvedRuleSetHash, value.AuditJob.ApplicableRuleCount,
            value.SelectedFindingIdsJson, value.ApprovedPlanSnapshotJson, value.PlanHash, value.PlannerVersion,
            value.SourceDocumentVersion!.Sha256,
            value.ResultDocumentVersion == null ? null : value.ResultDocumentVersion.Sha256,
            value.ResultSha256,
            value.ResultDocumentVersion == null ? null : value.ResultDocumentVersion.ParentVersionId,
            value.RequestedByUserId, value.AuditJob!.DocumentVersion!.Document!.OwnerUserId, value.CompletedAt));

    public static void ValidateSource(FindingResolutionSourceContext source)
    {
        if (source.ExecutionState != FixExecutionState.Completed) throw new FindingResolutionException("resolution-execution-not-completed");
        if (source.ResultDocumentVersionId is null || source.ResultDocumentId is null || source.ExecutionCompletedAt is null)
            throw new FindingResolutionException("resolution-result-version-missing");
        if (source.SourceAuditStatus != AuditJobStatus.Completed) throw new FindingResolutionException("resolution-source-audit-not-completed");
        if (source.SourceAuditDocumentVersionId != source.SourceDocumentVersionId || source.SourceDocumentId != source.ResultDocumentId)
            throw new FindingResolutionException("resolution-lineage-mismatch");
        if (source.ResultParentVersionId != source.SourceDocumentVersionId
            || source.ResultVersionSha256 != source.ExecutionResultSha256)
            throw new FindingResolutionException("resolution-lineage-mismatch");
    }

    private static void ValidateLineage(FindingResolutionSourceContext source, AuditComparisonResultContext result)
    {
        if (result.SourceFixExecutionId != source.ExecutionId || result.SourceAuditId != source.SourceAuditId
            || result.ResultDocumentVersionId != source.ResultDocumentVersionId)
            throw new FindingResolutionException("resolution-lineage-mismatch");
        if (result.ResultProfileVersionId != source.SourceProfileVersionId || result.ResultDocumentKind != source.SourceDocumentKind
            || result.ResultRuleSetHash != source.SourceRuleSetHash || result.ResultApplicableRuleCount != source.SourceApplicableRuleCount)
            throw new FindingResolutionException("resolution-historical-context-mismatch");
    }

    private void ValidateSnapshots(FindingResolutionSourceContext source, AuditComparisonResultContext result,
        IReadOnlyList<AuditRuleSnapshot> before, IReadOnlyList<AuditRuleSnapshot> after)
    {
        try
        {
            if (before.Count == 0 || before.Count != source.SourceApplicableRuleCount
                || after.Count != result.ResultApplicableRuleCount
                || ruleSetHasher.Hash(before) != source.SourceRuleSetHash
                || ruleSetHasher.Hash(after) != result.ResultRuleSetHash
                || !AuditComparisonService.HistoricalSnapshotsEqual(before, after))
                throw new FindingResolutionException("resolution-historical-context-mismatch");
        }
        catch (FindingResolutionException) { throw; }
        catch { throw new FindingResolutionException("resolution-historical-context-mismatch"); }
    }

    private static HashSet<Guid> SelectedFindingIds(FindingResolutionSourceContext source)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(source.SelectedFindingIdsJson) ?? [];
            var selected = ids.Select(Guid.Parse).ToHashSet();
            var approved = ApprovedFixExecutionPlanSerializer.Deserialize(source.ApprovedPlanSnapshotJson);
            var approvedIds = approved.Source.Findings.Select(value => value.FindingId).ToHashSet();
            if (selected.Count is < 1 or > FixPlanSelection.MaximumFindingCount || selected.Contains(Guid.Empty)
                || !selected.SetEquals(approvedIds) || approved.Source.AuditId != source.SourceAuditId
                || approved.Source.DocumentVersionId != source.SourceDocumentVersionId
                || approved.Source.AuditStatus != AuditJobStatus.Completed
                || approved.Source.SourceVersionSha256 != source.SourceVersionSha256
                || approved.Source.ResolvedRuleSetHash != source.SourceRuleSetHash
                || approved.Source.DocumentKindSnapshot != source.SourceDocumentKind
                || approved.Preview.State != FixPlanState.Ready
                || approved.Preview.SourceDocumentVersionId != source.SourceDocumentVersionId
                || approved.Preview.SourceDocumentVersionSha256 != source.SourceVersionSha256
                || approved.Preview.PlanHash != source.PlanHash
                || approved.Preview.PlannerVersion != source.PlannerVersion
                || approved.Preview.SelectedFindingCount != selected.Count
                || !approved.Preview.Items.Select(value => value.FindingId).ToHashSet().SetEquals(selected)
                || !approved.Preview.Operations.SelectMany(value => value.SourceFindingIds).ToHashSet().SetEquals(selected))
                throw new FindingResolutionException("resolution-approved-plan-invalid");
            return selected;
        }
        catch (FindingResolutionException) { throw; }
        catch { throw new FindingResolutionException("resolution-approved-plan-invalid"); }
    }

    private static int AddEvent(PpkiDbContext db, FindingResolutionCase resolutionCase,
        IReadOnlyCollection<FindingResolutionEvent> existingEvents, FindingResolutionEventType type,
        string key, Guid executionId, Guid? reAuditId, Guid? resultVersionId, Guid? resultFindingId,
        string? comparisonStatus, DateTimeOffset occurredAt, DateTimeOffset createdAt)
    {
        if (existingEvents.Any(value => value.SourceEventKey == key)) return 0;
        db.FindingResolutionEvents.Add(new FindingResolutionEvent
        {
            ResolutionCaseId = resolutionCase.Id,
            Sequence = existingEvents.Where(value => value.ResolutionCaseId == resolutionCase.Id)
                .Select(value => value.Sequence).DefaultIfEmpty().Max() + 1,
            EventType = type, SourceFixExecutionId = executionId, SourceReauditJobId = reAuditId,
            ResultDocumentVersionId = resultVersionId, ResultAuditFindingId = resultFindingId,
            ComparisonStatus = comparisonStatus, SourceOccurredAt = occurredAt,
            SourceEventKey = key, CreatedAt = createdAt
        });
        return 1;
    }

    private static Task<List<FindingResolutionCase>> LoadCasesAsync(PpkiDbContext db,
        IReadOnlySet<Guid> findingIds, CancellationToken cancellationToken) =>
        db.FindingResolutionCases.AsNoTracking()
            .Where(value => findingIds.Contains(value.SourceAuditFindingId))
            .ToListAsync(cancellationToken);

    private static Task<List<FindingResolutionEvent>> LoadEventsAsync(PpkiDbContext db,
        IReadOnlyCollection<FindingResolutionCase> cases, CancellationToken cancellationToken)
    {
        var caseIds = cases.Select(value => value.Id).ToArray();
        return db.FindingResolutionEvents.AsNoTracking()
            .Where(value => caseIds.Contains(value.ResolutionCaseId))
            .ToListAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset> ReauditOccurredAt(PpkiDbContext db, Guid id, CancellationToken ct) =>
        await db.AuditJobs.AsNoTracking().Where(value => value.Id == id).Select(value => value.CreatedAt).SingleAsync(ct);
    private static async Task<DateTimeOffset?> ReauditCompletedAt(PpkiDbContext db, Guid id, CancellationToken ct) =>
        await db.AuditJobs.AsNoTracking().Where(value => value.Id == id).Select(value => value.CompletedAt).SingleAsync(ct);

    private static FindingResolutionEventDto ToDto(FindingResolutionEvent value) => new(value.Sequence,
        value.EventType, value.SourceFixExecutionId, value.SourceReauditJobId, value.ResultDocumentVersionId,
        value.ResultAuditFindingId, ParseStatus(value.ComparisonStatus), value.SourceOccurredAt, value.CreatedAt);
    private static AuditComparisonStatus? ParseStatus(string? value) =>
        Enum.TryParse<AuditComparisonStatus>(value, out var parsed) ? parsed : null;
    private static bool ConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure }) return true;
            else if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected }) return true;
        return false;
    }
}
