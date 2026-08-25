using Ppki.Application;

namespace Ppki.Worker;

public sealed class ApprovedFixPlanQueueRecoveryProcessor(
    IFixPlanApprovalQueueRecoveryRepository repository,
    IFixPlanApprovalApplyQueue applyQueue)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var candidate = await repository.LoadNextMissingAsync(cancellationToken);
        if (candidate is null) return false;
        var result = await applyQueue.EnqueueAsync(candidate.Plan, candidate.Snapshot, cancellationToken);
        if (result.ConflictCode is not null)
            throw new FixPlanApprovalException(result.ConflictCode);
        if (result.Job is null)
            throw new FixPlanApprovalException("fix-plan-approval-apply-queue-failed");
        return true;
    }
}

public sealed class ApprovedFixPlanQueueRecoveryWorker(
    ILogger<ApprovedFixPlanQueueRecoveryWorker> logger,
    IConfiguration configuration,
    ApprovedFixPlanQueueRecoveryProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Max(1,
            int.TryParse(configuration["Worker:PollSeconds"], out var value) ? value : 2);
        logger.LogInformation("Approved fix plan queue recovery started with {PollSeconds}s polling.", pollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recovered = await processor.ProcessNextAsync(stoppingToken);
                if (!recovered) await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (FixPlanApprovalException exception)
            {
                logger.LogWarning("Approved fix plan queue recovery failed safely; Code={Code}.",
                    exception.DiagnosticCode);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (Exception)
            {
                logger.LogError("Approved fix plan queue recovery failed with an unexpected safe failure.");
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
        }
    }
}
