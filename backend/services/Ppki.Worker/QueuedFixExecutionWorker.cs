using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;

namespace Ppki.Worker;

public sealed class QueuedFixExecutionWorker(
    ILogger<QueuedFixExecutionWorker> logger,
    IConfiguration configuration,
    IDbContextFactory<PpkiDbContext> dbFactory,
    FixExecutionProcessor processor,
    IRemediationFaultInjector faults) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Max(1, int.TryParse(configuration["Worker:PollSeconds"], out var value) ? value : 2);
        logger.LogInformation("PPKI fix execution worker started with {PollSeconds}s polling.", pollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            FixExecutionClaim? claim = null;
            try
            {
                claim = await ClaimAsync(stoppingToken);
                if (claim is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
                    continue;
                }
                logger.LogInformation("Processing fix execution {ExecutionId}; Attempt={AttemptNumber}.",
                    claim.Value.ExecutionId, claim.Value.AttemptNumber);
                await faults.CheckpointAsync(RemediationCheckpoint.AfterClaim, claim.Value.ExecutionId,
                    claim.Value.AttemptNumber, stoppingToken);
                await processor.ProcessAsync(claim.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (claim is not null) await RetryOrFailAsync(claim.Value,
                    new FixExecutionException(FixFailureCategory.TransientInfrastructure, "worker-interrupted"), CancellationToken.None);
                break;
            }
            catch (Exception exception)
            {
                var failure = exception as FixExecutionException
                    ?? new FixExecutionException(FixFailureCategory.TerminalInfrastructure, "database-finalization-terminal");
                logger.LogError("Fix execution iteration failed for {ExecutionId}; Category={FailureCategory}; Code={FailureCode}.",
                    claim?.ExecutionId, failure.Category, failure.DiagnosticCode);
                if (claim is not null) await RetryOrFailAsync(claim.Value, failure, CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
        }
    }

    internal async Task<FixExecutionClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await db.FixExecutionJobs
            .Where(value => value.State == FixExecutionState.Processing && value.LeaseExpiresAt < now
                && value.AttemptCount >= value.MaxAttempts)
            .ExecuteUpdateAsync(update => update
                .SetProperty(value => value.State, FixExecutionState.Failed)
                .SetProperty(value => value.ClaimToken, (Guid?)null)
                .SetProperty(value => value.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(value => value.FailureCategory, FixFailureCategory.TransientInfrastructure)
                .SetProperty(value => value.SafeFailureCode, "worker-lease-lost")
                .SetProperty(value => value.FailedOperationCount, 1)
                .SetProperty(value => value.CompletedAt, now), cancellationToken);
        var job = await db.FixExecutionJobs.FromSqlRaw("""
            select * from public.fix_execution_jobs
            where (state = 'Queued' and (next_attempt_at is null or next_attempt_at <= now()))
               or (state = 'Processing' and lease_expires_at < now() and attempt_count < max_attempts)
            order by created_at
            for update skip locked
            limit 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (job is null) return null;
        var token = Guid.NewGuid();
        job.State = FixExecutionState.Processing;
        job.StartedAt ??= now;
        job.ClaimToken = token;
        job.AttemptCount++;
        job.NextAttemptAt = null;
        job.FailureCategory = null;
        job.SafeFailureCode = null;
        job.LeaseExpiresAt = now.Add(LeaseDuration);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(job.Id, token, job.AttemptCount, job.LeaseExpiresAt.Value);
    }

    internal async Task<bool> HeartbeatAsync(FixExecutionClaim claim, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var changed = await db.FixExecutionJobs
            .Where(value => value.Id == claim.ExecutionId && value.State == FixExecutionState.Processing
                && value.ClaimToken == claim.Token && value.LeaseExpiresAt > DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseExpiresAt,
                DateTimeOffset.UtcNow.Add(LeaseDuration)), cancellationToken);
        return changed == 1;
    }

    internal async Task RetryOrFailAsync(FixExecutionClaim claim, FixExecutionException failure,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.FixExecutionJobs.FromSqlInterpolated(
            $"select * from public.fix_execution_jobs where id = {claim.ExecutionId} for update")
            .SingleOrDefaultAsync(cancellationToken);
        if (job is not null && (job.State != FixExecutionState.Processing || job.ClaimToken != claim.Token)) job = null;
        if (job is null) return;
        job.FailureCategory = failure.Category;
        job.SafeFailureCode = FixFailureCatalog.IsSafe(failure.DiagnosticCode)
            ? failure.DiagnosticCode : "database-finalization-terminal";
        job.ClaimToken = null;
        job.LeaseExpiresAt = null;
        if (FixRetryPolicy.ShouldRetry(failure.Category, job.AttemptCount, job.MaxAttempts))
        {
            job.State = FixExecutionState.Queued;
            job.NextAttemptAt = DateTimeOffset.UtcNow.Add(FixRetryPolicy.Backoff);
        }
        else
        {
            job.State = FixExecutionState.Failed;
            job.FailedOperationCount = Math.Min(1, job.PlannedOperationCount);
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.NextAttemptAt = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
