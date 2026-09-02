using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;

namespace Ppki.Worker;

public sealed record AutomaticReauditRecoveryCandidate(Guid FixExecutionId, Guid OwnerUserId);

public sealed class AutomaticReauditRecoveryProcessor(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IReauditService reaudits,
    IFindingResolutionService resolutions)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var publication = await MissingReaudit(db)
            .Select(value => new AutomaticReauditRecoveryCandidate(value.FixExecutionId, value.OwnerUserId))
            .FirstOrDefaultAsync(cancellationToken);
        if (publication is not null)
        {
            var accepted = await reaudits.CreateAsync(publication.FixExecutionId,
                publication.OwnerUserId, cancellationToken);
            if (accepted is null) throw new ReauditException("automatic-reaudit-owner-mismatch");
            await resolutions.ReconcileAsync(publication.FixExecutionId,
                publication.OwnerUserId, cancellationToken);
            return true;
        }

        var reconciliation = await MissingReconciliation(db)
            .Select(value => new AutomaticReauditRecoveryCandidate(value.FixExecutionId, value.OwnerUserId))
            .FirstOrDefaultAsync(cancellationToken);
        if (reconciliation is null) return false;
        await resolutions.ReconcileAsync(reconciliation.FixExecutionId,
            reconciliation.OwnerUserId, cancellationToken);
        return true;
    }

    public static IQueryable<MissingReauditRow> MissingReaudit(PpkiDbContext db) =>
        db.FixExecutionJobs.AsNoTracking()
            .Where(value => value.State == FixExecutionState.Completed
                && value.ResultDocumentVersionId != null
                && !db.AuditJobs.Any(audit => audit.SourceFixExecutionId == value.Id))
            .OrderBy(value => value.CompletedAt).ThenBy(value => value.Id)
            .Select(value => new MissingReauditRow(value.Id,
                value.SourceDocumentVersion!.Document!.OwnerUserId,
                value.CompletedAt!.Value));

    public static IQueryable<MissingReconciliationRow> MissingReconciliation(PpkiDbContext db) =>
        db.AuditJobs.AsNoTracking()
            .Where(audit => audit.SourceFixExecutionId != null
                && (audit.Status == AuditJobStatus.Queued || audit.Status == AuditJobStatus.Processing
                    || audit.Status == AuditJobStatus.Completed)
                && (audit.Status == AuditJobStatus.Completed
                    ? !db.FindingResolutionEvents.Any(value => value.SourceReauditJobId == audit.Id
                        && (value.EventType == FindingResolutionEventType.VerificationResolvedObserved
                            || value.EventType == FindingResolutionEventType.VerificationStillDetectedObserved))
                    : !db.FindingResolutionEvents.Any(value => value.SourceReauditJobId == audit.Id
                        && value.EventType == FindingResolutionEventType.ReauditPendingObserved)))
            .OrderBy(audit => audit.CreatedAt).ThenBy(audit => audit.SourceFixExecutionId)
            .Select(audit => new MissingReconciliationRow(audit.SourceFixExecutionId!.Value,
                audit.DocumentVersion!.Document!.OwnerUserId, audit.CreatedAt));
}

public sealed record MissingReauditRow(Guid FixExecutionId, Guid OwnerUserId, DateTimeOffset CompletedAt);
public sealed record MissingReconciliationRow(Guid FixExecutionId, Guid OwnerUserId, DateTimeOffset ReauditCreatedAt);

public sealed class AutomaticReauditRecoveryWorker(
    ILogger<AutomaticReauditRecoveryWorker> logger,
    IConfiguration configuration,
    AutomaticReauditRecoveryProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Max(1,
            int.TryParse(configuration["Worker:PollSeconds"], out var value) ? value : 2);
        logger.LogInformation("Automatic re-audit recovery worker started with {PollSeconds}s polling.", pollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.ProcessNextAsync(stoppingToken);
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogError("Automatic re-audit recovery iteration failed safely.");
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
        }
    }
}
