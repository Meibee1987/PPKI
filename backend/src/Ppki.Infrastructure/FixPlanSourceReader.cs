using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class FixPlanSourceReader(
    IDbContextFactory<PpkiDbContext> dbFactory) : IFixPlanSourceReader
{
    public async Task<FixPlanSource?> LoadAsync(
        Guid auditId,
        Guid ownerUserId,
        FixPlanSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var audit = await FixPlanSourceQueries.OwnedAudit(db, auditId, ownerUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (audit is null) return null;

        var findings = await FixPlanSourceQueries.OwnedSelectedFindings(
                db, auditId, ownerUserId, selection.FindingIds)
            .ToListAsync(cancellationToken);

        // A missing or foreign finding is deliberately indistinguishable from a
        // missing audit at this boundary and must never result in a partial plan.
        if (findings.Count != selection.FindingIds.Count) return null;

        return new(
            audit.AuditId,
            audit.Status,
            audit.DocumentVersionId,
            audit.SourceVersionSha256,
            audit.ResolvedRuleSetHash,
            audit.DocumentKindSnapshot,
            findings);
    }
}

public static class FixPlanSourceQueries
{
    public static IQueryable<FixPlanAuditSourceRow> OwnedAudit(
        PpkiDbContext db,
        Guid auditId,
        Guid ownerUserId) =>
        db.AuditJobs
            .AsNoTracking()
            .Where(value => value.Id == auditId
                && value.DocumentVersion!.Document!.OwnerUserId == ownerUserId)
            .Select(value => new FixPlanAuditSourceRow(
                value.Id,
                value.Status,
                value.DocumentVersionId,
                value.DocumentVersion!.Sha256,
                value.ResolvedRuleSetHash,
                value.DocumentKindSnapshot));

    public static IQueryable<FixPlanFindingSnapshot> OwnedSelectedFindings(
        PpkiDbContext db,
        Guid auditId,
        Guid ownerUserId,
        IReadOnlyList<Guid> findingIds) =>
        from finding in db.AuditFindings.AsNoTracking()
        join snapshot in db.AuditRuleSnapshots.AsNoTracking()
            on new { finding.AuditJobId, RuleCode = finding.RuleCodeSnapshot }
            equals new { snapshot.AuditJobId, RuleCode = snapshot.RuleCode }
        where finding.AuditJobId == auditId
            && finding.AuditJob!.DocumentVersion!.Document!.OwnerUserId == ownerUserId
            && findingIds.Contains(finding.Id)
        select new FixPlanFindingSnapshot(
            finding.Id,
            snapshot.Ordinal,
            snapshot.RuleCode,
            snapshot.Domain,
            snapshot.Element,
            snapshot.ValidationKey,
            snapshot.Severity,
            snapshot.FixMode,
            finding.Status,
            finding.ActualValueJson,
            finding.ExpectedValueJson,
            finding.LocationJson,
            snapshot.SnapshotSchemaVersion);
}

public sealed record FixPlanAuditSourceRow(
    Guid AuditId,
    AuditJobStatus Status,
    Guid DocumentVersionId,
    string SourceVersionSha256,
    string? ResolvedRuleSetHash,
    DocumentKind? DocumentKindSnapshot);
