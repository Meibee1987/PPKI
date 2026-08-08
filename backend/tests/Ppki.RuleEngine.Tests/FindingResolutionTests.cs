using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FindingResolutionStateTests
{
    [Fact]
    public void No_event_projects_open_without_a_persisted_case() =>
        Assert.Equal(FindingResolutionState.Open, FindingResolutionProjection.State(null));

    [Fact]
    public void Resolution_state_and_event_type_serialize_as_wire_names()
    {
        Assert.Equal("\"Open\"", JsonSerializer.Serialize(FindingResolutionState.Open));
        Assert.Equal("\"FixAppliedObserved\"", JsonSerializer.Serialize(FindingResolutionEventType.FixAppliedObserved));
    }

    [Theory]
    [InlineData(FindingResolutionEventType.FixAppliedObserved, FindingResolutionState.Applied)]
    [InlineData(FindingResolutionEventType.ReauditPendingObserved, FindingResolutionState.ReauditPending)]
    [InlineData(FindingResolutionEventType.VerificationResolvedObserved, FindingResolutionState.VerifiedResolved)]
    [InlineData(FindingResolutionEventType.VerificationStillDetectedObserved, FindingResolutionState.VerifiedStillDetected)]
    public void Last_event_projects_the_current_state(FindingResolutionEventType type, FindingResolutionState expected) =>
        Assert.Equal(expected, FindingResolutionProjection.State(type));

    [Theory]
    [InlineData(AuditComparisonStatus.NoLongerDetected, FindingResolutionEventType.VerificationResolvedObserved)]
    [InlineData(AuditComparisonStatus.StillDetected, FindingResolutionEventType.VerificationStillDetectedObserved)]
    [InlineData(AuditComparisonStatus.Changed, FindingResolutionEventType.VerificationStillDetectedObserved)]
    public void Comparison_status_maps_conservatively(AuditComparisonStatus status, FindingResolutionEventType expected) =>
        Assert.Equal(expected, FindingResolutionProjection.VerificationEvent(status));

    [Fact]
    public void Newly_detected_cannot_change_a_source_case()
    {
        var error = Assert.Throws<FindingResolutionException>(() =>
            FindingResolutionProjection.VerificationEvent(AuditComparisonStatus.NewlyDetected));
        Assert.Equal("resolution-comparison-invalid", error.DiagnosticCode);
    }

    [Theory]
    [InlineData(FixExecutionState.Queued)]
    [InlineData(FixExecutionState.Processing)]
    [InlineData(FixExecutionState.Failed)]
    [InlineData(FixExecutionState.NoChange)]
    public void Non_completed_execution_is_rejected(FixExecutionState state)
    {
        var error = Assert.Throws<FindingResolutionException>(() =>
            FindingResolutionService.ValidateSource(Source() with { ExecutionState = state }));
        Assert.Equal("resolution-execution-not-completed", error.DiagnosticCode);
    }

    [Fact]
    public void Missing_result_version_is_rejected()
    {
        var error = Assert.Throws<FindingResolutionException>(() => FindingResolutionService.ValidateSource(
            Source() with { ResultDocumentVersionId = null, ResultDocumentId = null }));
        Assert.Equal("resolution-result-version-missing", error.DiagnosticCode);
    }

    [Theory]
    [InlineData(AuditJobStatus.Queued)]
    [InlineData(AuditJobStatus.Processing)]
    [InlineData(AuditJobStatus.Failed)]
    public void Non_completed_source_audit_is_rejected(AuditJobStatus status)
    {
        var error = Assert.Throws<FindingResolutionException>(() => FindingResolutionService.ValidateSource(
            Source() with { SourceAuditStatus = status }));
        Assert.Equal("resolution-source-audit-not-completed", error.DiagnosticCode);
    }

    [Fact]
    public void Source_and_result_document_lineage_must_match()
    {
        var error = Assert.Throws<FindingResolutionException>(() => FindingResolutionService.ValidateSource(
            Source() with { ResultDocumentId = Guid.NewGuid() }));
        Assert.Equal("resolution-lineage-mismatch", error.DiagnosticCode);
    }

    [Fact]
    public void Finding_ids_do_not_change_comparison_aggregate_or_resolution_outcome()
    {
        var first = AuditComparisonEngine.Compare([Finding(Guid.NewGuid(), "left")], [Finding(Guid.NewGuid(), "both", false)]);
        var second = AuditComparisonEngine.Compare([Finding(Guid.NewGuid(), "left")], [Finding(Guid.NewGuid(), "both", false)]);
        Assert.Equal(first.Single().Status, second.Single().Status);
        Assert.Equal(FindingResolutionProjection.VerificationEvent(first.Single().Status),
            FindingResolutionProjection.VerificationEvent(second.Single().Status));
    }

    [Fact]
    public void Duplicate_findings_remain_separate_source_cases()
    {
        var source = new[] { Finding(Guid.NewGuid(), "left"), Finding(Guid.NewGuid(), "left") };
        var result = new[] { Finding(Guid.NewGuid(), "left", false) };
        var comparisons = AuditComparisonEngine.Compare(source, result).Where(value => value.Before is not null).ToArray();
        Assert.Equal(2, comparisons.Length);
        Assert.Equal(2, comparisons.Select(value => value.Before!.Id).Distinct().Count());
    }

    [Fact]
    public void Resolution_outcome_is_input_order_and_culture_invariant()
    {
        var source = new[] { Finding(Guid.NewGuid(), "a"), Finding(Guid.NewGuid(), "b") };
        var result = new[] { Finding(Guid.NewGuid(), "a", false), Finding(Guid.NewGuid(), "c", false) };
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("id-ID");
            var first = AuditComparisonEngine.Compare(source, result).Select(value => value.Status).Order().ToArray();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = AuditComparisonEngine.Compare(source.Reverse(), result.Reverse()).Select(value => value.Status).Order().ToArray();
            Assert.Equal(first, second);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static FindingResolutionSourceContext Source()
    {
        var sourceVersion = Guid.NewGuid();
        var document = Guid.NewGuid();
        return new(Guid.NewGuid(), FixExecutionState.Completed, Guid.NewGuid(), AuditJobStatus.Completed,
            sourceVersion, sourceVersion, document, Guid.NewGuid(), document, Guid.NewGuid(),
            DocumentKind.Skripsi, new string('a', 64), 1, "[]", "{}", new string('b',64), "v1",
            new string('c',64), new string('d',64), new string('d',64), sourceVersion,
            Guid.Parse("51000000-0000-0000-0000-000000000099"), Guid.Parse("51000000-0000-0000-0000-000000000099"),
            DateTimeOffset.UtcNow);
    }

    private static AuditComparisonFindingSnapshot Finding(Guid id, string actual, bool before = true) => new(
        id, before ? Guid.Parse("51000000-0000-0000-0000-000000000001") : Guid.Parse("51000000-0000-0000-0000-000000000002"),
        1, "PPKI-LAY-019", "LAY", "body.justified", "paragraph", RuleSeverity.Error,
        FixMode.Auto, FindingStatus.Open, "paragraph-alignment-invalid",
        $"{{\"Property\":\"alignment\",\"NormalizedValue\":\"{actual}\"}}",
        "{\"Property\":\"alignment\",\"AcceptedValues\":[\"both\"]}",
        "{\"CompactLocation\":\"body/1\",\"BodyElementIndex\":1}", null, null, null, null);
}

public sealed class FindingResolutionArchitectureTests
{
    [Fact]
    public void Owned_queries_filter_before_materialization()
    {
        using var db = new PpkiDbContext(new DbContextOptionsBuilder<PpkiDbContext>()
            .UseNpgsql("Host=localhost;Database=finding_resolution_offline_test").Options);
        var sql = FindingResolutionService.OwnedExecution(db, Guid.NewGuid(), Guid.NewGuid()).ToQueryString().ToLowerInvariant();
        Assert.Contains("owner_user_id", sql, StringComparison.Ordinal);
        Assert.Contains("fix_execution_jobs", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_response_contract_excludes_sensitive_or_internal_payloads()
    {
        var properties = new[] { typeof(FindingResolutionDto), typeof(FindingResolutionEventDto),
            typeof(FindingResolutionReconciliationResult) }
            .SelectMany(value => value.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        var forbidden = new[] { "Actual", "Expected", "Fingerprint", "SemanticKey", "SourceEventKey",
            "DocumentText", "Filename", "Path", "Url", "Xml", "Secret", "Snapshot" };
        Assert.DoesNotContain(properties, property => forbidden.Any(value =>
            property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Service_uses_historical_snapshots_and_shared_comparison_without_live_catalog_or_docx()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "backend", "src", "Ppki.Infrastructure", "FindingResolutionService.cs"));
        Assert.Contains("ApprovedFixExecutionPlanSerializer.Deserialize", source, StringComparison.Ordinal);
        Assert.Contains("AuditRuleSnapshots.AsNoTracking", source, StringComparison.Ordinal);
        Assert.Contains("AuditComparisonEngine.Compare", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleDefinitions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileRules", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentTypes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Docx", source, StringComparison.OrdinalIgnoreCase);
        Assert.True(source.Split("SaveChangesAsync", StringSplitOptions.None).Length >= 4,
            "Case, applied, and verification inserts must be separate database phases.");
        Assert.True(source.Split("ChangeTracker.Clear", StringSplitOptions.None).Length >= 3,
            "Immutable event entities must not remain tracked between insertion phases.");
        Assert.Contains("maximumAttempts = 5", source, StringComparison.Ordinal);
        Assert.Contains("Task.Delay", source, StringComparison.Ordinal);
        Assert.Contains("PostgresErrorCodes.DeadlockDetected", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_is_additive_append_only_and_browser_read_only()
    {
        var migration = File.ReadAllText(Path.Combine(Root(), "supabase", "migrations", "202608050001_finding_resolution_state.sql"));
        Assert.Contains("on delete restrict", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uq_finding_resolution_cases_finding", migration, StringComparison.Ordinal);
        Assert.Contains("uq_finding_resolution_events_source_event", migration, StringComparison.Ordinal);
        Assert.Contains("events are append-only", migration, StringComparison.Ordinal);
        Assert.Contains("enable row level security", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grant select", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant insert on table public.finding_resolution", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Api_has_bodyless_recovery_and_read_routes_only()
    {
        var api = File.ReadAllText(Path.Combine(Root(), "backend", "services", "Ppki.Api", "Program.cs"));
        Assert.Contains("/audits/{auditId}/findings/{findingId}/resolution", api, StringComparison.Ordinal);
        Assert.Contains("/fix-executions/{executionId}/resolution-reconciliation", api, StringComparison.Ordinal);
        Assert.DoesNotContain("FindingResolutionRequest", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_fixture_casts_values_identifiers_to_uuid()
    {
        var smoke = File.ReadAllText(Path.Combine(Root(), "scripts", "finding-resolution-smoke-test.mjs"));
        Assert.Contains("'::uuid", smoke, StringComparison.Ordinal);
        Assert.Contains("sourceFindingIds.map", smoke, StringComparison.Ordinal);
        Assert.Contains("resultFindingIds", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_resolution_workflow_uses_bodyless_existing_reconciliation_route()
    {
        var client = File.ReadAllText(Path.Combine(Root(), "apps", "web", "src", "lib", "remediation-api.ts"));
        var workflow = File.ReadAllText(Path.Combine(Root(), "apps", "web", "src", "components", "remediation-workflow.tsx"));
        Assert.Contains("/resolution-reconciliation", client, StringComparison.Ordinal);
        Assert.Contains("{ method: \"POST\" }", client, StringComparison.Ordinal);
        Assert.DoesNotContain("FindingResolutionRequest", client, StringComparison.Ordinal);
        Assert.Contains("getComparison(execution.id", workflow, StringComparison.Ordinal);
        Assert.Contains("reconcileResolution(execution.id)", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("levenshtein", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approvedPlan", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operation.provider", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operation.target", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage/v1", client, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supabase.from", client, StringComparison.OrdinalIgnoreCase);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "backend"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
