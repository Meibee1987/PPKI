using Microsoft.EntityFrameworkCore;
using Ppki.Domain;
using Ppki.Infrastructure;
using Ppki.RuleEngine;

namespace Ppki.Worker;

public sealed class QueuedAuditWorker(
    ILogger<QueuedAuditWorker> logger,
    IConfiguration configuration,
    IDbContextFactory<PpkiDbContext> dbFactory,
    AuditRunner auditRunner) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredPollSeconds = int.TryParse(configuration["Worker:PollSeconds"], out var parsed)
            ? parsed
            : 2;
        var pollSeconds = Math.Max(1, configuredPollSeconds);
        logger.LogInformation("PPKI audit worker started with {PollSeconds}s polling.", pollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? auditId = null;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var queued = await db.AuditJobs
                    .Where(x => x.Status == AuditJobStatus.Queued)
                    .OrderBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);

                if (queued is not null)
                {
                    queued.Status = AuditJobStatus.Processing;
                    queued.StartedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                    auditId = queued.Id;
                }

                if (auditId is not null)
                {
                    logger.LogInformation("Processing audit {AuditId}.", auditId);
                    await auditRunner.RunAsync(auditId.Value, stoppingToken);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Audit worker iteration failed for {AuditId}.", auditId);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
        }
    }
}
