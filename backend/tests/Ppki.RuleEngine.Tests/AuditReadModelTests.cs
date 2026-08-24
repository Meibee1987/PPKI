using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class AuditReadModelTests
{
    [Fact]
    public void Completed_summary_wire_contract_uses_string_score_state_with_null_score()
    {
        var summary = new AuditSummaryDto(
            Guid.NewGuid(), "Completed", Guid.NewGuid(), Guid.NewGuid(), "Skripsi",
            new string('a', 64), 1, 1, 2_228, 2_228, 2_228, 0, 0,
            new(2_228, 0, 0), [new("LAY", 2_228)], new(0, 0, 0, 2_228),
            AuditScoreState.NotConfigured, null, null, null,
            "scoring-policy-not-configured", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, null, null,
            new AuditFindingDispositionSummaryDto(0, 0, 0, 2_228),
            null,
            new CorrectionAnalysisReadinessDto("Completed"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("scoreState").ValueKind);
        Assert.Equal("NotConfigured", json.RootElement.GetProperty("scoreState").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("score").ValueKind);
        Assert.Equal("Completed", json.RootElement.GetProperty("correctionAnalysis")
            .GetProperty("state").GetString());
    }

    [Fact]
    public void Summary_projects_profile_version_and_authoritative_readiness_fields()
    {
        var summary = new AuditSummaryDto(
            Guid.NewGuid(), "Completed", Guid.NewGuid(), Guid.NewGuid(), "Skripsi",
            new string('a', 64), 1, 1, 1, 1, 1, 0, 0,
            new(1, 0, 0), [new("LAY", 1)], new(1, 0, 0, 0),
            AuditScoreState.NotConfigured, null, null, null, "scoring-policy-not-configured",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null,
            new(0, 0, 0, 1), null, new("Completed"),
            ProfileVersionNo: 4, BlockingFindingCount: 1,
            ReadinessState: "NeedsFix", ReadinessPolicyVersion: ReviewReadinessPolicy.Version);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(4, json.RootElement.GetProperty("profileVersionNo").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("blockingFindingCount").GetInt32());
        Assert.Equal("NeedsFix", json.RootElement.GetProperty("readinessState").GetString());
        Assert.Equal(ReviewReadinessPolicy.Version,
            json.RootElement.GetProperty("readinessPolicyVersion").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("errorCount").GetInt32());
    }

    [Fact]
    public void Summary_counts_are_consistent_across_severity_domain_and_fix_mode()
    {
        var result = AuditSummaryCounts.FromBuckets([
            new("Layout", RuleSeverity.Error, FixMode.Manual, 2),
            new("Layout", RuleSeverity.Warning, FixMode.Report, 3),
            new("Structure", RuleSeverity.Info, FixMode.Confirm, 4),
            new("Structure", RuleSeverity.Error, FixMode.Auto, 1)
        ]);

        Assert.Equal(10, result.FindingCount);
        Assert.Equal(new AuditSeveritySummary(3, 3, 4), result.Severity);
        Assert.Equal([
            new AuditDomainSummary("Layout", 5),
            new AuditDomainSummary("Structure", 5)
        ], result.Domains);
        Assert.Equal(new AuditFixModeSummary(1, 4, 2, 3), result.FixModes);
    }

    [Fact]
    public void Empty_audit_has_a_valid_zero_summary()
    {
        var result = AuditSummaryCounts.FromBuckets([]);

        Assert.Equal(0, result.FindingCount);
        Assert.Equal(new AuditSeveritySummary(0, 0, 0), result.Severity);
        Assert.Empty(result.Domains);
        Assert.Equal(new AuditFixModeSummary(0, 0, 0, 0), result.FixModes);
    }

    [Fact]
    public void Failed_audit_exposes_only_a_stable_safe_failure()
    {
        var failed = AuditFailureSummary.FromStatus(AuditJobStatus.Failed);

        Assert.Equal("audit-processing-failed", failed!.Code);
        Assert.Equal("Audit processing failed.", failed.Message);
        Assert.Null(AuditFailureSummary.FromStatus(AuditJobStatus.Completed));
    }

    [Fact]
    public void Findings_query_has_bounded_defaults_and_exact_filters()
    {
        Assert.True(AuditFindingQuery.TryCreate(
            "warning", "manual", "requiresreview", true, " Layout ", " RULE-A ", " page.size ",
            " heading ", "default", null, null, out var query, out var error));

        Assert.Null(error);
        Assert.Equal(RuleSeverity.Warning, query.Severity);
        Assert.Equal(FixMode.Manual, query.FixMode);
        Assert.Equal(AuditFindingDisposition.RequiresReview, query.Disposition);
        Assert.True(query.AutomaticallyResolved);
        Assert.Equal("Layout", query.Domain);
        Assert.Equal("RULE-A", query.RuleCode);
        Assert.Equal("page.size", query.ValidationKey);
        Assert.Equal("heading", query.Search);
        Assert.Equal(1, query.Page);
        Assert.Equal(AuditFindingQuery.DefaultPageSize, query.PageSize);
    }

    [Theory]
    [InlineData("critical", null, null, null, "finding-filter-enum-invalid")]
    [InlineData("0", null, null, null, "finding-filter-enum-invalid")]
    [InlineData(null, "automatic", null, null, "finding-filter-enum-invalid")]
    [InlineData(null, null, "newest", null, "finding-sort-invalid")]
    [InlineData(null, null, null, 0, "finding-pagination-invalid")]
    [InlineData(null, null, null, 101, "finding-pagination-invalid")]
    public void Findings_query_rejects_unbounded_or_unknown_inputs(
        string? severity,
        string? fixMode,
        string? sort,
        int? pageSize,
        string expectedCode)
    {
        var valid = AuditFindingQuery.TryCreate(
            severity, fixMode, null, null, null, null, null, null, sort, 1, pageSize,
            out _, out var error);

        Assert.False(valid);
        Assert.Equal(expectedCode, error);
    }

    [Fact]
    public void Findings_query_limits_the_result_window_to_the_finding_cap()
    {
        var valid = AuditFindingQuery.TryCreate(
            null, null, null, null, null, null, null, null, null, 101, 100,
            out _, out var error);

        Assert.False(valid);
        Assert.Equal("finding-pagination-invalid", error);
    }

    [Fact]
    public void Canonical_finding_dispositions_are_exhaustive_without_double_counting()
    {
        var summary = AuditFindingDispositionSummaryDto.Create(197, 0, 0, 0, 197);
        Assert.Equal(197, summary.ResolvedCount + summary.IgnoredCount + summary.RequiresReviewCount);
        Assert.Throws<InvalidOperationException>(() =>
            AuditFindingDispositionSummaryDto.Create(197, 1, 0, 0, 197));
    }

    [Theory]
    [InlineData(FindingStatus.Open, null, null, AuditFindingDisposition.RequiresReview)]
    [InlineData(FindingStatus.Open, FindingResolutionEventType.VerificationStillDetectedObserved, null, AuditFindingDisposition.RequiresReview)]
    [InlineData(FindingStatus.Open, FindingResolutionEventType.VerificationResolvedObserved, null, AuditFindingDisposition.Resolved)]
    [InlineData(FindingStatus.Open, null, FindingReviewEventType.Ignored, AuditFindingDisposition.Ignored)]
    [InlineData(FindingStatus.Open, null, FindingReviewEventType.AcceptedRisk, AuditFindingDisposition.Ignored)]
    public void Finding_disposition_preserves_still_detected_and_excludes_resolved_or_ignored(
        FindingStatus findingState,
        FindingResolutionEventType? resolution,
        FindingReviewEventType? review,
        AuditFindingDisposition expected) =>
        Assert.Equal(expected, AuditFindingDispositionProjection.Resolve(findingState, resolution, review));

    [Fact]
    public void Structural_presentation_uses_only_allowlisted_immutable_evidence()
    {
        var presentation = AuditFindingPresentation.Create(
            """{"Property":"marginLeft","NormalizedValue":"1701","Unit":"twip"}""",
            """{"Property":"marginLeft","AcceptedValues":["2268"],"Unit":"twip"}""");

        Assert.Equal("Margin kiri", presentation.PropertyLabel);
        Assert.Equal("3 cm", presentation.BeforeValue);
        Assert.Equal("4 cm", presentation.ExpectedValue);
        Assert.Equal("Complete", presentation.EvidenceState);
    }

    [Fact]
    public void Section_presence_uses_found_and_required_semantics()
    {
        var presentation = AuditFindingPresentation.Create(
            """{"Property":"sectionPresence.SummaryIndonesian","NormalizedValue":"absent"}""",
            """{"Property":"sectionPresence.SummaryIndonesian","AcceptedValues":["present"]}""");

        Assert.Equal("SectionRequirement", presentation.Kind);
        Assert.Equal("Ditemukan", presentation.BeforeLabel);
        Assert.Equal("Belum tersedia", presentation.BeforeValue);
        Assert.Equal("Wajib", presentation.ExpectedLabel);
        Assert.Contains("Ringkasan Bahasa Indonesia", presentation.ExpectedValue);
    }

    [Fact]
    public void Unknown_or_unsafe_evidence_is_never_echoed_or_fabricated()
    {
        var presentation = AuditFindingPresentation.Create(
            """{"Property":"unknown","NormalizedValue":"raw thesis sentence"}""",
            """{"Property":"unknown","AcceptedValues":["secret path"]}""");

        Assert.Equal("Unavailable", presentation.EvidenceState);
        Assert.Null(presentation.BeforeValue);
        Assert.Null(presentation.ExpectedValue);
        Assert.DoesNotContain("thesis", JsonSerializer.Serialize(presentation), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TextCorrectionAnalysisState.Pending, "Pending")]
    [InlineData(TextCorrectionAnalysisState.Processing, "Processing")]
    [InlineData(TextCorrectionAnalysisState.Completed, "Completed")]
    [InlineData(TextCorrectionAnalysisState.Failed, "Failed")]
    [InlineData(TextCorrectionAnalysisState.Skipped, "Skipped")]
    public void Persisted_text_correction_analysis_state_is_exact(
        TextCorrectionAnalysisState persisted,
        string expected)
    {
        Assert.Equal(expected, TextCorrectionAnalysisReadiness.Resolve(
            persisted, AuditJobStatus.Completed, true, true));
    }

    [Fact]
    public void Eligible_completed_current_audit_without_analysis_is_explicitly_awaiting()
    {
        Assert.Equal("AwaitingAnalysis", TextCorrectionAnalysisReadiness.Resolve(
            null, AuditJobStatus.Completed, true, true));
        Assert.Equal("Skipped", TextCorrectionAnalysisReadiness.Resolve(
            null, AuditJobStatus.Processing, true, true));
        Assert.Equal("Skipped", TextCorrectionAnalysisReadiness.Resolve(
            null, AuditJobStatus.Completed, false, true));
        Assert.Equal("Skipped", TextCorrectionAnalysisReadiness.Resolve(
            null, AuditJobStatus.Completed, true, false));
    }

    [Fact]
    public void Findings_query_accepts_explicit_page_and_maximum_page_size()
    {
        var valid = AuditFindingQuery.TryCreate(
            null, null, null, null, null, null, null, null, "default", 2, 100,
            out var query, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal(2, query.Page);
        Assert.Equal(100, query.PageSize);
    }

    [Fact]
    public void Findings_page_carries_canonical_audit_and_document_version_identity()
    {
        var auditId = Id(90);
        var documentVersionId = Id(91);
        var page = new AuditFindingPageDto(auditId, documentVersionId, 2, 25, 2_228, []);

        Assert.Equal(auditId, page.AuditId);
        Assert.Equal(documentVersionId, page.DocumentVersionId);
        Assert.Equal(2, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Equal(2_228, page.TotalCount);
    }

    [Fact]
    public void Finding_detail_contract_exposes_authoritative_workflow_disposition()
    {
        Assert.Equal(typeof(string), typeof(AuditFindingDetailDto).GetProperty("Disposition")!.PropertyType);
        Assert.Equal(typeof(string), typeof(AuditFindingListItemDto).GetProperty("Disposition")!.PropertyType);
        Assert.Equal(AuditFindingDisposition.Resolved,
            AuditFindingDispositionProjection.Resolve(FindingStatus.Open,
                FindingResolutionEventType.VerificationResolvedObserved, null));
        Assert.Equal(AuditFindingDisposition.Ignored,
            AuditFindingDispositionProjection.Resolve(FindingStatus.Open, null,
                FindingReviewEventType.AcceptedRisk));
    }

    [Fact]
    public void Findings_search_is_bounded_and_matches_only_safe_rule_metadata()
    {
        var rows = new[]
        {
            Row(1, "PPKI-LAYOUT-001", "Layout", "page.size", RuleSeverity.Error, FixMode.Manual, "{}"),
            Row(2, "PPKI-TYPE-001", "Typography", "font.size", RuleSeverity.Warning, FixMode.Report, "{}")
        }.AsQueryable();

        var result = AuditReadQueries.ApplyFilters(rows,
            new(null, null, null, null, null, null, null, 1, 25, "layout")).Single();

        Assert.Equal(Id(1), result.Id);
        Assert.Equal("%100\\%\\_safe\\\\value%", AuditReadQueries.SearchPattern("100%_safe\\value"));
        Assert.False(AuditFindingQuery.TryCreate(null, null, null, null, null, null, null,
            new string('a', 129), null, 1, 25, out _, out var error));
        Assert.Equal("finding-filter-text-invalid", error);
    }

    [Fact]
    public void Combined_filters_are_applied_together()
    {
        var wanted = Row(1, "RULE-A", "Layout", "page.size",
            RuleSeverity.Error, FixMode.Manual, "{\"bodyElementIndex\":1}");
        var rows = new[]
        {
            wanted,
            Row(2, "RULE-A", "Layout", "page.size", RuleSeverity.Warning, FixMode.Manual, "{}"),
            Row(3, "RULE-B", "Layout", "page.size", RuleSeverity.Error, FixMode.Manual, "{}"),
            Row(4, "RULE-A", "Typography", "page.size", RuleSeverity.Error, FixMode.Manual, "{}"),
            Row(5, "RULE-A", "Layout", "font.size", RuleSeverity.Error, FixMode.Manual, "{}"),
            Row(6, "RULE-A", "Layout", "page.size", RuleSeverity.Error, FixMode.Report, "{}")
        }.AsQueryable();
        var query = new AuditFindingQuery(
            RuleSeverity.Error, FixMode.Manual, null, null, "Layout", "RULE-A", "page.size", 1, 25);

        var result = AuditReadQueries.ApplyFilters(rows, query).Single();

        Assert.Equal(wanted.Id, result.Id);
    }

    [Fact]
    public void Each_supported_filter_is_exact_and_server_composable()
    {
        var wanted = Row(1, "RULE-A", "Layout", "page.size",
            RuleSeverity.Error, FixMode.Manual, "{}");
        var other = Row(2, "RULE-B", "Structure", "section.order",
            RuleSeverity.Warning, FixMode.Report, "{}");
        var rows = new[] { wanted, other }.AsQueryable();

        Assert.Equal(wanted.Id, Apply(rows, severity: RuleSeverity.Error).Single().Id);
        Assert.Equal(wanted.Id, Apply(rows, fixMode: FixMode.Manual).Single().Id);
        Assert.Equal(wanted.Id, Apply(rows, domain: "Layout").Single().Id);
        Assert.Equal(wanted.Id, Apply(rows, ruleCode: "RULE-A").Single().Id);
        Assert.Equal(wanted.Id, Apply(rows, validationKey: "page.size").Single().Id);
    }

    [Fact]
    public void Default_order_and_page_boundaries_are_repeatable()
    {
        var rows = new[]
        {
            Row(6, "RULE-C", "Layout", "c", RuleSeverity.Warning, FixMode.Report, "{\"sectionIndex\":1}", 2),
            Row(4, "RULE-B", "Layout", "b", RuleSeverity.Error, FixMode.Report, "{\"sectionIndex\":2}", 1),
            Row(2, "RULE-A", "Layout", "a", RuleSeverity.Error, FixMode.Report, "{\"sectionIndex\":1}", 1),
            Row(1, "RULE-A", "Layout", "a", RuleSeverity.Error, FixMode.Report, "{\"sectionIndex\":1}", 1),
            Row(5, "RULE-B", "Layout", "b", RuleSeverity.Warning, FixMode.Report, "{\"sectionIndex\":0}", 1),
            Row(3, "RULE-A", "Layout", "a", RuleSeverity.Error, FixMode.Report, "{\"sectionIndex\":2}", 1)
        }.AsQueryable();

        var first = AuditReadQueries.ApplyDefaultOrdering(rows).Skip(2).Take(3)
            .Select(value => value.Id).ToArray();
        var second = AuditReadQueries.ApplyDefaultOrdering(rows).Skip(2).Take(3)
            .Select(value => value.Id).ToArray();

        Assert.Equal(first, second);
        Assert.Equal(new[] { Id(3), Id(4), Id(5) }, first);
    }

    [Fact]
    public void Structural_locations_use_numeric_hierarchy_and_document_level_first()
    {
        var document = Row(1, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, Location("document", null, null, null, null));
        var sectionZero = Row(2, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, Location("section-0", 0, 5, null, null));
        var paragraphTwo = Row(3, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, Location("paragraph-2", 0, 5, 2, null));
        var paragraphNine = Row(4, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, Location("paragraph-9", 0, 5, 9, null));
        var paragraphTen = Row(5, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, Location("paragraph-10", 0, 5, 10, null));
        var paragraphEleven = Row(6, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, Location("paragraph-11", 0, 5, 11, null));
        var sectionOne = Row(7, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, Location("section-1", 1, 5, null, null));
        var run = Row(8, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, Location("run-0", 0, 5, 2, 0));
        var shuffled = new[]
        {
            paragraphTen, sectionOne, run, paragraphTwo, document,
            paragraphEleven, sectionZero, paragraphNine
        };

        var result = AuditReadQueries.ApplyDefaultOrdering(shuffled)
            .Select(value => value.Id).ToArray();

        Assert.Equal(new[]
        {
            document.Id, sectionZero.Id, paragraphTwo.Id, run.Id,
            paragraphNine.Id, paragraphTen.Id, paragraphEleven.Id, sectionOne.Id
        }, result);
    }

    [Fact]
    public void Numeric_paragraph_order_remains_correct_across_page_boundary()
    {
        var rows = new[]
        {
            Row(11, "RULE-A", "Layout", "a", RuleSeverity.Error, FixMode.Report,
                Location("paragraph-11", 0, 5, 11, null)),
            Row(2, "RULE-A", "Layout", "a", RuleSeverity.Error, FixMode.Report,
                Location("paragraph-2", 0, 5, 2, null)),
            Row(10, "RULE-A", "Layout", "a", RuleSeverity.Error, FixMode.Report,
                Location("paragraph-10", 0, 5, 10, null)),
            Row(9, "RULE-A", "Layout", "a", RuleSeverity.Error, FixMode.Report,
                Location("paragraph-9", 0, 5, 9, null))
        };
        var ordered = AuditReadQueries.ApplyDefaultOrdering(rows).ToArray();
        var pageOne = ordered.Take(2).Select(value => value.Id).ToArray();
        var pageTwo = ordered.Skip(2).Take(2).Select(value => value.Id).ToArray();

        Assert.Equal(new[] { Id(2), Id(9) }, pageOne);
        Assert.Equal(new[] { Id(10), Id(11) }, pageTwo);
        Assert.Equal(4, pageOne.Concat(pageTwo).Distinct().Count());
    }

    [Fact]
    public void Rule_code_precedes_finding_id_and_id_is_only_the_final_tie_breaker()
    {
        var location = Location("same", 0, 1, 1, null);
        var ruleBWithLowerId = Row(1, "RULE-B", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, location);
        var ruleAWithHigherId = Row(2, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, location);
        var ruleAWithLowestId = Row(0, "RULE-A", "Layout", "a", RuleSeverity.Error,
            FixMode.Report, location);

        var result = AuditReadQueries.ApplyDefaultOrdering(
                new[] { ruleBWithLowerId, ruleAWithHigherId, ruleAWithLowestId })
            .Select(value => value.Id).ToArray();

        Assert.Equal(new[] { ruleAWithLowestId.Id, ruleAWithHigherId.Id, ruleBWithLowerId.Id }, result);
    }

    [Fact]
    public void Parallel_structural_ordering_is_identical()
    {
        var rows = Enumerable.Range(0, 12)
            .Select(value => Row(value + 1, "RULE-A", "Layout", "a",
                RuleSeverity.Error, FixMode.Report,
                Location($"paragraph-{value}", value / 6, 5, value, null)))
            .Reverse()
            .ToArray();

        var results = Enumerable.Range(0, 16).AsParallel()
            .Select(_ => AuditReadQueries.ApplyDefaultOrdering(rows)
                .Select(value => value.Id).ToArray())
            .ToArray();

        Assert.All(results, value => Assert.Equal(results[0], value));
    }

    [Fact]
    public void Stable_pages_have_no_missing_or_duplicate_items()
    {
        var rows = Enumerable.Range(1, 11)
            .Select(value => Row(value, $"RULE-{value:00}", "Layout", "page.size",
                RuleSeverity.Error, FixMode.Report,
                $"{{\"bodyElementIndex\":{value}}}"))
            .Reverse()
            .AsQueryable();
        var ordered = AuditReadQueries.ApplyDefaultOrdering(rows);
        var pages = Enumerable.Range(0, 3)
            .SelectMany(page => ordered.Skip(page * 4).Take(4))
            .Select(value => value.Id)
            .ToArray();

        Assert.Equal(11, pages.Length);
        Assert.Equal(11, pages.Distinct().Count());
        Assert.Equal(rows.Select(value => value.Id).Order().ToArray(), pages.Order().ToArray());
    }

    [Fact]
    public void Shared_admin_findings_query_uses_immutable_snapshots_without_owner_filter()
    {
        using var db = Context();
        var sql = AuditReadQueries.OwnedFindings(db, Guid.NewGuid(), Guid.NewGuid())
            .ToQueryString().ToLowerInvariant();

        Assert.Contains("audit_findings", sql);
        Assert.Contains("audit_rule_snapshots", sql);
        Assert.DoesNotContain("owner_user_id", sql);
        Assert.DoesNotContain("rule_definitions", sql);
    }

    [Fact]
    public void Summary_filter_order_projection_and_page_are_database_translatable()
    {
        using var db = Context();
        var auditId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var summarySql = AuditReadQueries.OwnedSummaryBuckets(db, auditId, ownerId)
            .ToQueryString().ToLowerInvariant();
        var query = new AuditFindingQuery(
            RuleSeverity.Error, FixMode.Manual, AuditFindingDisposition.RequiresReview,
            true, "Layout", "RULE-A", "page.size", 2, 25);
        var pageSql = AuditReadQueries.ApplyDatabaseOrdering(
                AuditReadQueries.DatabaseFindings(db, auditId, query))
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(value => new { value.Id, value.RuleCode, value.LocationJson })
            .ToQueryString()
            .ToLowerInvariant();

        Assert.Contains("group by", summarySql);
        Assert.DoesNotContain("owner_user_id", summarySql);
        Assert.Contains("automatic_remediation_orchestrations", pageSql);
        Assert.Contains("limit", pageSql);
        Assert.Contains("offset", pageSql);
        Assert.Contains("order by", pageSql);
        Assert.Contains("collate \"c\"", pageSql);
        Assert.Contains("location_sort", pageSql);
        Assert.Contains("workflow", pageSql);
        Assert.Contains("severity", pageSql);
        Assert.Contains("fixmode", pageSql);
        Assert.Contains("domain", pageSql);
        Assert.Contains("rulecode", pageSql);
        Assert.Contains("validationkey", pageSql);
        Assert.DoesNotContain("owner_user_id", pageSql);
        Assert.DoesNotContain($"limit {AuditFindingQuery.MaximumFindingCount}", pageSql);
    }

    [Fact]
    public void Two_thousand_rows_have_repeatable_complete_bounded_pages()
    {
        var rows = Enumerable.Range(1, 2_037)
            .Select(value => Row(value, $"RULE-{value % 7:00}",
                value % 2 == 0 ? "Layout" : "Structure", "page.size",
                (RuleSeverity)(value % 3), (FixMode)(value % 4),
                Location($"paragraph-{value}", value / 500, value, value % 500, null),
                value % 11))
            .Reverse()
            .ToArray();
        var ordered = AuditReadQueries.ApplyDefaultOrdering(rows).ToArray();
        var firstRun = Enumerable.Range(0, 21)
            .SelectMany(page => ordered.Skip(page * 100).Take(100))
            .Select(value => value.Id).ToArray();
        var secondRun = Enumerable.Range(0, 21)
            .SelectMany(page => AuditReadQueries.ApplyDefaultOrdering(rows)
                .Skip(page * 100).Take(100))
            .Select(value => value.Id).ToArray();

        Assert.Equal(2_037, firstRun.Length);
        Assert.Equal(2_037, firstRun.Distinct().Count());
        Assert.Equal(firstRun, secondRun);
        Assert.All(Enumerable.Range(0, 20), page =>
            Assert.Equal(100, firstRun.Skip(page * 100).Take(100).Count()));
        Assert.Equal(37, firstRun.Skip(2_000).Count());
        Assert.Empty(firstRun.Skip(2_100));
    }

    [Fact]
    public void Read_models_do_not_expose_document_or_paragraph_text()
    {
        var properties = typeof(AuditFindingDetailDto).GetProperties()
            .Select(value => value.Name).ToArray();

        Assert.DoesNotContain("DocumentText", properties);
        Assert.DoesNotContain("ParagraphText", properties);
        Assert.DoesNotContain("Content", properties);
        Assert.Contains("ReasonCode", properties);
        Assert.Contains("Location", properties);
    }

    private static PpkiDbContext Context() => new(
        new DbContextOptionsBuilder<PpkiDbContext>()
            .UseNpgsql("Host=localhost;Database=audit_read_model_offline_test")
            .Options);

    private static AuditFindingReadRow Row(
        int id,
        string ruleCode,
        string domain,
        string validationKey,
        RuleSeverity severity,
        FixMode fixMode,
        string location,
        int ordinal = 1) => new(
            Id(id), Guid.Empty, Guid.Empty, ordinal, ruleCode, domain,
            validationKey, "Page", severity, fixMode, FindingStatus.Open,
            "finding.reason", "{}", "{}", location, null, null, null, null);

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private static string Location(
        string compactLocation,
        int? sectionIndex,
        int? bodyElementIndex,
        int? paragraphIndex,
        int? runIndex) => JsonSerializer.Serialize(new
        {
            CompactLocation = compactLocation,
            SectionIndex = sectionIndex,
            BodyElementIndex = bodyElementIndex,
            ParagraphIndex = paragraphIndex,
            RunIndex = runIndex
        });

    private static IQueryable<AuditFindingReadRow> Apply(
        IQueryable<AuditFindingReadRow> rows,
        RuleSeverity? severity = null,
        FixMode? fixMode = null,
        string? domain = null,
        string? ruleCode = null,
        string? validationKey = null) => AuditReadQueries.ApplyFilters(
            rows,
            new(severity, fixMode, null, null, domain, ruleCode, validationKey, 1, 25));
}
