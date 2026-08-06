using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed record ReauditSourceContext(
    Guid SourceFixExecutionId,
    FixExecutionState ExecutionState,
    Guid SourceAuditId,
    AuditJobStatus SourceAuditStatus,
    Guid SourceAuditDocumentVersionId,
    Guid SourceDocumentVersionId,
    Guid SourceDocumentId,
    Guid? ResultDocumentVersionId,
    Guid? ResultDocumentId,
    Guid ProfileVersionId,
    DocumentKind? DocumentKindSnapshot,
    string? ResolvedRuleSetHash,
    int ApplicableRuleCount,
    Guid RequestedByUserId);

public static class ReauditCreationContract
{
    public static string? Validate(
        ReauditSourceContext source,
        IReadOnlyList<AuditRuleSnapshot> snapshots,
        IResolvedRuleSetHasher hasher)
    {
        if (source.ExecutionState != FixExecutionState.Completed)
            return "reaudit-execution-not-completed";
        if (source.ResultDocumentVersionId is null)
            return "reaudit-result-version-missing";
        if (source.SourceAuditStatus != AuditJobStatus.Completed)
            return "reaudit-source-audit-not-completed";
        if (source.SourceAuditDocumentVersionId != source.SourceDocumentVersionId)
            return "reaudit-source-lineage-invalid";
        if (source.ResultDocumentId is null || source.ResultDocumentId != source.SourceDocumentId)
            return "reaudit-result-lineage-invalid";
        if (source.ProfileVersionId == Guid.Empty || source.DocumentKindSnapshot is null)
            return "reaudit-source-context-invalid";
        if (!ValidHash(source.ResolvedRuleSetHash) || source.ApplicableRuleCount <= 0
            || snapshots.Count != source.ApplicableRuleCount
            || snapshots.Select(value => value.RuleCode).Distinct(StringComparer.Ordinal).Count() != snapshots.Count
            || snapshots.Select(value => value.Ordinal).Distinct().Count() != snapshots.Count)
            return "reaudit-source-snapshots-invalid";
        try
        {
            if (!string.Equals(hasher.Hash(snapshots), source.ResolvedRuleSetHash, StringComparison.Ordinal))
                return "reaudit-source-snapshot-hash-mismatch";
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException
            or InvalidOperationException or ArgumentException)
        {
            return "reaudit-source-snapshots-invalid";
        }
        return null;
    }

    public static IReadOnlyList<AuditRuleSnapshot> Clone(
        Guid targetAuditId,
        IReadOnlyList<AuditRuleSnapshot> source,
        DateTimeOffset createdAt) => source
        .OrderBy(value => value.Ordinal)
        .ThenBy(value => value.RuleCode, StringComparer.Ordinal)
        .Select(value => new AuditRuleSnapshot
        {
            Id = Guid.NewGuid(),
            AuditJobId = targetAuditId,
            RuleId = value.RuleId,
            RuleCode = value.RuleCode,
            Domain = value.Domain,
            Subdomain = value.Subdomain,
            AppliesTo = value.AppliesTo,
            Element = value.Element,
            RequirementJson = value.RequirementJson,
            ValidationKey = value.ValidationKey,
            ValidationJson = value.ValidationJson,
            Severity = value.Severity,
            FixMode = value.FixMode,
            SourceReferenceJson = value.SourceReferenceJson,
            Layer = value.Layer,
            Precedence = value.Precedence,
            Ordinal = value.Ordinal,
            SnapshotSchemaVersion = value.SnapshotSchemaVersion,
            CreatedAt = createdAt
        })
        .ToArray();

    private static bool ValidHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class ReauditService(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IResolvedRuleSetHasher hasher,
    IAuditTrailWriter auditTrail,
    TimeProvider timeProvider) : IReauditService
{
    public async Task<ReauditAccepted?> CreateAsync(
        Guid sourceFixExecutionId,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var existing = await ExistingAsync(db, sourceFixExecutionId, ownerUserId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Map(existing, true);
        }

        var source = await OwnedSource(db, sourceFixExecutionId, ownerUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var sourceSnapshots = await db.AuditRuleSnapshots.AsNoTracking()
            .Where(value => value.AuditJobId == source.SourceAuditId)
            .OrderBy(value => value.Ordinal)
            .ThenBy(value => value.RuleCode)
            .ToListAsync(cancellationToken);
        var failureCode = ReauditCreationContract.Validate(source, sourceSnapshots, hasher);
        if (failureCode is not null) throw new ReauditException(failureCode);

        var createdAt = timeProvider.GetUtcNow();
        var audit = new AuditJob
        {
            Id = Guid.NewGuid(),
            DocumentVersionId = source.ResultDocumentVersionId!.Value,
            ProfileVersionId = source.ProfileVersionId,
            DocumentKindSnapshot = source.DocumentKindSnapshot,
            RequestedByUserId = source.RequestedByUserId,
            SourceAuditJobId = source.SourceAuditId,
            SourceFixExecutionId = source.SourceFixExecutionId,
            Status = AuditJobStatus.Queued,
            ResolvedRuleSetHash = source.ResolvedRuleSetHash,
            ApplicableRuleCount = source.ApplicableRuleCount,
            Score = null,
            CreatedAt = createdAt
        };
        var clones = ReauditCreationContract.Clone(audit.Id, sourceSnapshots, createdAt);
        if (!string.Equals(hasher.Hash(clones), source.ResolvedRuleSetHash, StringComparison.Ordinal))
            throw new ReauditException("reaudit-cloned-snapshot-hash-mismatch");

        var eventContext = AuditEventContext.User(ownerUserId, audit.Id, source.SourceFixExecutionId);
        await auditTrail.SetTransactionContextAsync(db, eventContext, cancellationToken);
        db.AuditJobs.Add(audit);
        db.AuditRuleSnapshots.AddRange(clones);
        auditTrail.Add(db, eventContext, new AuditEventData(
            AuditActions.AuditRequested,
            AuditResourceTypes.AuditJob,
            audit.Id,
            ownerUserId,
            AuditEventMetadata.Create(("audit_status", "Queued"))));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Map(audit, false);
        }
        catch (Exception exception) when (ConcurrencyConflict(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await using var replayDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            var canonical = await ExistingAsync(replayDb, sourceFixExecutionId, ownerUserId, cancellationToken);
            return canonical is null
                ? throw new ReauditException("reaudit-concurrency-conflict")
                : Map(canonical, true);
        }
    }

    public static IQueryable<ReauditSourceContext> OwnedSource(
        PpkiDbContext db,
        Guid executionId,
        Guid ownerUserId) => db.FixExecutionJobs.AsNoTracking()
        .Where(value => value.Id == executionId)
        .Select(value => new ReauditSourceContext(
            value.Id,
            value.State,
            value.AuditJobId,
            value.AuditJob!.Status,
            value.AuditJob.DocumentVersionId,
            value.SourceDocumentVersionId,
            value.SourceDocumentVersion!.DocumentId,
            value.ResultDocumentVersionId,
            value.ResultDocumentVersion == null ? null : value.ResultDocumentVersion.DocumentId,
            value.AuditJob.ProfileVersionId,
            value.AuditJob.DocumentKindSnapshot,
            value.AuditJob.ResolvedRuleSetHash,
            value.AuditJob.ApplicableRuleCount,
            value.RequestedByUserId));

    private static async Task<AuditJob?> ExistingAsync(
        PpkiDbContext db,
        Guid sourceFixExecutionId,
        Guid ownerUserId,
        CancellationToken cancellationToken) => await db.AuditJobs.AsNoTracking()
        .Where(value => value.SourceFixExecutionId == sourceFixExecutionId)
        .SingleOrDefaultAsync(cancellationToken);

    private static ReauditAccepted Map(AuditJob audit, bool replayed) => new(
        audit.Id,
        audit.Status.ToString(),
        audit.SourceAuditJobId!.Value,
        audit.SourceFixExecutionId!.Value,
        audit.DocumentVersionId,
        audit.ProfileVersionId,
        audit.ResolvedRuleSetHash!,
        audit.DocumentKindSnapshot!.Value,
        audit.CreatedAt,
        replayed);

    private static bool ConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.SerializationFailure }) return true;
        return false;
    }
}
