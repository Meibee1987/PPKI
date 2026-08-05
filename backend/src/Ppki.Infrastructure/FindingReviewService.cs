using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class AdminFindingReviewAuthorizationService(IDbContextFactory<PpkiDbContext> dbFactory)
    : IAdminFindingReviewAuthorizationService
{
    public async Task<UserRole?> GetAuthoritativeRoleAsync(Guid actorUserId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.Database.SqlQueryRaw<string>(
            "select role as \"Value\" from public.user_profiles where id = @actor_user_id",
            new NpgsqlParameter<Guid>("actor_user_id", actorUserId)).SingleOrDefaultAsync(cancellationToken);
        return UserRoleDatabase.TryParseExact(value, out var role) ? role : null;
    }

    public async Task<bool> CanDecideFindingAsync(Guid actorUserId, Guid auditId, Guid findingId,
        CancellationToken cancellationToken)
    {
        if (await GetAuthoritativeRoleAsync(actorUserId, cancellationToken) != UserRole.PPKIAdmin) return false;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AuditFindings.AsNoTracking().AnyAsync(value => value.Id == findingId
            && value.AuditJobId == auditId
            && value.AuditJob!.DocumentVersion!.Document!.OwnerUserId != actorUserId, cancellationToken);
    }
}

internal sealed record FindingReviewResource(Guid FindingId, Guid AuditId, Guid DocumentVersionId, Guid OwnerUserId);
internal sealed record FindingReviewWriteResult(Guid AuditId, Guid FindingId, bool Replayed);
internal enum FindingReviewCommandKind { Request, Decision, ManualReport }

public sealed class FindingReviewService(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IAdminFindingReviewAuthorizationService authorization,
    TimeProvider timeProvider) : IFindingReviewService
{
    private const int MaximumEvents = 100;
    private const int MaximumAttempts = 5;

    public async Task<FindingReviewDto?> GetAsync(Guid auditId, Guid findingId, Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var resource = await ResourceQuery(db, auditId, findingId).SingleOrDefaultAsync(cancellationToken);
        if (resource is null) return null;
        var owner = resource.OwnerUserId == actorUserId;
        var canAdminDecide = !owner && await authorization.CanDecideFindingAsync(
            actorUserId, auditId, findingId, cancellationToken);
        if (!owner && !canAdminDecide) return null;

        var resolutionState = await ResolutionStateAsync(db, findingId, cancellationToken);
        var reviewCase = await db.FindingReviewCases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.AuditFindingId == findingId, cancellationToken);
        if (reviewCase is null)
            return new(null, findingId, auditId, resource.DocumentVersionId, resolutionState,
                FindingReviewState.NoReview, null, null, null, 0, null,
                new(owner && resolutionState != FindingResolutionState.VerifiedResolved, false, false), [], []);

        var eventCount = await db.FindingReviewEvents.AsNoTracking()
            .CountAsync(value => value.ReviewCaseId == reviewCase.Id, cancellationToken);
        var events = await db.FindingReviewEvents.AsNoTracking().Where(value => value.ReviewCaseId == reviewCase.Id)
            .OrderBy(value => value.Sequence).Take(MaximumEvents).ToListAsync(cancellationToken);
        var latest = events.LastOrDefault();
        var reviewState = FindingReviewProjection.State(latest?.EventType);
        var requested = events.LastOrDefault(value => value.EventType == FindingReviewEventType.ReviewRequested)
            ?.RequestedDisposition;
        var canRequest = owner && resolutionState != FindingResolutionState.VerifiedResolved
            && reviewState is FindingReviewState.NoReview or FindingReviewState.NeedsRevision;
        var canReport = owner && reviewState == FindingReviewState.ManualRemediationApproved;
        var canDecide = canAdminDecide && reviewState == FindingReviewState.PendingReview;
        var allowed = canDecide && requested is not null ? FindingReviewProjection.Allowed(requested.Value) : [];
        return new(reviewCase.Id, findingId, auditId, resource.DocumentVersionId, resolutionState, reviewState,
            requested, reviewCase.RequestedByUserId, events.LastOrDefault(value => value.Decision is not null)?.Decision,
            eventCount, latest?.CreatedAt, new(canRequest, canReport, canDecide), allowed,
            events.Select(ToDto).ToArray());
    }

    public Task<FindingReviewCommandResult?> RequestAsync(Guid auditId, Guid findingId, Guid actorUserId,
        Guid idempotencyKey, FindingReviewRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.RequestedDisposition))
            throw new FindingReviewException("finding-review-not-available");
        return ExecuteAsync(FindingReviewCommandKind.Request, auditId, findingId, null, actorUserId,
            idempotencyKey, request.RequestedDisposition, null, request.Note, cancellationToken);
    }

    public Task<FindingReviewCommandResult?> DecideAsync(Guid reviewCaseId, Guid actorUserId,
        Guid idempotencyKey, FindingReviewDecisionRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Decision))
            throw new FindingReviewException("finding-review-invalid-transition");
        return ExecuteAsync(FindingReviewCommandKind.Decision, null, null, reviewCaseId, actorUserId,
            idempotencyKey, null, request.Decision, request.Note, cancellationToken);
    }

    public Task<FindingReviewCommandResult?> ReportManualRemediationAsync(Guid reviewCaseId, Guid actorUserId,
        Guid idempotencyKey, ManualRemediationReportRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(FindingReviewCommandKind.ManualReport, null, null, reviewCaseId, actorUserId,
            idempotencyKey, null, null, request.Note, cancellationToken);

    private async Task<FindingReviewCommandResult?> ExecuteAsync(FindingReviewCommandKind kind,
        Guid? auditId, Guid? findingId, Guid? reviewCaseId, Guid actorUserId, Guid idempotencyKey,
        FindingReviewRequestedDisposition? disposition, FindingReviewDecision? decision, string? suppliedNote,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty) throw new FindingReviewException("finding-review-idempotency-key-invalid");
        var note = NormalizeNote(suppliedNote);
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            try
            {
                var written = await ExecuteAttemptAsync(kind, auditId, findingId, reviewCaseId, actorUserId,
                    idempotencyKey, disposition, decision, note, cancellationToken);
                if (written is null) return null;
                var review = await GetAsync(written.AuditId, written.FindingId, actorUserId, cancellationToken)
                    ?? throw new FindingReviewException("finding-review-conflict");
                return new(review, written.Replayed);
            }
            catch (Exception exception) when (ConcurrencyConflict(exception))
            {
                if (attempt == MaximumAttempts - 1) throw new FindingReviewException("finding-review-conflict");
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
        }
        throw new FindingReviewException("finding-review-conflict");
    }

    private async Task<FindingReviewWriteResult?> ExecuteAttemptAsync(FindingReviewCommandKind kind,
        Guid? auditId, Guid? findingId, Guid? reviewCaseId, Guid actorUserId, Guid idempotencyKey,
        FindingReviewRequestedDisposition? disposition, FindingReviewDecision? decision, string? note,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        FindingReviewResource? resource;
        FindingReviewCase? reviewCase;
        if (kind == FindingReviewCommandKind.Request)
        {
            resource = await ResourceQuery(db, auditId!.Value, findingId!.Value, actorUserId)
                .SingleOrDefaultAsync(cancellationToken);
            if (resource is null) { await transaction.CommitAsync(cancellationToken); return null; }
            if (await ResolutionStateAsync(db, resource.FindingId, cancellationToken) == FindingResolutionState.VerifiedResolved)
                throw new FindingReviewException("finding-already-verified-resolved");
            reviewCase = await db.FindingReviewCases.AsNoTracking()
                .SingleOrDefaultAsync(value => value.AuditFindingId == resource.FindingId, cancellationToken);
            if (reviewCase is null)
            {
                reviewCase = new FindingReviewCase
                {
                    AuditFindingId = resource.FindingId, AuditJobId = resource.AuditId,
                    SourceDocumentVersionId = resource.DocumentVersionId,
                    RequestedByUserId = actorUserId, CreatedAt = timeProvider.GetUtcNow()
                };
                db.FindingReviewCases.Add(reviewCase);
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
                reviewCase = await db.FindingReviewCases.AsNoTracking()
                    .SingleAsync(value => value.AuditFindingId == resource.FindingId, cancellationToken);
            }
        }
        else
        {
            var joined = await db.FindingReviewCases.AsNoTracking().Where(value => value.Id == reviewCaseId)
                .Select(value => new { Case = value, Resource = new FindingReviewResource(value.AuditFindingId,
                    value.AuditJobId, value.SourceDocumentVersionId,
                    value.AuditJob!.DocumentVersion!.Document!.OwnerUserId) }).SingleOrDefaultAsync(cancellationToken);
            if (joined is null) { await transaction.CommitAsync(cancellationToken); return null; }
            reviewCase = joined.Case; resource = joined.Resource;
            if (kind == FindingReviewCommandKind.ManualReport && resource.OwnerUserId != actorUserId)
            { await transaction.CommitAsync(cancellationToken); return null; }
            if (kind == FindingReviewCommandKind.Decision)
            {
                if (await authorization.GetAuthoritativeRoleAsync(actorUserId, cancellationToken) != UserRole.PPKIAdmin)
                    throw new FindingReviewException("finding-review-not-reviewer");
                if (resource.OwnerUserId == actorUserId
                    || !await authorization.CanDecideFindingAsync(actorUserId, resource.AuditId,
                        resource.FindingId, cancellationToken))
                    throw new FindingReviewException("finding-review-out-of-scope");
            }
        }

        var events = await db.FindingReviewEvents.AsNoTracking().Where(value => value.ReviewCaseId == reviewCase.Id)
            .OrderBy(value => value.Sequence).ToListAsync(cancellationToken);
        var requested = events.LastOrDefault(value => value.EventType == FindingReviewEventType.ReviewRequested)
            ?.RequestedDisposition;
        var eventType = kind switch
        {
            FindingReviewCommandKind.Request => FindingReviewEventType.ReviewRequested,
            FindingReviewCommandKind.Decision => FindingReviewProjection.Event(decision!.Value),
            _ => FindingReviewEventType.ManualRemediationReported
        };
        var existing = events.SingleOrDefault(value => value.IdempotencyKey == idempotencyKey);
        if (existing is not null)
        {
            if (PayloadMatches(existing, eventType, disposition, decision, actorUserId, note))
            { await transaction.CommitAsync(cancellationToken); return new(resource.AuditId, resource.FindingId, true); }
            throw new FindingReviewException("finding-review-idempotency-conflict");
        }

        var state = FindingReviewProjection.State(events.LastOrDefault()?.EventType);
        if (kind == FindingReviewCommandKind.Request
            && state is not (FindingReviewState.NoReview or FindingReviewState.NeedsRevision)
            || kind == FindingReviewCommandKind.ManualReport
            && state != FindingReviewState.ManualRemediationApproved
            || kind == FindingReviewCommandKind.Decision
            && (state != FindingReviewState.PendingReview || requested is null
                || !FindingReviewProjection.Allowed(requested.Value).Contains(decision!.Value)))
            throw new FindingReviewException("finding-review-invalid-transition");

        db.FindingReviewEvents.Add(new FindingReviewEvent
        {
            ReviewCaseId = reviewCase.Id, Sequence = events.Count + 1, EventType = eventType,
            RequestedDisposition = kind == FindingReviewCommandKind.Request ? disposition : null,
            Decision = kind == FindingReviewCommandKind.Decision ? decision : null,
            ActorUserId = actorUserId, Note = note, IdempotencyKey = idempotencyKey,
            SourceEventKey = $"review-command:{reviewCase.Id:D}:{idempotencyKey:D}",
            CreatedAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(resource.AuditId, resource.FindingId, false);
    }

    public static string? NormalizeNote(string? note)
    {
        if (note is null) return null;
        var normalized = note.Trim();
        if (normalized.Length == 0) return null;
        if (normalized.Length > 1000 || normalized.Any(char.IsControl))
            throw new FindingReviewException("finding-review-note-invalid");
        return normalized;
    }

    private static IQueryable<FindingReviewResource> ResourceQuery(PpkiDbContext db, Guid auditId,
        Guid findingId, Guid? ownerUserId = null)
    {
        var query = db.AuditFindings.AsNoTracking().Where(value => value.Id == findingId
            && value.AuditJobId == auditId);
        if (ownerUserId is not null)
            query = query.Where(value => value.AuditJob!.DocumentVersion!.Document!.OwnerUserId == ownerUserId);
        return query.Select(value => new FindingReviewResource(value.Id, value.AuditJobId,
            value.AuditJob!.DocumentVersionId, value.AuditJob.DocumentVersion!.Document!.OwnerUserId));
    }

    private static async Task<FindingResolutionState> ResolutionStateAsync(PpkiDbContext db, Guid findingId,
        CancellationToken cancellationToken)
    {
        var resolutionCaseId = await db.FindingResolutionCases.AsNoTracking()
            .Where(value => value.SourceAuditFindingId == findingId).Select(value => (Guid?)value.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (resolutionCaseId is null) return FindingResolutionState.Open;
        var latest = await db.FindingResolutionEvents.AsNoTracking().Where(value => value.ResolutionCaseId == resolutionCaseId)
            .OrderByDescending(value => value.Sequence).Select(value => (FindingResolutionEventType?)value.EventType)
            .FirstOrDefaultAsync(cancellationToken);
        return FindingResolutionProjection.State(latest);
    }

    private static FindingReviewEventDto ToDto(FindingReviewEvent value) => new(value.Sequence, value.EventType,
        value.RequestedDisposition, value.Decision, value.ActorUserId, value.Note, value.CreatedAt);

    private static bool PayloadMatches(FindingReviewEvent value, FindingReviewEventType type,
        FindingReviewRequestedDisposition? disposition, FindingReviewDecision? decision, Guid actorUserId, string? note) =>
        value.EventType == type && value.RequestedDisposition == disposition && value.Decision == decision
        && value.ActorUserId == actorUserId && value.Note == note;

    private static bool ConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }) return true;
        return false;
    }
}
