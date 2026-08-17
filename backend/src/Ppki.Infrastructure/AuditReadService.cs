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
            .Where(value => value.Id == auditId)
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
        var automatic = await db.AutomaticRemediationOrchestrations.AsNoTracking()
            .Where(value => value.SourceAuditJobId == auditId
                && value.OrchestrationType == AutomaticRemediationPolicy.OrchestrationType
                && value.PolicyVersion == AutomaticRemediationPolicy.Version)
            .Select(value => new
            {
                value.State, value.PolicyVersion, value.EligibleFindingCount,
                value.OperationCount, value.FixExecutionId, value.SafeFailureCode
            }).SingleOrDefaultAsync(cancellationToken);
        AutomaticRemediationSummaryDto? automaticSummary = null;
        if (automatic is not null)
        {
            var resolved = automatic.FixExecutionId is null ? 0 : await db.FindingResolutionEvents.AsNoTracking()
                .CountAsync(value => value.SourceFixExecutionId == automatic.FixExecutionId
                    && value.EventType == FindingResolutionEventType.VerificationResolvedObserved, cancellationToken);
            var stillDetected = automatic.FixExecutionId is null ? 0 : await db.FindingResolutionEvents.AsNoTracking()
                .CountAsync(value => value.SourceFixExecutionId == automatic.FixExecutionId
                    && value.EventType == FindingResolutionEventType.VerificationStillDetectedObserved, cancellationToken);
            automaticSummary = new(automatic.State.ToString(), automatic.PolicyVersion,
                automatic.EligibleFindingCount, automatic.OperationCount, resolved,
                stillDetected, automatic.SafeFailureCode);
        }
        var render = await RenderStateAsync(db, audit.DocumentVersionId, cancellationToken);

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
            failure?.Message,
            automaticSummary,
            render.Dto);
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
            value.Id == auditId,
            cancellationToken);
        if (!owned) return null;

        var filtered = AuditReadQueries.DatabaseFindings(db, auditId, query);
        var totalCount = await filtered.CountAsync(cancellationToken);
        if (totalCount > AuditFindingQuery.MaximumFindingCount)
            throw new InvalidOperationException("Persisted finding count exceeds the supported limit.");

        var offset = (query.Page - 1) * query.PageSize;
        var rows = await AuditReadQueries.ApplyDatabaseOrdering(filtered)
            .Skip(offset)
            .Take(query.PageSize)
            .Select(value => new AuditFindingReadRow(
                value.Id,
                value.AuditId,
                value.DocumentVersionId,
                value.RuleOrdinal,
                value.RuleCode,
                value.Domain,
                value.ValidationKey,
                value.Element,
                value.Severity,
                value.FixMode,
                value.FindingState,
                value.ReasonCode,
                value.ActualJson,
                value.ExpectedJson,
                value.LocationJson,
                value.Confidence,
                value.SourceSection,
                value.PdfPage,
                value.PrintedPage,
                value.SnapshotSchemaVersion))
            .ToListAsync(cancellationToken);
        var automaticFindingIds = await AutomaticFindingIdsAsync(db, auditId, cancellationToken);
        var render = await RenderStateAsync(db,
            rows.Select(value => value.DocumentVersionId).FirstOrDefault(), cancellationToken);
        var pageLocations = await PageLocationsAsync(db, render, rows, cancellationToken);

        return new(query.Page, query.PageSize, totalCount,
            rows.Select(row => ToListItem(row, automaticFindingIds,
                pageLocations.GetValueOrDefault(row.Id, RenderFallback(render)))).ToArray());
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
        var automaticFindingIds = await AutomaticFindingIdsAsync(db, auditId, cancellationToken);
        var render = await RenderStateAsync(db, row.DocumentVersionId, cancellationToken);
        var detailRows = new[] { new AuditFindingReadRow(row.Id, row.AuditId, row.DocumentVersionId,
            row.RuleOrdinal, row.RuleCode, row.Domain, row.ValidationKey, row.Element, row.Severity,
            row.FixMode, row.FindingState, row.ReasonCode, row.ActualJson, row.ExpectedJson,
            row.LocationJson, row.Confidence, row.SourceSection, row.PdfPage, row.PrintedPage,
            row.SnapshotSchemaVersion) };
        var pageLocations = await PageLocationsAsync(db, render, detailRows, cancellationToken);

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
            automaticFindingIds.Contains(row.Id) ? "Automatic" : "None",
            pageLocations.GetValueOrDefault(row.Id, RenderFallback(render)));
    }

    private static AuditFindingListItemDto ToListItem(
        AuditFindingReadRow row,
        IReadOnlySet<Guid> automaticFindingIds,
        FindingPageLocationDto pageLocation) => new(
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
        automaticFindingIds.Contains(row.Id) ? "Automatic" : "None",
        pageLocation);

    private static async Task<RenderReadState> RenderStateAsync(
        PpkiDbContext db, Guid documentVersionId, CancellationToken cancellationToken)
    {
        if (documentVersionId == Guid.Empty)
            return new(null, null, new("Pending", null, CanonicalDocumentRenderContract.RendererVersion,
                CanonicalDocumentRenderContract.RendererContractVersion, CanonicalDocumentRenderContract.FontProfileVersion,
                CanonicalDocumentRenderContract.PageMapSchemaVersion, null, false));
        var row = await db.DocumentRenderJobs.AsNoTracking()
            .Where(value => value.DocumentVersionId == documentVersionId
                && value.RendererId == CanonicalDocumentRenderContract.RendererId
                && value.RendererVersion == CanonicalDocumentRenderContract.RendererVersion
                && value.RendererContractVersion == CanonicalDocumentRenderContract.RendererContractVersion
                && value.FontProfileVersion == CanonicalDocumentRenderContract.FontProfileVersion)
            .Select(value => new { value.State, value.SafeFailureCode,
                ArtifactId = value.Artifact == null ? (Guid?)null : value.Artifact.Id,
                PageCount = value.Artifact == null ? (int?)null : value.Artifact.PageCount })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            return new(null, null, new("Pending", null, CanonicalDocumentRenderContract.RendererVersion,
                CanonicalDocumentRenderContract.RendererContractVersion, CanonicalDocumentRenderContract.FontProfileVersion,
                CanonicalDocumentRenderContract.PageMapSchemaVersion, null, false));
        return new(row.ArtifactId, row.State, new(row.State.ToString(), row.PageCount,
            CanonicalDocumentRenderContract.RendererVersion, CanonicalDocumentRenderContract.RendererContractVersion,
            CanonicalDocumentRenderContract.FontProfileVersion, CanonicalDocumentRenderContract.PageMapSchemaVersion,
            row.SafeFailureCode, row.State == DocumentRenderState.Completed && row.ArtifactId is not null));
    }

    private static async Task<IReadOnlyDictionary<Guid, FindingPageLocationDto>> PageLocationsAsync(
        PpkiDbContext db,
        RenderReadState render,
        IReadOnlyList<AuditFindingReadRow> findings,
        CancellationToken cancellationToken)
    {
        if (render.ArtifactId is null || render.State != DocumentRenderState.Completed || findings.Count == 0)
            return new Dictionary<Guid, FindingPageLocationDto>();
        var requested = findings.Select(value => (value.Id, Location: StructuralLocation(value.LocationJson))).ToArray();
        var paragraphIndexes = requested.Where(value => value.Location.ParagraphIndex is not null)
            .Select(value => value.Location.ParagraphIndex!.Value).Distinct().ToArray();
        if (paragraphIndexes.Length == 0) return new Dictionary<Guid, FindingPageLocationDto>();
        var entries = await db.DocumentPageMapEntries.AsNoTracking()
            .Where(value => value.RenderArtifactId == render.ArtifactId
                && value.ParagraphIndex != null && paragraphIndexes.Contains(value.ParagraphIndex.Value))
            .Select(value => new { value.ParagraphIndex, value.RunIndex, value.PageNumber, value.Confidence })
            .ToListAsync(cancellationToken);
        var result = new Dictionary<Guid, FindingPageLocationDto>();
        foreach (var item in requested)
        {
            if (item.Location.ParagraphIndex is null) continue;
            var entry = entries.SingleOrDefault(value => value.ParagraphIndex == item.Location.ParagraphIndex
                && value.RunIndex == item.Location.RunIndex);
            if (entry is not null)
                result[item.Id] = new(entry.PageNumber, entry.Confidence.ToString(), "Completed");
        }
        return result;
    }

    private static (int? ParagraphIndex, int? RunIndex) StructuralLocation(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return (Integer(document.RootElement, "ParagraphIndex", "paragraphIndex"),
                Integer(document.RootElement, "RunIndex", "runIndex"));
        }
        catch (JsonException) { return (null, null); }
    }

    private static int? Integer(JsonElement root, string first, string second)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if ((root.TryGetProperty(first, out var value) || root.TryGetProperty(second, out value))
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) && parsed >= 0)
            return parsed;
        return null;
    }

    private static FindingPageLocationDto RenderFallback(RenderReadState render) => render.State switch
    {
        DocumentRenderState.Failed => new(null, "Unavailable", "Failed"),
        DocumentRenderState.Completed => new(null, "Unavailable", "Completed"),
        _ => new(null, "Unavailable", "Pending")
    };

    private static async Task<IReadOnlySet<Guid>> AutomaticFindingIdsAsync(
        PpkiDbContext db,
        Guid auditId,
        CancellationToken cancellationToken)
    {
        var selectedFindingIdsJson = await db.AutomaticRemediationOrchestrations.AsNoTracking()
            .Where(value => value.SourceAuditJobId == auditId
                && value.OrchestrationType == AutomaticRemediationPolicy.OrchestrationType
                && value.PolicyVersion == AutomaticRemediationPolicy.Version
                && value.FixExecutionId != null)
            .Select(value => value.FixExecution!.SelectedFindingIdsJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (selectedFindingIdsJson is null) return new HashSet<Guid>();

        return JsonSerializer.Deserialize<Guid[]>(selectedFindingIdsJson)?.ToHashSet()
            ?? new HashSet<Guid>();
    }

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

    private sealed record RenderReadState(
        Guid? ArtifactId,
        DocumentRenderState? State,
        DocumentRenderStateDto Dto);

}

public static class AuditReadQueries
{
    private const string DatabaseFindingSql = """
        select
            finding.id as "Id",
            finding.audit_job_id as "AuditId",
            audit.document_version_id as "DocumentVersionId",
            snapshot.ordinal as "RuleOrdinal",
            finding.rule_code_snapshot as "RuleCode",
            snapshot.domain as "Domain",
            snapshot.validation_key as "ValidationKey",
            snapshot.element as "Element",
            snapshot.snapshot_schema_version as "SnapshotSchemaVersion",
            case finding.severity when 'Error' then 0 when 'Warning' then 1 when 'Info' then 2 else 3 end as "Severity",
            case finding.fix_mode_snapshot when 'Auto' then 0 when 'Confirm' then 1 when 'Manual' then 2 when 'Report' then 3 else 4 end as "FixMode",
            case finding.status when 'Open' then 0 when 'Fixed' then 1 when 'Ignored' then 2 when 'ManualReview' then 3 else 4 end as "FindingState",
            finding.message as "ReasonCode",
            finding.actual_value::text as "ActualJson",
            finding.expected_value::text as "ExpectedJson",
            finding.location::text as "LocationJson",
            finding.confidence as "Confidence",
            finding.source_section_snapshot as "SourceSection",
            finding.pdf_page_snapshot as "PdfPage",
            finding.printed_page_snapshot as "PrintedPage",
            case when location_sort.body is null
                   and location_sort.section is null
                   and location_sort.paragraph is null
                   and location_sort.run is null
                 then 0 else 1 end as "LocationCategory",
            location_sort.body as "BodyElementIndex",
            location_sort.section as "SectionIndex",
            location_sort.paragraph as "ParagraphIndex",
            location_sort.run as "RunIndex",
            case
                when jsonb_typeof(coalesce(finding.location -> 'CompactLocation', finding.location -> 'compactLocation')) = 'string'
                then coalesce(finding.location ->> 'CompactLocation', finding.location ->> 'compactLocation')
                else ''
            end as "CompactLocation"
        from public.audit_findings as finding
        join public.audit_jobs as audit on audit.id = finding.audit_job_id
        join public.audit_rule_snapshots as snapshot
          on snapshot.audit_job_id = finding.audit_job_id
         and snapshot.rule_code = finding.rule_code_snapshot
        cross join lateral (
            select
                coalesce(finding.location -> 'BodyElementIndex', finding.location -> 'bodyElementIndex') as body,
                coalesce(finding.location -> 'SectionIndex', finding.location -> 'sectionIndex') as section,
                coalesce(finding.location -> 'ParagraphIndex', finding.location -> 'paragraphIndex') as paragraph,
                coalesce(finding.location -> 'RunIndex', finding.location -> 'runIndex') as run
        ) as location_value
        cross join lateral (
            select
                case when jsonb_typeof(location_value.body) = 'number'
                           and (location_value.body #>> '{{}}') ~ '^-?[0-9]+$'
                           and length(ltrim(location_value.body #>> '{{}}', '-')) <= 10
                      then case when (location_value.body #>> '{{}}')::bigint between -2147483648 and 2147483647
                                then (location_value.body #>> '{{}}')::integer end end as body,
                case when jsonb_typeof(location_value.section) = 'number'
                           and (location_value.section #>> '{{}}') ~ '^-?[0-9]+$'
                           and length(ltrim(location_value.section #>> '{{}}', '-')) <= 10
                      then case when (location_value.section #>> '{{}}')::bigint between -2147483648 and 2147483647
                                then (location_value.section #>> '{{}}')::integer end end as section,
                case when jsonb_typeof(location_value.paragraph) = 'number'
                           and (location_value.paragraph #>> '{{}}') ~ '^-?[0-9]+$'
                           and length(ltrim(location_value.paragraph #>> '{{}}', '-')) <= 10
                      then case when (location_value.paragraph #>> '{{}}')::bigint between -2147483648 and 2147483647
                                then (location_value.paragraph #>> '{{}}')::integer end end as paragraph,
                case when jsonb_typeof(location_value.run) = 'number'
                           and (location_value.run #>> '{{}}') ~ '^-?[0-9]+$'
                           and length(ltrim(location_value.run #>> '{{}}', '-')) <= 10
                      then case when (location_value.run #>> '{{}}')::bigint between -2147483648 and 2147483647
                                then (location_value.run #>> '{{}}')::integer end end as run
        ) as location_sort
        """;

    public static IQueryable<AuditFindingDatabaseRow> DatabaseFindings(
        PpkiDbContext db,
        Guid auditId,
        AuditFindingQuery query)
    {
        var values = db.Database.SqlQueryRaw<AuditFindingDatabaseRow>(DatabaseFindingSql)
            .Where(value => value.AuditId == auditId);
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

    public static IOrderedQueryable<AuditFindingDatabaseRow> ApplyDatabaseOrdering(
        IQueryable<AuditFindingDatabaseRow> values) => values
            .OrderBy(value => value.RuleOrdinal)
            .ThenBy(value => value.Severity)
            .ThenBy(value => EF.Functions.Collate(value.Domain, "C"))
            .ThenBy(value => value.LocationCategory)
            .ThenBy(value => value.BodyElementIndex ?? int.MinValue)
            .ThenBy(value => value.SectionIndex ?? int.MinValue)
            .ThenBy(value => value.ParagraphIndex ?? int.MinValue)
            .ThenBy(value => value.RunIndex ?? int.MinValue)
            .ThenBy(value => EF.Functions.Collate(value.CompactLocation, "C"))
            .ThenBy(value => EF.Functions.Collate(value.RuleCode, "C"))
            .ThenBy(value => value.Id);

    public static IQueryable<AuditFindingSummaryBucket> OwnedSummaryBuckets(
        PpkiDbContext db,
        Guid auditId,
        Guid ownerUserId) =>
        from finding in db.AuditFindings.AsNoTracking()
        join snapshot in db.AuditRuleSnapshots.AsNoTracking()
            on new { finding.AuditJobId, RuleCode = finding.RuleCodeSnapshot }
            equals new { snapshot.AuditJobId, RuleCode = snapshot.RuleCode }
        where finding.AuditJobId == auditId
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
            finding.PrintedPageSnapshot,
            snapshot.SnapshotSchemaVersion);
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
    string? PrintedPage,
    int SnapshotSchemaVersion = 1);

public sealed class AuditFindingDatabaseRow
{
    public Guid Id { get; init; }
    public Guid AuditId { get; init; }
    public Guid DocumentVersionId { get; init; }
    public int RuleOrdinal { get; init; }
    public string RuleCode { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string ValidationKey { get; init; } = string.Empty;
    public string Element { get; init; } = string.Empty;
    public int SnapshotSchemaVersion { get; init; }
    public RuleSeverity Severity { get; init; }
    public FixMode FixMode { get; init; }
    public FindingStatus FindingState { get; init; }
    public string ReasonCode { get; init; } = string.Empty;
    public string ActualJson { get; init; } = string.Empty;
    public string ExpectedJson { get; init; } = string.Empty;
    public string LocationJson { get; init; } = string.Empty;
    public decimal? Confidence { get; init; }
    public string? SourceSection { get; init; }
    public int? PdfPage { get; init; }
    public string? PrintedPage { get; init; }
    public int LocationCategory { get; init; }
    public int? BodyElementIndex { get; init; }
    public int? SectionIndex { get; init; }
    public int? ParagraphIndex { get; init; }
    public int? RunIndex { get; init; }
    public string CompactLocation { get; init; } = string.Empty;
}
