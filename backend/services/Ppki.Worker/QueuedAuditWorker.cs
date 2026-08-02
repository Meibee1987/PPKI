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
                var queuedId = await db.AuditJobs
                    .AsNoTracking()
                    .Where(x => x.Status == AuditJobStatus.Queued)
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(stoppingToken);

                if (queuedId is not null)
                {
                    var claimed = await db.AuditJobs
                        .Where(x => x.Id == queuedId.Value && x.Status == AuditJobStatus.Queued)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.Status, AuditJobStatus.Processing)
                            .SetProperty(x => x.StartedAt, DateTimeOffset.UtcNow), stoppingToken);
                    if (claimed == 1) auditId = queuedId.Value;
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
