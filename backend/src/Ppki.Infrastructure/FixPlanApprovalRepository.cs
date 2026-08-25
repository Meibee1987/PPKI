using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class FixPlanApprovalRepository(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IAuditTrailWriter auditTrail) : IFixPlanApprovalRepository
{
    public async Task<FixPlanApprovalWriteResult> ApproveAsync(Guid auditId, Guid planId,
        Guid ownerUserId, string approvalRequestHash, DateTimeOffset now,
        Func<FixPlanDraftAggregate, FixPlanApprovalPrepared> prepare,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var plan = await db.FixPlans.Include(value => value.Items)
            .SingleOrDefaultAsync(value => value.Id == planId && value.SourceAuditJobId == auditId
                && value.OwnerUserId == ownerUserId, cancellationToken);
        if (plan is null) return new(null, null, false);

        var existing = await db.FixPlanApprovalSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(value => value.FixPlanId == planId, cancellationToken);
        if (plan.State != FixPlanLifecycleState.Draft)
        {
            if (plan.State == FixPlanLifecycleState.Approved && existing is not null
                && string.Equals(existing.ApprovalRequestHash, approvalRequestHash, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return new(plan, existing, true);
            }
            return new(null, null, false, existing is null
                ? "fix-plan-approval-snapshot-missing" : "fix-plan-approval-conflict");
        }
        if (existing is not null) return new(null, null, false, "fix-plan-approval-conflict");

        var ids = plan.Items.Select(value => value.FindingId).Order().ToArray();
        var source = await FixPlanDraftRepository.LoadSourceAsync(db, auditId, ids, cancellationToken);
        if (source is null) return new(null, null, false, "fix-plan-approval-source-invalid");
        if (!await FixPlanDraftRepository.SourceCurrentAsync(db, plan.SourceDocumentVersionId, cancellationToken))
            return new(null, null, false, "fix-plan-source-version-superseded");
        var prepared = prepare(new(plan, source));
        if (!string.Equals(prepared.ApprovalRequestHash, approvalRequestHash, StringComparison.Ordinal))
            return new(null, null, false, "fix-plan-confirm-approval-invalid");

        var snapshot = FixPlanApprovalSnapshotRecord.Create(plan.Id, prepared.SchemaVersion,
            prepared.PlanHash, prepared.ApprovalRequestHash, prepared.SourceVersionSha256,
            prepared.SnapshotJson, ownerUserId, now);
        plan.Approve(ownerUserId, now);
        db.FixPlanApprovalSnapshots.Add(snapshot);
        var context = AuditEventContext.User(ownerUserId, plan.Id);
        await auditTrail.SetTransactionContextAsync(db, context, cancellationToken);
        auditTrail.Add(db, context, new(AuditActions.FixPlanApproved, AuditResourceTypes.FixPlan,
            plan.Id, ownerUserId, AuditEventMetadata.Create(("plan_hash", prepared.PlanHash),
                ("snapshot_schema_version", prepared.SchemaVersion), ("item_count", prepared.ItemCount))));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(plan, snapshot, false);
        }
        catch (Exception exception) when (IsConcurrency(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ReplayAsync(auditId, planId, ownerUserId, approvalRequestHash, cancellationToken);
        }
    }

    private async Task<FixPlanApprovalWriteResult> ReplayAsync(Guid auditId, Guid planId,
        Guid ownerUserId, string requestHash, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await db.FixPlans.AsNoTracking().Include(value => value.Items)
            .SingleOrDefaultAsync(value => value.Id == planId && value.SourceAuditJobId == auditId
                && value.OwnerUserId == ownerUserId, cancellationToken);
        if (plan is null) return new(null, null, false);
        var snapshot = await db.FixPlanApprovalSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(value => value.FixPlanId == planId, cancellationToken);
        return plan.State == FixPlanLifecycleState.Approved && snapshot is not null
            && string.Equals(snapshot.ApprovalRequestHash, requestHash, StringComparison.Ordinal)
            ? new(plan, snapshot, true)
            : new(null, null, false, "fix-plan-approval-concurrency-conflict");
    }

    private static bool IsConcurrency(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => true,
        PostgresException postgres when postgres.SqlState is PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.UniqueViolation => true,
        DbUpdateException { InnerException: PostgresException postgres }
            when postgres.SqlState is PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.UniqueViolation => true,
        _ => false
    };
}
