using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class FixExecutionRepository(IDbContextFactory<PpkiDbContext> dbFactory) : IFixExecutionRepository
{
    public async Task<FixExecutionEnqueueResult> EnqueueAsync(FixExecutionCandidate candidate, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingAsync(db, candidate, cancellationToken);
        if (existing is not null) return Compare(existing, candidate);
        var sourceIsCurrent = await db.DocumentVersions.AsNoTracking()
            .Where(value => value.Id == candidate.SourceDocumentVersionId)
            .AnyAsync(value => value.Document!.CurrentVersionNo == value.VersionNo, cancellationToken);
        if (!sourceIsCurrent) return new(null, false, "fix-source-version-superseded");

        var job = new FixExecutionJob
        {
            Id = candidate.ExecutionId,
            AuditJobId = candidate.AuditJobId,
            SourceDocumentVersionId = candidate.SourceDocumentVersionId,
            RequestedByUserId = candidate.RequestedByUserId,
            IdempotencyKey = candidate.IdempotencyKey,
            PlanHash = candidate.PlanHash,
            PlannerVersion = candidate.PlannerVersion,
            SelectedFindingIdsJson = candidate.SelectedFindingIdsJson,
            ApprovedPlanSnapshotJson = candidate.ApprovedPlanSnapshotJson,
            PlannedOperationCount = candidate.PlannedOperationCount,
            CreatedAt = candidate.CreatedAt,
            State = FixExecutionState.Queued,
            MaxAttempts = FixRetryPolicy.MaximumAttempts
        };
        db.FixExecutionJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(job, false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await using var retryDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            existing = await ExistingAsync(retryDb, candidate, cancellationToken);
            return existing is null
                ? new(null, false, "fix-execution-idempotency-conflict")
                : Compare(existing, candidate);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            return new(null, false, "fix-source-version-superseded");
        }
    }

    public Task<FixExecutionJob?> GetOwnedAsync(Guid executionId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        return Owned(dbFactory, executionId, ownerUserId, cancellationToken);
    }

    private static async Task<FixExecutionJob?> Owned(IDbContextFactory<PpkiDbContext> factory,
        Guid executionId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.FixExecutionJobs.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == executionId, cancellationToken);
    }

    private static Task<FixExecutionJob?> ExistingAsync(PpkiDbContext db, FixExecutionCandidate candidate,
        CancellationToken cancellationToken) => db.FixExecutionJobs.AsNoTracking()
        .Where(value => value.AuditJobId == candidate.AuditJobId && value.IdempotencyKey == candidate.IdempotencyKey
            || value.SourceDocumentVersionId == candidate.SourceDocumentVersionId && value.PlanHash == candidate.PlanHash)
        .OrderBy(value => value.CreatedAt)
        .FirstOrDefaultAsync(cancellationToken);

    private static FixExecutionEnqueueResult Compare(FixExecutionJob existing, FixExecutionCandidate candidate)
    {
        var sameRequest = existing.AuditJobId == candidate.AuditJobId
            && existing.SourceDocumentVersionId == candidate.SourceDocumentVersionId
            && existing.PlanHash == candidate.PlanHash
            && existing.SelectedFindingIdsJson == candidate.SelectedFindingIdsJson;
        return sameRequest
            ? new(existing, true)
            : new(null, false, "fix-execution-idempotency-conflict");
    }
}
