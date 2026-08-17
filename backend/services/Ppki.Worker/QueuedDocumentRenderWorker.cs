using Microsoft.EntityFrameworkCore;
using Ppki.Domain;
using Ppki.Infrastructure;
using Ppki.RenderEngine;

namespace Ppki.Worker;

public readonly record struct DocumentRenderClaim(Guid JobId, Guid Token, int AttemptNumber, DateTimeOffset LeaseExpiresAt);

public sealed class QueuedDocumentRenderWorker(
    IServiceScopeFactory scopes,
    ILogger<QueuedDocumentRenderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DocumentRenderClaim? claim = null;
            try
            {
                claim = await ClaimAsync(stoppingToken);
                if (claim is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DocumentRenderProcessor>()
                    .ProcessAsync(claim.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (DocumentRenderException exception)
            {
                logger.LogWarning("Document render failed safely; Code={Code}; Retryable={Retryable}.",
                    exception.DiagnosticCode, exception.Retryable);
                if (claim is not null) await RetryOrFailAsync(claim.Value, exception, CancellationToken.None);
            }
            catch (Exception)
            {
                logger.LogError("Document render failed with an unexpected safe failure.");
                if (claim is not null)
                    await RetryOrFailAsync(claim.Value,
                        new DocumentRenderException("render-unexpected-failure", retryable: false), CancellationToken.None);
            }
        }
    }

    internal async Task<DocumentRenderClaim?> ClaimAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PpkiDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.DocumentRenderJobs
            .Where(value => value.State == DocumentRenderState.Processing && value.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(value => value.State, DocumentRenderState.Pending)
                .SetProperty(value => value.ClaimToken, (Guid?)null)
                .SetProperty(value => value.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(value => value.NextAttemptAt, now), cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.DocumentRenderJobs.FromSqlInterpolated($$"""
            select * from public.document_render_jobs
            where state = 'Pending' and (next_attempt_at is null or next_attempt_at <= {{now}})
            order by priority desc, created_at, id
            for update skip locked
            limit 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (job is null) { await transaction.CommitAsync(cancellationToken); return null; }
        var token = Guid.NewGuid();
        job.State = DocumentRenderState.Processing;
        job.ClaimToken = token;
        job.AttemptCount++;
        job.StartedAt ??= now;
        job.LeaseExpiresAt = now.Add(LeaseDuration);
        job.NextAttemptAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(job.Id, token, job.AttemptCount, job.LeaseExpiresAt.Value);
    }

    internal async Task RetryOrFailAsync(
        DocumentRenderClaim claim,
        DocumentRenderException failure,
        CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PpkiDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var job = await db.DocumentRenderJobs.FromSqlInterpolated(
            $"select * from public.document_render_jobs where id = {claim.JobId} for update")
            .SingleOrDefaultAsync(cancellationToken);
        if (job is null || job.State != DocumentRenderState.Processing || job.ClaimToken != claim.Token)
        { await transaction.CommitAsync(cancellationToken); return; }
        var now = DateTimeOffset.UtcNow;
        job.ClaimToken = null;
        job.LeaseExpiresAt = null;
        if (failure.Retryable && job.AttemptCount < job.MaxAttempts)
        {
            job.State = DocumentRenderState.Pending;
            job.NextAttemptAt = now.AddSeconds(Math.Pow(2, job.AttemptCount));
        }
        else
        {
            job.State = DocumentRenderState.Failed;
            job.SafeFailureCode = failure.DiagnosticCode;
            job.CompletedAt = now;
            job.NextAttemptAt = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
