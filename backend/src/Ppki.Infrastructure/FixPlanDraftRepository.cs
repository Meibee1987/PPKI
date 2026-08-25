using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class FixPlanDraftRepository(
    IDbContextFactory<PpkiDbContext> dbFactory) : IFixPlanDraftRepository
{
    public async Task<FixPlanDraftSource?> LoadSourceAsync(
        Guid auditId,
        FixPlanSelection selection,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await LoadSourceAsync(db, auditId, selection.FindingIds, cancellationToken);
    }

    public async Task<FixPlanDraftAggregate?> LoadOwnedAsync(
        Guid auditId,
        Guid planId,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await db.FixPlans.AsNoTracking().Include(value => value.Items)
            .SingleOrDefaultAsync(value => value.Id == planId
                && value.SourceAuditJobId == auditId
                && value.OwnerUserId == ownerUserId, cancellationToken);
        if (plan is null) return null;
        var ids = plan.Items.Select(value => value.FindingId).Order().ToArray();
        var source = await LoadSourceAsync(db, auditId, ids, cancellationToken);
        return source is null ? null : new(plan, source);
    }

    public async Task<FixPlanDraftWriteResult> CreateAsync(
        FixPlanDraftSource source,
        Guid ownerUserId,
        Guid idempotencyKey,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingAsync(db, ownerUserId, idempotencyKey, cancellationToken);
        if (existing is not null) return Compare(existing, source, requestHash);
        if (!await SourceCurrentAsync(db, source.SourceDocumentVersionId, cancellationToken))
            return new(null, false, "fix-plan-source-version-superseded");

        var audit = await db.AuditJobs.Include(value => value.DocumentVersion)
            .SingleOrDefaultAsync(value => value.Id == source.Audit.Id, cancellationToken);
        if (audit is null || audit.DocumentVersionId != source.SourceDocumentVersionId)
            return new(null, false, "fix-plan-source-lineage-invalid");
        var ids = source.Findings.Select(value => value.Finding.Id).ToArray();
        var findings = await db.AuditFindings.Include(value => value.AuditJob)
            .Where(value => ids.Contains(value.Id) && value.AuditJobId == audit.Id)
            .ToListAsync(cancellationToken);
        if (findings.Count != ids.Length)
            return new(null, false, "fix-plan-selection-not-found");

        var plan = FixPlanRecord.Create(audit, ownerUserId, idempotencyKey, requestHash, now);
        plan.ReplaceItems(findings, now);
        db.FixPlans.Add(plan);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(plan, false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await using var retryDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            existing = await ExistingAsync(retryDb, ownerUserId, idempotencyKey, cancellationToken);
            return existing is null
                ? new(null, false, "fix-plan-idempotency-conflict")
                : Compare(existing, source, requestHash);
        }
        catch (DbUpdateException exception) when (IsLineageViolation(exception))
        {
            return new(null, false, "fix-plan-source-lineage-invalid");
        }
    }

    public async Task<FixPlanDraftWriteResult> ReplaceAsync(
        Guid auditId,
        Guid planId,
        Guid ownerUserId,
        FixPlanDraftSource source,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var plan = await db.FixPlans.Include(value => value.Items)
            .SingleOrDefaultAsync(value => value.Id == planId
                && value.SourceAuditJobId == auditId
                && value.OwnerUserId == ownerUserId, cancellationToken);
        if (plan is null) return new(null, false);
        if (plan.State != FixPlanLifecycleState.Draft)
            return new(null, false, "fix-plan-not-draft");
        if (plan.SourceDocumentVersionId != source.SourceDocumentVersionId
            || !await SourceCurrentAsync(db, plan.SourceDocumentVersionId, cancellationToken))
            return new(null, false, "fix-plan-source-version-superseded");

        var ids = source.Findings.Select(value => value.Finding.Id).Order().ToArray();
        if (plan.Items.Select(value => value.FindingId).Order().SequenceEqual(ids))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(plan, true);
        }
        var findings = await db.AuditFindings.Include(value => value.AuditJob)
            .Where(value => ids.Contains(value.Id) && value.AuditJobId == auditId)
            .ToListAsync(cancellationToken);
        if (findings.Count != ids.Length)
            return new(null, false, "fix-plan-selection-not-found");
        plan.ReplaceItems(findings, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(plan, false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(null, false, "fix-plan-concurrency-conflict");
        }
        catch (DbUpdateException exception) when (IsSerializationFailure(exception))
        {
            return new(null, false, "fix-plan-concurrency-conflict");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return new(null, false, "fix-plan-concurrency-conflict");
        }
    }

    public async Task<string?> DeleteAsync(
        Guid auditId,
        Guid planId,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await db.FixPlans.SingleOrDefaultAsync(value => value.Id == planId
            && value.SourceAuditJobId == auditId
            && value.OwnerUserId == ownerUserId, cancellationToken);
        if (plan is null) return "fix-plan-not-found";
        if (plan.State != FixPlanLifecycleState.Draft) return "fix-plan-not-draft";
        db.FixPlans.Remove(plan);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return "fix-plan-concurrency-conflict";
        }
    }

    private static async Task<FixPlanDraftSource?> LoadSourceAsync(
        PpkiDbContext db,
        Guid auditId,
        IReadOnlyList<Guid> findingIds,
        CancellationToken cancellationToken)
    {
        var audit = await db.AuditJobs.AsNoTracking()
            .Include(value => value.DocumentVersion).ThenInclude(value => value!.Document)
            .SingleOrDefaultAsync(value => value.Id == auditId, cancellationToken);
        if (audit is null) return null;
        var findings = await db.AuditFindings.AsNoTracking()
            .Where(value => value.AuditJobId == auditId && findingIds.Contains(value.Id))
            .ToListAsync(cancellationToken);
        if (findings.Count != findingIds.Count) return null;
        foreach (var finding in findings) finding.AuditJob = audit;

        var ruleCodes = findings.Select(value => value.RuleCodeSnapshot).Distinct().ToArray();
        var snapshots = await db.AuditRuleSnapshots.AsNoTracking()
            .Where(value => value.AuditJobId == auditId && ruleCodes.Contains(value.RuleCode))
            .ToListAsync(cancellationToken);
        if (snapshots.Count != ruleCodes.Length) return null;
        var byRule = snapshots.ToDictionary(value => value.RuleCode, StringComparer.Ordinal);
        var ids = findings.Select(value => value.Id).ToArray();
        var resolutionEvents = await (from resolutionCase in db.FindingResolutionCases.AsNoTracking()
            from resolutionEvent in resolutionCase.Events
            where ids.Contains(resolutionCase.SourceAuditFindingId)
            select new { resolutionCase.SourceAuditFindingId, resolutionEvent.Sequence, resolutionEvent.EventType })
            .ToListAsync(cancellationToken);
        var reviewEvents = await (from reviewCase in db.FindingReviewCases.AsNoTracking()
            from reviewEvent in reviewCase.Events
            where ids.Contains(reviewCase.AuditFindingId)
            select new { reviewCase.AuditFindingId, reviewEvent.Sequence, reviewEvent.EventType })
            .ToListAsync(cancellationToken);

        var sources = findings.Select(finding =>
        {
            var snapshot = byRule[finding.RuleCodeSnapshot];
            var resolution = resolutionEvents.Where(value => value.SourceAuditFindingId == finding.Id)
                .OrderBy(value => value.Sequence).LastOrDefault();
            var review = reviewEvents.Where(value => value.AuditFindingId == finding.Id)
                .OrderBy(value => value.Sequence).LastOrDefault();
            return new FixPlanDraftFindingSource(finding, new(
                finding.Id, snapshot.Ordinal, snapshot.RuleCode, snapshot.Domain, snapshot.Element,
                snapshot.ValidationKey, snapshot.Severity, snapshot.FixMode, finding.Status,
                finding.ActualValueJson, finding.ExpectedValueJson, finding.LocationJson,
                snapshot.SnapshotSchemaVersion),
                FindingResolutionProjection.State(resolution?.EventType),
                FindingReviewProjection.State(review?.EventType));
        }).OrderBy(value => value.Snapshot.RuleOrdinal).ThenBy(value => value.Finding.Id).ToArray();

        var version = audit.DocumentVersion;
        var staleCode = version?.Document is null || version.Document.Status != DocumentStatus.Active
            ? "fix-plan-source-version-unavailable"
            : version.VersionNo != version.Document.CurrentVersionNo
                ? "fix-plan-source-version-superseded"
                : null;
        return new(audit, audit.DocumentVersionId, staleCode, sources);
    }

    private static Task<FixPlanRecord?> ExistingAsync(PpkiDbContext db, Guid ownerUserId,
        Guid idempotencyKey, CancellationToken cancellationToken) => db.FixPlans.AsNoTracking()
        .Include(value => value.Items)
        .SingleOrDefaultAsync(value => value.OwnerUserId == ownerUserId
            && value.IdempotencyKey == idempotencyKey, cancellationToken);

    private static FixPlanDraftWriteResult Compare(FixPlanRecord existing,
        FixPlanDraftSource source, string requestHash) =>
        existing.SourceAuditJobId == source.Audit.Id
        && existing.SourceDocumentVersionId == source.SourceDocumentVersionId
        && string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? new(existing, true)
            : new(null, false, "fix-plan-idempotency-conflict");

    private static Task<bool> SourceCurrentAsync(PpkiDbContext db, Guid sourceVersionId,
        CancellationToken cancellationToken) => db.DocumentVersions.AsNoTracking()
        .AnyAsync(value => value.Id == sourceVersionId
            && value.Document!.Status == DocumentStatus.Active
            && value.VersionNo == value.Document.CurrentVersionNo, cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static bool IsLineageViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation };

    private static bool IsSerializationFailure(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure };
}
