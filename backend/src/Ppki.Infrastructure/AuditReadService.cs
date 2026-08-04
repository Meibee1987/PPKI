using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class AuditReadService(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IAuditScoreCalculator scoreCalculator) : IAuditReadService
{
    public async Task<AuditSummaryDto?> GetSummaryAsync(
        Guid auditId,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var audit = await db.AuditJobs
            .AsNoTracking()
            .Where(value => value.Id == auditId
                && value.DocumentVersion!.Document!.OwnerUserId == ownerUserId)
            .Select(value => new AuditSummaryRow(
                value.Id,
                value.Status,
                value.DocumentVersionId,
                value.ProfileVersionId,
                value.DocumentKindSnapshot,
                value.ResolvedRuleSetHash,
                value.ApplicableRuleCount,
                value.StartedAt,
                value.CompletedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (audit is null) return null;

        var countBuckets = await AuditReadQueries.OwnedSummaryBuckets(
                db, auditId, ownerUserId)
            .ToListAsync(cancellationToken);
        var counts = AuditSummaryCounts.FromBuckets(countBuckets);

        // AuditJob has no persisted scoring-policy version yet. Applying a live
        // policy here would rewrite historical meaning, so the read model must
        // report NotConfigured until a policy is explicitly snapshotted.
        var score = scoreCalculator.Calculate(
            new(audit.Status, audit.ApplicableRuleCount, []), policy: null);
        var failure = AuditFailureSummary.FromStatus(audit.Status);

        return new(
            audit.Id,
            audit.Status.ToString(),
            audit.DocumentVersionId,
            audit.ProfileVersionId,
            audit.DocumentKindSnapshot?.ToString(),
            audit.ResolvedRuleSetHash,
            audit.ApplicableRuleCount,
            audit.ApplicableRuleCount,
            counts.FindingCount,
            counts.FindingCount,
            counts.Severity.Error,
            counts.Severity.Warning,
            counts.Severity.Info,
            counts.Severity,
            counts.Domains,
            counts.FixModes,
            score.State,
            score.Score,
            score.PolicyVersion,
            score.Breakdown,
            score.DiagnosticCode,
            audit.StartedAt,
            audit.CompletedAt,
            failure?.Code,
            failure?.Message);
    }

    public async Task<AuditFindingPageDto?> GetFindingsAsync(
        Guid auditId,
        Guid ownerUserId,
        AuditFindingQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var owned = await db.AuditJobs.AsNoTracking().AnyAsync(value =>
            value.Id == auditId
            && value.DocumentVersion!.Document!.OwnerUserId == ownerUserId,
            cancellationToken);
        if (!owned) return null;

        var filtered = AuditReadQueries.OwnedFindings(db, auditId, ownerUserId, query);
        var totalCount = await filtered.CountAsync(cancellationToken);
        if (totalCount > AuditFindingQuery.MaximumFindingCount)
            throw new InvalidOperationException("Persisted finding count exceeds the supported limit.");

        var offset = (query.Page - 1) * query.PageSize;
        var boundedRows = await filtered
            .Take(AuditFindingQuery.MaximumFindingCount)
            .ToListAsync(cancellationToken);
        var rows = AuditReadQueries.ApplyDefaultOrdering(boundedRows)
            .Skip(offset)
            .Take(query.PageSize);

        return new(query.Page, query.PageSize, totalCount, rows.Select(ToListItem).ToArray());
    }

    public async Task<AuditFindingDetailDto?> GetFindingAsync(
        Guid auditId,
        Guid findingId,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await AuditReadQueries.OwnedFindings(
                db, auditId, ownerUserId, findingId: findingId)
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        return new(
            row.Id,
            row.AuditId,
            row.DocumentVersionId,
            row.RuleOrdinal,
            row.RuleCode,
            row.Domain,
            row.ValidationKey,
            row.Element,
            row.Severity.ToString(),
            row.FixMode.ToString(),
            row.FindingState.ToString(),
            row.ReasonCode,
            row.ReasonCode,
            Json(row.ActualJson),
            Json(row.ExpectedJson),
            Json(row.LocationJson),
            row.Confidence,
            Source(row),
            "None");
    }

    private static AuditFindingListItemDto ToListItem(AuditFindingReadRow row) => new(
        row.Id,
        row.AuditId,
        row.RuleOrdinal,
        row.RuleCode,
        row.Domain,
        row.ValidationKey,
        row.Element,
        row.Severity.ToString(),
        row.FixMode.ToString(),
        row.FindingState.ToString(),
        row.ReasonCode,
        row.ReasonCode,
        Json(row.ActualJson),
        Json(row.ExpectedJson),
        Json(row.LocationJson),
        row.Confidence,
        Source(row),
        "None");

    private static AuditFindingSourceDto Source(AuditFindingReadRow row) =>
        new(row.SourceSection, row.PdfPage, row.PrintedPage);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed record AuditSummaryRow(
        Guid Id,
        AuditJobStatus Status,
        Guid DocumentVersionId,
        Guid ProfileVersionId,
        DocumentKind? DocumentKindSnapshot,
        string? ResolvedRuleSetHash,
        int ApplicableRuleCount,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt);

}

public static class AuditReadQueries
{
    public static IQueryable<AuditFindingSummaryBucket> OwnedSummaryBuckets(
        PpkiDbContext db,
        Guid auditId,
        Guid ownerUserId) =>
        from finding in db.AuditFindings.AsNoTracking()
        join snapshot in db.AuditRuleSnapshots.AsNoTracking()
            on new { finding.AuditJobId, RuleCode = finding.RuleCodeSnapshot }
            equals new { snapshot.AuditJobId, RuleCode = snapshot.RuleCode }
        where finding.AuditJobId == auditId
            && finding.AuditJob!.DocumentVersion!.Document!.OwnerUserId == ownerUserId
        group finding by new
        {
            snapshot.Domain,
            finding.Severity,
            finding.FixModeSnapshot
        }
        into grouped
        select new AuditFindingSummaryBucket(
            grouped.Key.Domain,
            grouped.Key.Severity,
            grouped.Key.FixModeSnapshot,
            grouped.Count());

    public static IQueryable<AuditFindingReadRow> OwnedFindings(
        PpkiDbContext db,
        Guid auditId,
        Guid ownerUserId,
        AuditFindingQuery? query = null,
        Guid? findingId = null)
    {
        var severity = query?.Severity;
        var fixMode = query?.FixMode;
        var domain = query?.Domain;
        var ruleCode = query?.RuleCode;
        var validationKey = query?.ValidationKey;
        return
        from finding in db.AuditFindings.AsNoTracking()
        join snapshot in db.AuditRuleSnapshots.AsNoTracking()
            on new { finding.AuditJobId, RuleCode = finding.RuleCodeSnapshot }
            equals new { snapshot.AuditJobId, RuleCode = snapshot.RuleCode }
        where finding.AuditJobId == auditId
            && finding.AuditJob!.DocumentVersion!.Document!.OwnerUserId == ownerUserId
            && (findingId == null || finding.Id == findingId)
            && (severity == null || finding.Severity == severity)
            && (fixMode == null || finding.FixModeSnapshot == fixMode)
            && (domain == null || snapshot.Domain == domain)
            && (ruleCode == null || finding.RuleCodeSnapshot == ruleCode)
            && (validationKey == null || snapshot.ValidationKey == validationKey)
        select new AuditFindingReadRow(
            finding.Id,
            finding.AuditJobId,
            finding.AuditJob!.DocumentVersionId,
            snapshot.Ordinal,
            finding.RuleCodeSnapshot,
            snapshot.Domain,
            snapshot.ValidationKey,
            snapshot.Element,
            finding.Severity,
            finding.FixModeSnapshot,
            finding.Status,
            finding.Message,
            finding.ActualValueJson,
            finding.ExpectedValueJson,
            finding.LocationJson,
            finding.Confidence,
            finding.SourceSectionSnapshot,
            finding.PdfPageSnapshot,
            finding.PrintedPageSnapshot);
    }

    public static IQueryable<AuditFindingReadRow> ApplyFilters(
        IQueryable<AuditFindingReadRow> values,
        AuditFindingQuery query)
    {
        if (query.Severity is not null)
            values = values.Where(value => value.Severity == query.Severity);
        if (query.FixMode is not null)
            values = values.Where(value => value.FixMode == query.FixMode);
        if (query.Domain is not null)
            values = values.Where(value => value.Domain == query.Domain);
        if (query.RuleCode is not null)
            values = values.Where(value => value.RuleCode == query.RuleCode);
        if (query.ValidationKey is not null)
            values = values.Where(value => value.ValidationKey == query.ValidationKey);
        return values;
    }

    public static IEnumerable<AuditFindingReadRow> ApplyDefaultOrdering(
        IEnumerable<AuditFindingReadRow> values) => values
            .Select(value => new OrderedFinding(value, FindingLocationSortKey.Parse(value.LocationJson)))
            .OrderBy(value => value.Finding.RuleOrdinal)
            .ThenBy(value => SeverityRank(value.Finding.Severity))
            .ThenBy(value => value.Finding.Domain, StringComparer.Ordinal)
            .ThenBy(value => value.Location.Category)
            .ThenBy(value => value.Location.BodyElementIndex ?? int.MinValue)
            .ThenBy(value => value.Location.SectionIndex ?? int.MinValue)
            .ThenBy(value => value.Location.ParagraphIndex ?? int.MinValue)
            .ThenBy(value => value.Location.RunIndex ?? int.MinValue)
            .ThenBy(value => value.Location.CompactLocation, StringComparer.Ordinal)
            .ThenBy(value => value.Finding.RuleCode, StringComparer.Ordinal)
            .ThenBy(value => value.Finding.Id)
            .Select(value => value.Finding);

    private static int SeverityRank(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => 0,
        RuleSeverity.Warning => 1,
        RuleSeverity.Info => 2,
        _ => 3
    };
}

internal sealed record OrderedFinding(
    AuditFindingReadRow Finding,
    FindingLocationSortKey Location);

internal sealed record FindingLocationSortKey(
    int Category,
    int? BodyElementIndex,
    int? SectionIndex,
    int? ParagraphIndex,
    int? RunIndex,
    string CompactLocation)
{
    public static FindingLocationSortKey Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var body = Integer(root, "BodyElementIndex", "bodyElementIndex");
        var section = Integer(root, "SectionIndex", "sectionIndex");
        var paragraph = Integer(root, "ParagraphIndex", "paragraphIndex");
        var run = Integer(root, "RunIndex", "runIndex");
        var category = body is null && section is null && paragraph is null && run is null ? 0 : 1;
        return new(category, body, section, paragraph, run,
            String(root, "CompactLocation", "compactLocation") ?? string.Empty);
    }

    private static int? Integer(JsonElement root, string canonical, string camelCase)
    {
        if (!TryProperty(root, canonical, camelCase, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static string? String(JsonElement root, string canonical, string camelCase) =>
        TryProperty(root, canonical, camelCase, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool TryProperty(
        JsonElement root,
        string canonical,
        string camelCase,
        out JsonElement value) =>
        root.TryGetProperty(canonical, out value) || root.TryGetProperty(camelCase, out value);
}

public sealed record AuditFindingReadRow(
    Guid Id,
    Guid AuditId,
    Guid DocumentVersionId,
    int RuleOrdinal,
    string RuleCode,
    string Domain,
    string ValidationKey,
    string Element,
    RuleSeverity Severity,
    FixMode FixMode,
    FindingStatus FindingState,
    string ReasonCode,
    string ActualJson,
    string ExpectedJson,
    string LocationJson,
    decimal? Confidence,
    string? SourceSection,
    int? PdfPage,
    string? PrintedPage);
