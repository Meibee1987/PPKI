using Microsoft.EntityFrameworkCore;
using Ppki.Domain;
using Ppki.Infrastructure;

namespace Ppki.Worker;

public sealed class QueuedFixExecutionWorker(
    ILogger<QueuedFixExecutionWorker> logger,
    IConfiguration configuration,
    IDbContextFactory<PpkiDbContext> dbFactory,
    FixExecutionProcessor processor) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Max(1, int.TryParse(configuration["Worker:PollSeconds"], out var value) ? value : 2);
        logger.LogInformation("PPKI fix execution worker started with {PollSeconds}s polling.", pollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? executionId = null;
            try
            {
                executionId = await ClaimAsync(stoppingToken);
                if (executionId is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
                    continue;
                }
                logger.LogInformation("Processing fix execution {ExecutionId}.", executionId);
                await processor.ProcessAsync(executionId.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                var code = exception is Ppki.Application.FixExecutionException safe
                    ? safe.DiagnosticCode : "fix-execution-failed";
                logger.LogError("Fix execution iteration failed for {ExecutionId}; Code={FailureCode}.", executionId, code);
                if (executionId is not null) await FailAsync(executionId.Value, code, CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
        }
    }

    private async Task<Guid?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.FixExecutionJobs.FromSqlRaw("""
            select * from public.fix_execution_jobs
            where state = 'Queued' or (state = 'Processing' and lease_expires_at < now())
            order by created_at
            for update skip locked
            limit 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (job is null) return null;
        var now = DateTimeOffset.UtcNow;
        job.State = FixExecutionState.Processing;
        job.StartedAt ??= now;
        job.LeaseExpiresAt = now.Add(LeaseDuration);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job.Id;
    }

    private async Task FailAsync(Guid executionId, string code, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.FixExecutionJobs.SingleOrDefaultAsync(value => value.Id == executionId, cancellationToken);
        if (job is null || job.State != FixExecutionState.Processing) return;
        job.State = FixExecutionState.Failed;
        job.FailedOperationCount = Math.Min(1, job.PlannedOperationCount);
        job.SafeFailureCode = SafeCode(code) ? code : "fix-execution-failed";
        job.LeaseExpiresAt = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool SafeCode(string value) => value is { Length: > 0 and <= 128 }
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}
