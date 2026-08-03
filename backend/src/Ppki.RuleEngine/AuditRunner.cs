using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.Infrastructure;

namespace Ppki.RuleEngine;

public sealed class AuditRunner(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IFileStorage fileStorage,
    IDocxParser docxParser,
    DocumentLayoutValidationEngine validationEngine,
    IResolvedRuleSetSnapshotBuilder snapshotBuilder,
    IResolvedRuleSetHasher snapshotHasher,
    IAuditTrailWriter auditTrail)
{
    public async Task RunAsync(Guid auditJobId, CancellationToken cancellationToken)
    {
        try
        {
            AuditJob audit;
            IReadOnlyList<RuleDefinition> resolvedRules;
            string resolutionLayer;

            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                audit = await db.AuditJobs
                    .AsNoTracking()
                    .Include(x => x.DocumentVersion)
                    .ThenInclude(x => x!.Document)
                    .SingleAsync(x => x.Id == auditJobId && x.Status == AuditJobStatus.Processing, cancellationToken);

                var assignedRules = await db.ProfileRules
                    .AsNoTracking()
                    .Where(assignment => assignment.ProfileVersionId == audit.ProfileVersionId && assignment.Rule!.IsImplemented)
                    .Select(assignment => assignment.Rule!)
                    .ToListAsync(cancellationToken);

                if (assignedRules.Count > 0)
                {
                    resolvedRules = assignedRules;
                    resolutionLayer = "profile";
                }
                else
                {
                    resolvedRules = await db.Rules
                        .AsNoTracking()
                        .Where(rule => rule.IsImplemented)
                        .ToListAsync(cancellationToken);
                    resolutionLayer = "catalog-default";
                }
            }

            var proposedSnapshots = snapshotBuilder.Build(auditJobId, resolvedRules, resolutionLayer, precedence: 0);
            var ownerUserId = audit.DocumentVersion!.Document!.OwnerUserId;
            var snapshots = await EnsureRuleSnapshotsAsync(auditJobId, ownerUserId, proposedSnapshots, cancellationToken);

            var filePath = await fileStorage.MaterializeToTempFileAsync(
                audit.DocumentVersion!.StorageBucket,
                audit.DocumentVersion.StorageKey,
                cancellationToken);
            ParsedDocument parsed;
            try
            {
                try
                {
                    parsed = await docxParser.ParseAsync(filePath, cancellationToken);
                }
                catch (DocxParserException)
                {
                    throw new InvalidOperationException("Document parsing failed.");
                }
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }

            DocumentKind? documentKind = audit.DocumentKindSnapshot;
            var validation = validationEngine.Validate(parsed, snapshots, documentKind, cancellationToken);
            if (validation.Outcomes.Any(value => value.Result.Applicability is
                    ValidationApplicability.Unsupported or ValidationApplicability.InvalidRuleConfiguration))
            {
                throw new InvalidOperationException("Resolved rule validation is unsupported or invalid.");
            }

            var pending = AuditFindingMapper.Map(audit.Id, validation);

            await CompleteAuditAsync(auditJobId, pending, cancellationToken);
        }
        catch
        {
            await FailAuditIfProcessingAsync(auditJobId);
            throw;
        }
    }

    private async Task<IReadOnlyList<AuditRuleSnapshot>> EnsureRuleSnapshotsAsync(
        Guid auditJobId,
        Guid ownerUserId,
        IReadOnlyList<AuditRuleSnapshot> proposedSnapshots,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var eventContext = AuditEventContext.Service("worker", auditJobId);
        await auditTrail.SetTransactionContextAsync(db, eventContext, cancellationToken);

        var audit = await db.AuditJobs
            .FromSqlInterpolated($"select * from public.audit_jobs where id = {auditJobId} for update")
            .SingleAsync(cancellationToken);
        if (audit.Status != AuditJobStatus.Processing)
        {
            throw new InvalidOperationException("Audit job is not processing.");
        }

        var snapshots = await db.AuditRuleSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.AuditJobId == auditJobId)
            .OrderBy(snapshot => snapshot.Ordinal)
            .ToListAsync(cancellationToken);

        if (snapshots.Count == 0 && audit.ResolvedRuleSetHash is null)
        {
            db.AuditRuleSnapshots.AddRange(proposedSnapshots);
            await db.SaveChangesAsync(cancellationToken);
            snapshots = proposedSnapshots.ToList();
        }

        var hash = snapshotHasher.Hash(snapshots);
        if (audit.ResolvedRuleSetHash is null)
        {
            audit.ResolvedRuleSetHash = hash;
            audit.ApplicableRuleCount = snapshots.Count;
            auditTrail.Add(db, eventContext, new AuditEventData(
                AuditActions.AuditRuleSnapshotCreated,
                AuditResourceTypes.AuditJob,
                auditJobId,
                ownerUserId,
                AuditEventMetadata.Create(("applicable_rule_count", snapshots.Count))));
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(audit.ResolvedRuleSetHash, hash, StringComparison.Ordinal)
            || audit.ApplicableRuleCount != snapshots.Count)
        {
            throw new InvalidOperationException("Persisted audit rule snapshot is inconsistent.");
        }

        await transaction.CommitAsync(cancellationToken);
        return snapshots;
    }

    private async Task CompleteAuditAsync(
        Guid auditJobId,
        IReadOnlyList<AuditFinding> findings,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await auditTrail.SetTransactionContextAsync(
            db,
            AuditEventContext.Service("worker", auditJobId),
            cancellationToken);
        var audit = await db.AuditJobs.SingleAsync(
            item => item.Id == auditJobId && item.Status == AuditJobStatus.Processing,
            cancellationToken);

        db.AuditFindings.AddRange(findings);
        await db.SaveChangesAsync(cancellationToken);

        audit.TotalRules = audit.ApplicableRuleCount;
        audit.ErrorCount = findings.Count(item => item.Severity == RuleSeverity.Error);
        audit.WarningCount = findings.Count(item => item.Severity == RuleSeverity.Warning);
        audit.InfoCount = findings.Count(item => item.Severity == RuleSeverity.Info);
        audit.Score = CalculateScore(findings);
        audit.Status = AuditJobStatus.Completed;
        audit.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FailAuditIfProcessingAsync(Guid auditJobId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            await auditTrail.SetTransactionContextAsync(
                db,
                AuditEventContext.Service("worker", auditJobId),
                CancellationToken.None);
            await db.AuditJobs
                .Where(item => item.Id == auditJobId && item.Status == AuditJobStatus.Processing)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, AuditJobStatus.Failed)
                    .SetProperty(item => item.ErrorMessage, "Audit processing failed.")
                    .SetProperty(item => item.CompletedAt, DateTimeOffset.UtcNow), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the original worker exception. Recovery may safely retry
            // only a still-processing job and cannot duplicate snapshots.
        }
    }

    private static decimal CalculateScore(IEnumerable<AuditFinding> findings)
    {
        var violatedRules = findings
            .GroupBy(item => item.RuleId)
            .Select(group => group.Max(item => item.Severity switch
            {
                RuleSeverity.Error => 8,
                RuleSeverity.Warning => 3,
                _ => 0
            }))
            .Sum();

        return Math.Max(0, 100 - violatedRules);
    }

}
