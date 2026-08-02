using Microsoft.EntityFrameworkCore;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public interface IAuditTrailWriter
{
    Task SetTransactionContextAsync(
        PpkiDbContext db,
        AuditEventContext context,
        CancellationToken cancellationToken);

    void Add(
        PpkiDbContext db,
        AuditEventContext context,
        AuditEventData data);
}

public sealed class AuditTrailWriter : IAuditTrailWriter
{
    public async Task SetTransactionContextAsync(
        PpkiDbContext db,
        AuditEventContext context,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Audit context requires an active database transaction.");
        }

        var actorUserId = context.ActorUserId?.ToString("D") ?? string.Empty;
        var actorService = context.ActorService ?? string.Empty;
        var correlationId = context.CorrelationId.ToString("D");
        var causationId = context.CausationId?.ToString("D") ?? string.Empty;
        var requestId = context.RequestId ?? string.Empty;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            select
              set_config('app.actor_user_id', {actorUserId}, true),
              set_config('app.actor_service', {actorService}, true),
              set_config('app.correlation_id', {correlationId}, true),
              set_config('app.causation_id', {causationId}, true),
              set_config('app.request_id', {requestId}, true)
            """, cancellationToken);
    }

    public void Add(PpkiDbContext db, AuditEventContext context, AuditEventData data) =>
        db.AuditTrailEvents.Add(AuditTrailEvent.Create(context, data));
}
