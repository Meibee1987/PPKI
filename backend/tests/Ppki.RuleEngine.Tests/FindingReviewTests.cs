using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FindingReviewStateTests
{
    [Fact]
    public void No_event_projects_no_review() =>
        Assert.Equal(FindingReviewState.NoReview, FindingReviewProjection.State(null));

    [Theory]
    [InlineData(FindingReviewEventType.ReviewRequested, FindingReviewState.PendingReview)]
    [InlineData(FindingReviewEventType.NeedsRevision, FindingReviewState.NeedsRevision)]
    [InlineData(FindingReviewEventType.ManualRemediationApproved, FindingReviewState.ManualRemediationApproved)]
    [InlineData(FindingReviewEventType.ManualRemediationReported, FindingReviewState.ManualRemediationReported)]
    [InlineData(FindingReviewEventType.Rejected, FindingReviewState.Rejected)]
    [InlineData(FindingReviewEventType.Ignored, FindingReviewState.Ignored)]
    [InlineData(FindingReviewEventType.AcceptedRisk, FindingReviewState.AcceptedRisk)]
    public void Last_event_projects_review_state(FindingReviewEventType type, FindingReviewState expected) =>
        Assert.Equal(expected, FindingReviewProjection.State(type));

    [Theory]
    [InlineData(FindingReviewRequestedDisposition.ManualRemediation, FindingReviewDecision.ApproveManualRemediation)]
    [InlineData(FindingReviewRequestedDisposition.ManualRemediation, FindingReviewDecision.NeedsRevision)]
    [InlineData(FindingReviewRequestedDisposition.ManualRemediation, FindingReviewDecision.Reject)]
    [InlineData(FindingReviewRequestedDisposition.Ignore, FindingReviewDecision.Ignore)]
    [InlineData(FindingReviewRequestedDisposition.Ignore, FindingReviewDecision.NeedsRevision)]
    [InlineData(FindingReviewRequestedDisposition.Ignore, FindingReviewDecision.Reject)]
    [InlineData(FindingReviewRequestedDisposition.AcceptedRisk, FindingReviewDecision.AcceptRisk)]
    [InlineData(FindingReviewRequestedDisposition.AcceptedRisk, FindingReviewDecision.NeedsRevision)]
    [InlineData(FindingReviewRequestedDisposition.AcceptedRisk, FindingReviewDecision.Reject)]
    public void Request_has_only_its_allowed_decisions(FindingReviewRequestedDisposition request,
        FindingReviewDecision decision) => Assert.Contains(decision, FindingReviewProjection.Allowed(request));

    [Theory]
    [InlineData(FindingReviewRequestedDisposition.ManualRemediation, FindingReviewDecision.Ignore)]
    [InlineData(FindingReviewRequestedDisposition.ManualRemediation, FindingReviewDecision.AcceptRisk)]
    [InlineData(FindingReviewRequestedDisposition.Ignore, FindingReviewDecision.ApproveManualRemediation)]
    [InlineData(FindingReviewRequestedDisposition.Ignore, FindingReviewDecision.AcceptRisk)]
    [InlineData(FindingReviewRequestedDisposition.AcceptedRisk, FindingReviewDecision.ApproveManualRemediation)]
    [InlineData(FindingReviewRequestedDisposition.AcceptedRisk, FindingReviewDecision.Ignore)]
    public void Cross_decisions_are_not_allowed(FindingReviewRequestedDisposition request,
        FindingReviewDecision decision) => Assert.DoesNotContain(decision, FindingReviewProjection.Allowed(request));

    [Theory]
    [InlineData("Student", UserRole.Student)]
    [InlineData("Reviewer", UserRole.Reviewer)]
    [InlineData("PPKIAdmin", UserRole.PPKIAdmin)]
    [InlineData("UnitAdmin", UserRole.UnitAdmin)]
    public void Database_roles_parse_exactly(string value, UserRole expected) =>
        Assert.Equal(expected, UserRoleDatabase.ParseExact(value));

    [Theory]
    [InlineData("admin")]
    [InlineData("ppkiadmin")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void Unknown_or_wrong_case_role_is_rejected(string value) =>
        Assert.Multiple(
            () => Assert.Throws<ArgumentOutOfRangeException>(() => UserRoleDatabase.ParseExact(value)),
            () => Assert.False(UserRoleDatabase.TryParseExact(value, out _)));

    [Fact]
    public void Review_and_resolution_wire_states_are_separate_strings()
    {
        Assert.Equal("\"Ignored\"", JsonSerializer.Serialize(FindingReviewState.Ignored));
        Assert.Equal("\"VerifiedResolved\"", JsonSerializer.Serialize(FindingResolutionState.VerifiedResolved));
        Assert.NotEqual(JsonSerializer.Serialize(FindingReviewState.AcceptedRisk),
            JsonSerializer.Serialize(FindingResolutionState.VerifiedResolved));
    }

    [Fact]
    public void Note_is_trimmed_bounded_and_control_free()
    {
        Assert.Equal("safe note", FindingReviewService.NormalizeNote("  safe note  "));
        Assert.Null(FindingReviewService.NormalizeNote("   "));
        Assert.Equal("finding-review-note-invalid", Assert.Throws<FindingReviewException>(() =>
            FindingReviewService.NormalizeNote(new string('a', 1001))).DiagnosticCode);
        Assert.Equal("finding-review-note-invalid", Assert.Throws<FindingReviewException>(() =>
            FindingReviewService.NormalizeNote("line\nbreak")).DiagnosticCode);
    }
}

public sealed class FindingReviewArchitectureTests
{
    [Fact]
    public void Entity_model_has_canonical_case_and_append_only_event_keys()
    {
        using var db = new PpkiDbContext(new DbContextOptionsBuilder<PpkiDbContext>()
            .UseNpgsql("Host=localhost;Database=finding_review_offline_test").Options);
        var reviewCase = db.Model.FindEntityType(typeof(FindingReviewCase))!;
        var reviewEvent = db.Model.FindEntityType(typeof(FindingReviewEvent))!;
        Assert.Equal("finding_review_cases", reviewCase.GetTableName());
        Assert.Contains(reviewCase.GetIndexes(), value => value.IsUnique
            && value.Properties.Single().Name == nameof(FindingReviewCase.AuditFindingId));
        Assert.Contains(reviewEvent.GetIndexes(), value => value.IsUnique
            && value.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(FindingReviewEvent.ReviewCaseId), nameof(FindingReviewEvent.Sequence)]));
        Assert.Contains(reviewEvent.GetIndexes(), value => value.IsUnique
            && value.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(FindingReviewEvent.ReviewCaseId), nameof(FindingReviewEvent.IdempotencyKey)]));
    }

    [Fact]
    public void Response_contract_excludes_internal_and_document_payloads()
    {
        var properties = new[] { typeof(FindingReviewDto), typeof(FindingReviewEventDto),
            typeof(FindingReviewCommandResult), typeof(FindingReviewPermissions) }
            .SelectMany(value => value.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        var forbidden = new[] { "Actual", "Expected", "Fingerprint", "SemanticKey", "SourceEventKey",
            "IdempotencyKey", "DocumentText", "Filename", "Storage", "Path", "Url", "Xml", "Secret", "Role" };
        Assert.DoesNotContain(properties, property => forbidden.Any(value =>
            property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Authorization_is_database_role_based_ppki_admin_only_and_no_self_review()
    {
        var source = Source("backend", "src", "Ppki.Infrastructure", "FindingReviewService.cs");
        Assert.Contains("select role as", source, StringComparison.Ordinal);
        Assert.Contains("UserRoleDatabase.TryParseExact", source, StringComparison.Ordinal);
        Assert.Contains("UserRole.PPKIAdmin", source, StringComparison.Ordinal);
        Assert.Contains("OwnerUserId != actorUserId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ClaimTypes.Role", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UserRole.Reviewer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UserRole.UnitAdmin", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_contracts_cannot_choose_actor_role_owner_or_lineage()
    {
        var properties = new[] { typeof(FindingReviewRequest), typeof(FindingReviewDecisionRequest),
            typeof(ManualRemediationReportRequest) }.SelectMany(type => type.GetProperties());
        var forbidden = new[] { "Actor", "Reviewer", "Role", "Owner", "Audit", "Finding", "Rule",
            "State", "Sequence", "Version", "Execution", "Comparison", "Actual", "Expected" };
        Assert.DoesNotContain(properties, property => forbidden.Any(value =>
            property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Migration_is_additive_scoped_append_only_and_browser_read_only()
    {
        var sql = Source("supabase", "migrations", "202608050002_finding_review_workflow.sql");
        Assert.Contains("role = 'PPKIAdmin'", sql, StringComparison.Ordinal);
        Assert.Contains("document.owner_user_id <> auth.uid()", sql, StringComparison.Ordinal);
        Assert.Contains("on delete restrict", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uq_finding_review_cases_finding", sql, StringComparison.Ordinal);
        Assert.Contains("uq_finding_review_events_idempotency", sql, StringComparison.Ordinal);
        Assert.Contains("events are append-only", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protect_user_profile_role_from_browser", sql, StringComparison.Ordinal);
        Assert.Contains("VerificationResolvedObserved", sql, StringComparison.Ordinal);
        Assert.Contains("grant select", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant insert on table public.finding_review", sql.Replace(
            "grant insert on table public.finding_review_cases to service_role;", string.Empty).Replace(
            "grant insert on table public.finding_review_events to service_role;", string.Empty),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter table public.audit_findings", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter table public.finding_resolution", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("public.rules", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Service_does_not_mutate_historical_resolution_scoring_rules_or_docx()
    {
        var source = Source("backend", "src", "Ppki.Infrastructure", "FindingReviewService.cs");
        Assert.DoesNotContain("AuditFindings.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindingResolutionEvents.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindingResolutionCases.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleDefinitions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Score", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentVersions.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Docx", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FindingResolutionState.VerifiedResolved", source, StringComparison.Ordinal);
        Assert.Contains("finding-already-verified-resolved", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_has_bounded_events_idempotency_and_concurrency_recovery()
    {
        var source = Source("backend", "src", "Ppki.Infrastructure", "FindingReviewService.cs");
        Assert.Contains("MaximumEvents = 100", source, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.Serializable", source, StringComparison.Ordinal);
        Assert.Contains("IdempotencyKey == idempotencyKey", source, StringComparison.Ordinal);
        Assert.Contains("PayloadMatches", source, StringComparison.Ordinal);
        Assert.Contains("PostgresErrorCodes.UniqueViolation", source, StringComparison.Ordinal);
        Assert.Contains("PostgresErrorCodes.SerializationFailure", source, StringComparison.Ordinal);
        Assert.Contains("PostgresErrorCodes.DeadlockDetected", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_exposes_only_the_four_review_routes_and_safe_errors()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        Assert.Contains("/audits/{auditId}/findings/{findingId}/review\"", api, StringComparison.Ordinal);
        Assert.Contains("/audits/{auditId}/findings/{findingId}/review-requests", api, StringComparison.Ordinal);
        Assert.Contains("/finding-reviews/{reviewCaseId}/decisions", api, StringComparison.Ordinal);
        Assert.Contains("/finding-reviews/{reviewCaseId}/manual-remediation-reports", api, StringComparison.Ordinal);
        Assert.Contains("finding-review-not-reviewer", api, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewerId", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roleClaim", api, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_frontend_source_contains_finding_review_workflow()
    {
        var web = Path.Combine(Root(), "apps", "web", "src");
        Assert.DoesNotContain(Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories), file =>
            File.ReadAllText(file).Contains("finding-reviews", StringComparison.Ordinal));
    }

    private static string Source(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path]));
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "backend"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
