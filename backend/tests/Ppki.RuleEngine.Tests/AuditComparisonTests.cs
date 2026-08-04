using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class AuditComparisonTests
{
    private static readonly Guid SourceAuditId = Guid.Parse("41000000-0000-0000-0000-000000000001");
    private static readonly Guid ResultAuditId = Guid.Parse("41000000-0000-0000-0000-000000000002");

    [Fact]
    public void Exact_changed_source_only_and_result_only_are_classified()
    {
        var source = new[]
        {
            Finding(1, "body/1", "left"),
            Finding(2, "body/2", "left"),
            Finding(3, "body/3", "left")
        };
        var result = new[]
        {
            Finding(11, "body/1", "left", false),
            Finding(12, "body/2", "both", false),
            Finding(14, "body/4", "both", false)
        };

        var items = AuditComparisonEngine.Compare(source, result);

        Assert.Equal(4, items.Count);
        Assert.Single(items, value => value.Status == AuditComparisonStatus.StillDetected);
        Assert.Single(items, value => value.Status == AuditComparisonStatus.Changed);
        Assert.Single(items, value => value.Status == AuditComparisonStatus.NoLongerDetected);
        Assert.Single(items, value => value.Status == AuditComparisonStatus.NewlyDetected);
    }

    [Fact]
    public void Pairing_is_one_to_one_and_identical_duplicates_do_not_collapse()
    {
        var source = new[] { Finding(1, "body/1", "left"), Finding(2, "body/1", "left") };
        var result = new[]
        {
            Finding(11, "body/1", "left", false),
            Finding(12, "body/1", "left", false),
            Finding(13, "body/1", "left", false)
        };

        var items = AuditComparisonEngine.Compare(source, result);

        Assert.Equal(3, items.Count);
        Assert.Equal(2, items.Count(value => value.Status == AuditComparisonStatus.StillDetected));
        Assert.Single(items, value => value.Status == AuditComparisonStatus.NewlyDetected);
        Assert.Equal(items.Count, items.Select(value => value.After?.Id).Where(value => value is not null).Distinct().Count());
    }

    [Fact]
    public void Duplicate_count_difference_retains_the_unpaired_source()
    {
        var items = AuditComparisonEngine.Compare(
            [Finding(1, "body/1", "left"), Finding(2, "body/1", "left")],
            [Finding(11, "body/1", "left", false)]);

        Assert.Single(items, value => value.Status == AuditComparisonStatus.StillDetected);
        Assert.Single(items, value => value.Status == AuditComparisonStatus.NoLongerDetected);
    }

    [Fact]
    public void Multiple_changed_findings_pair_deterministically_without_severity_as_winner()
    {
        var source = new[]
        {
            Finding(1, "body/1", "a", severity: RuleSeverity.Error),
            Finding(2, "body/1", "b", severity: RuleSeverity.Info)
        };
        var result = new[]
        {
            Finding(12, "body/1", "d", false, RuleSeverity.Warning),
            Finding(11, "body/1", "c", false, RuleSeverity.Error)
        };

        var first = AuditComparisonEngine.Compare(source, result);
        var second = AuditComparisonEngine.Compare(source.Reverse(), result.Reverse());

        Assert.All(first, value => Assert.Equal(AuditComparisonStatus.Changed, value.Status));
        Assert.Equal(Semantics(first), Semantics(second));
    }

    [Fact]
    public void Random_ids_do_not_change_aggregate_classification()
    {
        var first = AuditComparisonEngine.Compare(
            [Finding(1, "body/1", "a"), Finding(2, "body/1", "b")],
            [Finding(11, "body/1", "a", false), Finding(12, "body/1", "c", false)]);
        var second = AuditComparisonEngine.Compare(
            [Finding(Guid.NewGuid(), "body/1", "a"), Finding(Guid.NewGuid(), "body/1", "b")],
            [Finding(Guid.NewGuid(), "body/1", "a", false), Finding(Guid.NewGuid(), "body/1", "c", false)]);

        Assert.Equal(StatusCounts(first), StatusCounts(second));
    }

    [Fact]
    public void Comparison_is_invariant_to_input_order_and_current_culture()
    {
        var source = new[] { Finding(1, "body/2", "a"), Finding(2, "body/1", "b") };
        var result = new[] { Finding(11, "body/1", "c", false), Finding(12, "body/2", "a", false) };
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("id-ID");
            var first = Semantics(AuditComparisonEngine.Compare(source, result));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = Semantics(AuditComparisonEngine.Compare(source.Reverse(), result.Reverse()));
            Assert.Equal(first, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Canonical_fingerprint_sorts_object_properties_but_preserves_types_null_and_empty()
    {
        Assert.Equal(CanonicalJsonFingerprint.Create("{\"b\":[1,2],\"a\":true}"),
            CanonicalJsonFingerprint.Create("{\"a\":true,\"b\":[1,2]}"));
        Assert.NotEqual(CanonicalJsonFingerprint.Create("{\"a\":null}"),
            CanonicalJsonFingerprint.Create("{\"a\":\"\"}"));
        Assert.NotEqual(CanonicalJsonFingerprint.Create("{\"a\":1}"),
            CanonicalJsonFingerprint.Create("{\"a\":\"1\"}"));
        Assert.NotEqual(CanonicalJsonFingerprint.Create("{\"a\":[1,2]}"),
            CanonicalJsonFingerprint.Create("{\"a\":[2,1]}"));
    }

    [Fact]
    public void Finding_without_stable_location_is_conservatively_unpaired()
    {
        var source = Finding(1, null, "left");
        var result = Finding(11, null, "left", false);

        var items = AuditComparisonEngine.Compare([source], [result]);

        Assert.Contains(items, value => value.Status == AuditComparisonStatus.NoLongerDetected);
        Assert.Contains(items, value => value.Status == AuditComparisonStatus.NewlyDetected);
        Assert.DoesNotContain(items, value => value.Status is AuditComparisonStatus.StillDetected or AuditComparisonStatus.Changed);
    }

    [Fact]
    public void Summary_is_global_and_score_delta_requires_two_numeric_scores()
    {
        var items = AuditComparisonEngine.Compare(
            [Finding(1, "body/1", "a"), Finding(2, "body/2", "b")],
            [Finding(11, "body/1", "a", false), Finding(12, "body/2", "c", false)]);
        var unavailable = new AuditComparisonScoreDto("NotConfigured", null, null, "audit-score-policy-not-configured");
        var summary = AuditComparisonEngine.Summary(items, 2, 2, unavailable, unavailable);

        Assert.Equal(2, summary.SourceFindingCount);
        Assert.Equal(2, summary.ResultFindingCount);
        Assert.Equal(1, summary.StillDetectedCount);
        Assert.Equal(1, summary.ChangedCount);
        Assert.Null(summary.ScoreDelta);
        Assert.Equal(2, summary.Severities.Single().TotalCount);
        Assert.Equal(2, summary.Domains.Single().TotalCount);
    }

    [Fact]
    public void Filters_are_exact_and_do_not_mutate_global_summary()
    {
        var items = AuditComparisonEngine.Compare(
            [Finding(1, "body/1", "a"), Finding(2, "body/2", "b", domain: "Typography")],
            [Finding(11, "body/1", "a", false), Finding(12, "body/2", "c", false, domain: "Typography")]);
        Assert.True(AuditComparisonQuery.TryCreate("Changed", "Error", "Typography", "PPKI-LAY-019",
            null, 1, 1, out var query, out _));
        var summary = AuditComparisonEngine.Summary(items, 2, 2, Score(), Score());

        var filtered = AuditComparisonEngine.ApplyFilters(items, query).ToArray();

        Assert.Single(filtered);
        Assert.Equal(AuditComparisonStatus.Changed, filtered[0].Status);
        Assert.Equal(2, summary.SourceFindingCount);
        Assert.Equal(2, summary.ResultFindingCount);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(10001, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(10000, 100)]
    public void Invalid_pagination_is_rejected_safely(int page, int pageSize)
    {
        Assert.False(AuditComparisonQuery.TryCreate(null, null, null, null, null,
            page, pageSize, out _, out var error));
        Assert.Equal("audit-comparison-pagination-invalid", error);
    }

    [Fact]
    public void Page_size_one_and_one_hundred_are_accepted_with_existing_default()
    {
        Assert.True(AuditComparisonQuery.TryCreate(null, null, null, null, null,
            null, null, out var defaults, out _));
        Assert.Equal(25, defaults.PageSize);
        Assert.True(AuditComparisonQuery.TryCreate(null, null, null, null, null,
            1, 1, out _, out _));
        Assert.True(AuditComparisonQuery.TryCreate(null, null, null, null, null,
            1, 100, out _, out _));
    }

    [Theory]
    [InlineData(FixExecutionState.Queued)]
    [InlineData(FixExecutionState.Processing)]
    [InlineData(FixExecutionState.Failed)]
    [InlineData(FixExecutionState.NoChange)]
    public void Non_completed_execution_is_not_ready(FixExecutionState state)
    {
        AssertCode("audit-comparison-execution-not-completed",
            () => AuditComparisonService.ValidateSource(SourceContext() with { ExecutionState = state }));
    }

    [Theory]
    [InlineData(AuditJobStatus.Queued)]
    [InlineData(AuditJobStatus.Processing)]
    [InlineData(AuditJobStatus.Failed)]
    public void Non_completed_source_audit_is_not_ready(AuditJobStatus status)
    {
        AssertCode("audit-comparison-source-audit-not-completed",
            () => AuditComparisonService.ValidateSource(SourceContext() with { SourceAuditStatus = status }));
    }

    [Fact]
    public void Missing_result_version_and_source_lineage_mismatch_are_rejected()
    {
        AssertCode("audit-comparison-result-version-missing", () => AuditComparisonService.ValidateSource(
            SourceContext() with { ResultDocumentVersionId = null, ResultDocumentId = null }));
        AssertCode("audit-comparison-source-lineage-invalid", () => AuditComparisonService.ValidateSource(
            SourceContext() with { SourceAuditDocumentVersionId = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData(AuditJobStatus.Queued)]
    [InlineData(AuditJobStatus.Processing)]
    [InlineData(AuditJobStatus.Failed)]
    public void Non_completed_result_audit_is_not_ready(AuditJobStatus status)
    {
        AssertCode("audit-comparison-result-audit-not-completed", () => AuditComparisonService.ValidateResult(
            SourceContext(), ResultContext() with { ResultAuditStatus = status }));
    }

    [Fact]
    public void Result_lineage_and_historical_context_mismatches_are_rejected()
    {
        var source = SourceContext();
        AssertCode("audit-comparison-result-lineage-invalid", () => AuditComparisonService.ValidateResult(
            source, ResultContext() with { SourceAuditId = Guid.NewGuid() }));
        AssertCode("audit-comparison-result-lineage-invalid", () => AuditComparisonService.ValidateResult(
            source, ResultContext() with { ResultDocumentVersionId = Guid.NewGuid() }));
        AssertCode("audit-comparison-historical-context-mismatch", () => AuditComparisonService.ValidateResult(
            source, ResultContext() with { ResultRuleSetHash = new string('f', 64) }));
    }

    [Fact]
    public void Historical_snapshot_comparison_requires_every_field_to_match()
    {
        var source = Snapshot();
        var same = Snapshot();
        var changed = Snapshot();
        changed.ValidationJson = "{\"changed\":true}";

        Assert.True(AuditComparisonService.HistoricalSnapshotsEqual([source], [same]));
        Assert.False(AuditComparisonService.HistoricalSnapshotsEqual([source], [changed]));
        Assert.False(AuditComparisonService.HistoricalSnapshotsEqual([source], [same, same]));
    }

    [Fact]
    public void Comparison_inputs_are_not_mutated()
    {
        var source = Finding(1, "body/1", "left");
        var result = Finding(11, "body/1", "both", false);

        _ = AuditComparisonEngine.Compare([source], [result]);

        Assert.Equal("left", source.ActualJson.Contains("left", StringComparison.Ordinal) ? "left" : null);
        Assert.Equal("both", result.ActualJson.Contains("both", StringComparison.Ordinal) ? "both" : null);
    }

    [Fact]
    public void Public_contract_contains_only_allowlisted_summaries()
    {
        var types = new[]
        {
            typeof(AuditComparisonDto), typeof(AuditComparisonItemDto),
            typeof(AuditComparisonFindingDto), typeof(AuditComparisonActualSummary),
            typeof(AuditComparisonExpectedSummary), typeof(AuditComparisonLocationSummary)
        };
        var properties = types.SelectMany(value => value.GetProperties(BindingFlags.Public | BindingFlags.Instance)).ToArray();
        var forbidden = new[] { "Json", "Fingerprint", "SemanticKey", "DocumentText", "Filename", "StoragePath", "SignedUrl", "RawXml", "RuleConfiguration" };

        Assert.DoesNotContain(properties, property => forbidden.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(properties, property => property.PropertyType.FullName?.Contains("JsonElement", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Service_and_endpoint_are_owned_historical_read_only_adapters()
    {
        var service = Source("backend", "src", "Ppki.Infrastructure", "AuditComparisonService.cs");
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");

        Assert.Contains("OwnerUserId == ownerUserId", service, StringComparison.Ordinal);
        Assert.Contains("AsNoTracking()", service, StringComparison.Ordinal);
        Assert.Contains("AuditRuleSnapshots", service, StringComparison.Ordinal);
        Assert.Contains("policy: null", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleDefinitions", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileRules", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentTypes", service, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentVersion", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileStorage", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IDocxParser", service, StringComparison.Ordinal);
        Assert.Contains("audit-comparison-result-audit-missing", service, StringComparison.Ordinal);
        Assert.Contains("/fix-executions/{executionId}/comparison", api, StringComparison.Ordinal);
        Assert.Contains("IAuditComparisonService comparison", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Ownership_is_in_both_database_queries_before_materialization()
    {
        using var db = new PpkiDbContext(new DbContextOptionsBuilder<PpkiDbContext>()
            .UseNpgsql("Host=localhost;Database=audit_comparison_offline_test").Options);

        var sourceSql = AuditComparisonService.OwnedExecution(db, Guid.NewGuid(), Guid.NewGuid())
            .ToQueryString().ToLowerInvariant();
        var resultSql = AuditComparisonService.OwnedResultAudit(db, Guid.NewGuid(), Guid.NewGuid())
            .ToQueryString().ToLowerInvariant();

        Assert.Contains("owner_user_id", sourceSql, StringComparison.Ordinal);
        Assert.Contains("owner_user_id", resultSql, StringComparison.Ordinal);
        Assert.Contains("fix_execution_jobs", sourceSql, StringComparison.Ordinal);
        Assert.Contains("source_fix_execution_id", resultSql, StringComparison.Ordinal);
    }

    private static AuditComparisonFindingSnapshot Finding(
        int idSuffix, string? location, string actual, bool before = true,
        RuleSeverity severity = RuleSeverity.Error, string domain = "Layout") =>
        Finding(Guid.Parse($"41000000-0000-0000-0000-{idSuffix:000000000000}"), location,
            actual, before, severity, domain);

    private static AuditComparisonFindingSnapshot Finding(
        Guid id, string? location, string actual, bool before = true,
        RuleSeverity severity = RuleSeverity.Error, string domain = "Layout") => new(
            id, before ? SourceAuditId : ResultAuditId, 1, "PPKI-LAY-019", domain,
            "body.justified", "paragraph", severity, FixMode.Auto,
            FindingStatus.Open, "paragraph-alignment-invalid",
            $"{{\"Property\":\"alignment\",\"NormalizedValue\":\"{actual}\",\"Unit\":null,\"ResolutionState\":\"Resolved\",\"SourceKind\":\"DirectFormatting\",\"Inherited\":false,\"RawValue\":\"private\"}}",
            "{\"ValidationKey\":\"body.justified\",\"AcceptedValues\":[\"both\"],\"Property\":\"alignment\"}",
            location is null ? "{}" : $"{{\"ParagraphIndex\":1,\"CompactLocation\":\"{location}\"}}",
            1m, "body", null, null);

    private static string[] Semantics(IEnumerable<AuditComparisonItemDto> values) => values.Select(value =>
        $"{value.Status}|{value.Location.CompactLocation}|{value.Before?.Actual.NormalizedValue}|{value.After?.Actual.NormalizedValue}").ToArray();

    private static int[] StatusCounts(IEnumerable<AuditComparisonItemDto> values) =>
        Enum.GetValues<AuditComparisonStatus>().Select(status => values.Count(value => value.Status == status)).ToArray();

    private static AuditComparisonScoreDto Score() => new("NotConfigured", null, null, "audit-score-policy-not-configured");

    private static AuditComparisonSourceContext SourceContext() => new(
        Guid.Parse("41000000-0000-0000-0000-000000000010"), FixExecutionState.Completed,
        SourceAuditId, AuditJobStatus.Completed,
        Guid.Parse("41000000-0000-0000-0000-000000000020"),
        Guid.Parse("41000000-0000-0000-0000-000000000020"),
        Guid.Parse("41000000-0000-0000-0000-000000000030"),
        Guid.Parse("41000000-0000-0000-0000-000000000021"),
        Guid.Parse("41000000-0000-0000-0000-000000000030"),
        Guid.Parse("41000000-0000-0000-0000-000000000040"),
        DocumentKind.Skripsi, new string('a', 64), 1, null);

    private static AuditComparisonResultContext ResultContext() => new(
        ResultAuditId, AuditJobStatus.Completed, SourceAuditId,
        Guid.Parse("41000000-0000-0000-0000-000000000010"),
        Guid.Parse("41000000-0000-0000-0000-000000000021"),
        Guid.Parse("41000000-0000-0000-0000-000000000040"),
        DocumentKind.Skripsi, new string('a', 64), 1, null);

    private static AuditRuleSnapshot Snapshot() => new()
    {
        Id = Guid.Parse("41000000-0000-0000-0000-000000000050"),
        AuditJobId = SourceAuditId,
        RuleId = Guid.Parse("41000000-0000-0000-0000-000000000051"),
        RuleCode = "PPKI-LAY-019",
        Domain = "Layout",
        Subdomain = "Paragraph",
        AppliesTo = "Thesis",
        Element = "paragraph",
        RequirementJson = "{}",
        ValidationKey = "body.justified",
        ValidationJson = "{}",
        Severity = RuleSeverity.Error,
        FixMode = FixMode.Auto,
        SourceReferenceJson = "{}",
        Layer = "base",
        Precedence = 1,
        Ordinal = 1,
        SnapshotSchemaVersion = 1
    };

    private static void AssertCode(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<AuditComparisonException>(action).DiagnosticCode);

    private static string Source(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. segments]));

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "package.json"))) return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
