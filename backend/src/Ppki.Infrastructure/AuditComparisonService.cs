using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed record AuditComparisonSourceContext(
    Guid FixExecutionId,
    FixExecutionState ExecutionState,
    Guid SourceAuditId,
    AuditJobStatus SourceAuditStatus,
    Guid SourceAuditDocumentVersionId,
    Guid SourceDocumentVersionId,
    Guid SourceDocumentId,
    Guid? ResultDocumentVersionId,
    Guid? ResultDocumentId,
    Guid SourceProfileVersionId,
    DocumentKind? SourceDocumentKind,
    string? SourceRuleSetHash,
    int SourceApplicableRuleCount,
    decimal? SourceScore);

public sealed record AuditComparisonResultContext(
    Guid ResultAuditId,
    AuditJobStatus ResultAuditStatus,
    Guid? SourceAuditId,
    Guid? SourceFixExecutionId,
    Guid ResultDocumentVersionId,
    Guid ResultProfileVersionId,
    DocumentKind? ResultDocumentKind,
    string? ResultRuleSetHash,
    int ResultApplicableRuleCount,
    decimal? ResultScore);

public sealed class AuditComparisonService(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IResolvedRuleSetHasher ruleSetHasher,
    IAuditScoreCalculator scoreCalculator) : IAuditComparisonService
{
    public async Task<AuditComparisonDto?> GetAsync(
        Guid fixExecutionId,
        Guid ownerUserId,
        AuditComparisonQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await OwnedExecution(db, fixExecutionId, ownerUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null) return null;
        ValidateSource(source);

        var result = await OwnedResultAudit(db, fixExecutionId, ownerUserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (result is null)
            throw new AuditComparisonException("audit-comparison-result-audit-missing");
        ValidateResult(source, result);

        var sourceSnapshots = await db.AuditRuleSnapshots.AsNoTracking()
            .Where(value => value.AuditJobId == source.SourceAuditId)
            .OrderBy(value => value.Ordinal).ThenBy(value => value.RuleCode)
            .ToListAsync(cancellationToken);
        var resultSnapshots = await db.AuditRuleSnapshots.AsNoTracking()
            .Where(value => value.AuditJobId == result.ResultAuditId)
            .OrderBy(value => value.Ordinal).ThenBy(value => value.RuleCode)
            .ToListAsync(cancellationToken);
        ValidateSnapshots(source, result, sourceSnapshots, resultSnapshots);

        var sourceQuery = AuditReadQueries.OwnedFindings(db, source.SourceAuditId, ownerUserId);
        var resultQuery = AuditReadQueries.OwnedFindings(db, result.ResultAuditId, ownerUserId);
        var sourceCount = await sourceQuery.CountAsync(cancellationToken);
        var resultCount = await resultQuery.CountAsync(cancellationToken);
        if (sourceCount > AuditFindingQuery.MaximumFindingCount
            || resultCount > AuditFindingQuery.MaximumFindingCount)
            throw new AuditComparisonException("audit-comparison-finding-limit-exceeded");

        var sourceFindings = (await sourceQuery.Take(AuditFindingQuery.MaximumFindingCount)
                .ToListAsync(cancellationToken)).Select(ToSnapshot).ToArray();
        var resultFindings = (await resultQuery.Take(AuditFindingQuery.MaximumFindingCount)
                .ToListAsync(cancellationToken)).Select(ToSnapshot).ToArray();
        var allItems = AuditComparisonEngine.Compare(sourceFindings, resultFindings);
        if (allItems.Count > AuditComparisonQuery.MaximumComparisonItems)
            throw new AuditComparisonException("audit-comparison-item-limit-exceeded");

        var sourceScore = Score(source.SourceAuditStatus, source.SourceApplicableRuleCount);
        var resultScore = Score(result.ResultAuditStatus, result.ResultApplicableRuleCount);
        var summary = AuditComparisonEngine.Summary(
            allItems, sourceCount, resultCount, sourceScore, resultScore);
        var filtered = AuditComparisonEngine.ApplyFilters(allItems, query).ToArray();
        var offset = (query.Page - 1) * query.PageSize;
        var pageItems = filtered.Skip(offset).Take(query.PageSize).ToArray();
        return new(source.SourceAuditId, result.ResultAuditId, source.FixExecutionId,
            source.SourceDocumentVersionId, source.ResultDocumentVersionId!.Value,
            "Ready", summary, query.Page, query.PageSize, filtered.Length, pageItems);
    }

    public static IQueryable<AuditComparisonSourceContext> OwnedExecution(
        PpkiDbContext db,
        Guid fixExecutionId,
        Guid ownerUserId) => db.FixExecutionJobs.AsNoTracking()
        .Where(value => value.Id == fixExecutionId)
        .Select(value => new AuditComparisonSourceContext(
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
            value.AuditJob.Score));

    public static IQueryable<AuditComparisonResultContext> OwnedResultAudit(
        PpkiDbContext db,
        Guid fixExecutionId,
        Guid ownerUserId) => db.AuditJobs.AsNoTracking()
        .Where(value => value.SourceFixExecutionId == fixExecutionId)
        .Select(value => new AuditComparisonResultContext(
            value.Id,
            value.Status,
            value.SourceAuditJobId,
            value.SourceFixExecutionId,
            value.DocumentVersionId,
            value.ProfileVersionId,
            value.DocumentKindSnapshot,
            value.ResolvedRuleSetHash,
            value.ApplicableRuleCount,
            value.Score));

    public static void ValidateSource(AuditComparisonSourceContext source)
    {
        if (source.ExecutionState != FixExecutionState.Completed)
            throw new AuditComparisonException("audit-comparison-execution-not-completed");
        if (source.ResultDocumentVersionId is null || source.ResultDocumentId is null)
            throw new AuditComparisonException("audit-comparison-result-version-missing");
        if (source.SourceAuditStatus != AuditJobStatus.Completed)
            throw new AuditComparisonException("audit-comparison-source-audit-not-completed");
        if (source.SourceAuditDocumentVersionId != source.SourceDocumentVersionId
            || source.SourceDocumentId != source.ResultDocumentId)
            throw new AuditComparisonException("audit-comparison-source-lineage-invalid");
    }

    public static void ValidateResult(
        AuditComparisonSourceContext source,
        AuditComparisonResultContext result)
    {
        if (result.ResultAuditStatus != AuditJobStatus.Completed)
            throw new AuditComparisonException("audit-comparison-result-audit-not-completed");
        if (result.SourceFixExecutionId != source.FixExecutionId
            || result.SourceAuditId != source.SourceAuditId
            || result.ResultDocumentVersionId != source.ResultDocumentVersionId)
            throw new AuditComparisonException("audit-comparison-result-lineage-invalid");
        if (result.ResultProfileVersionId != source.SourceProfileVersionId
            || result.ResultDocumentKind != source.SourceDocumentKind
            || !string.Equals(result.ResultRuleSetHash, source.SourceRuleSetHash, StringComparison.Ordinal)
            || result.ResultApplicableRuleCount != source.SourceApplicableRuleCount)
            throw new AuditComparisonException("audit-comparison-historical-context-mismatch");
    }

    private void ValidateSnapshots(
        AuditComparisonSourceContext source,
        AuditComparisonResultContext result,
        IReadOnlyList<AuditRuleSnapshot> sourceSnapshots,
        IReadOnlyList<AuditRuleSnapshot> resultSnapshots)
    {
        if (sourceSnapshots.Count == 0
            || sourceSnapshots.Count != source.SourceApplicableRuleCount
            || resultSnapshots.Count != result.ResultApplicableRuleCount
            || !string.Equals(ruleSetHasher.Hash(sourceSnapshots), source.SourceRuleSetHash, StringComparison.Ordinal)
            || !string.Equals(ruleSetHasher.Hash(resultSnapshots), result.ResultRuleSetHash, StringComparison.Ordinal)
            || !HistoricalSnapshotsEqual(sourceSnapshots, resultSnapshots))
            throw new AuditComparisonException("audit-comparison-historical-context-mismatch");
    }

    private AuditComparisonScoreDto Score(AuditJobStatus status, int applicableRuleCount)
    {
        // A live policy must not be used to reinterpret either historical audit.
        var score = scoreCalculator.Calculate(new(status, applicableRuleCount, []), policy: null);
        return new(score.State.ToString(), score.Score, score.PolicyVersion, score.DiagnosticCode);
    }

    public static bool HistoricalSnapshotsEqual(
        IReadOnlyList<AuditRuleSnapshot> source,
        IReadOnlyList<AuditRuleSnapshot> result) =>
        source.Count == result.Count
        && source.Zip(result, SnapshotEquals).All(value => value);

    private static bool SnapshotEquals(AuditRuleSnapshot source, AuditRuleSnapshot result) =>
        source.RuleId == result.RuleId
        && source.RuleCode == result.RuleCode
        && source.Domain == result.Domain
        && source.Subdomain == result.Subdomain
        && source.AppliesTo == result.AppliesTo
        && source.Element == result.Element
        && source.RequirementJson == result.RequirementJson
        && source.ValidationKey == result.ValidationKey
        && source.ValidationJson == result.ValidationJson
        && source.Severity == result.Severity
        && source.FixMode == result.FixMode
        && source.SourceReferenceJson == result.SourceReferenceJson
        && source.Layer == result.Layer
        && source.Precedence == result.Precedence
        && source.Ordinal == result.Ordinal
        && source.SnapshotSchemaVersion == result.SnapshotSchemaVersion;

    public static AuditComparisonFindingSnapshot ToSnapshot(AuditFindingReadRow value) => new(
        value.Id, value.AuditId, value.RuleOrdinal, value.RuleCode, value.Domain,
        value.ValidationKey, value.Element, value.Severity, value.FixMode,
        value.FindingState, value.ReasonCode, value.ActualJson, value.ExpectedJson,
        value.LocationJson, value.Confidence, value.SourceSection, value.PdfPage,
        value.PrintedPage);
}
