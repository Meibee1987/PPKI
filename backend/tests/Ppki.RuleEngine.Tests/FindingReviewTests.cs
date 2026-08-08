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
    public void User_role_serialization_is_an_explicit_string()
    {
        Assert.Equal("\"PPKIAdmin\"", JsonSerializer.Serialize(UserRole.PPKIAdmin));
        Assert.Equal("\"Student\"", JsonSerializer.Serialize(UserRole.Student));
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
    public void Authorization_is_database_role_based_ppki_admin_only_and_allows_self_review()
    {
        var source = Source("backend", "src", "Ppki.Infrastructure", "FindingReviewService.cs");
        Assert.Contains("select role as", source, StringComparison.Ordinal);
        Assert.Contains("UserRoleDatabase.TryParseExact", source, StringComparison.Ordinal);
        Assert.Contains("UserRole.PPKIAdmin", source, StringComparison.Ordinal);
        Assert.Contains("RequirePpkiAdminAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerUserId != actorUserId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerUserId == actorUserId", source, StringComparison.Ordinal);
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
    public void Correction_migration_is_additive_admin_only_self_review_and_browser_read_only()
    {
        var sql = Source("supabase", "migrations", "202608050003_admin_only_internal_access.sql");
        Assert.Contains("create or replace function public.is_ppki_admin()", sql, StringComparison.Ordinal);
        Assert.Contains("role = 'PPKIAdmin'", sql, StringComparison.Ordinal);
        Assert.Contains("operational self-approval", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("new.actor_user_id = review_case.requested_by_user_id", sql, StringComparison.Ordinal);
        Assert.Contains("protect_user_profile_delete_from_browser", sql, StringComparison.Ordinal);
        Assert.Contains("VerificationResolvedObserved", sql, StringComparison.Ordinal);
        Assert.Contains("finding_review_events_select_internal_admin", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("grant insert", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("public.rules", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Entire_business_api_uses_one_database_authoritative_admin_filter()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        var filter = Source("backend", "services", "Ppki.Api", "InternalAdminEndpointFilter.cs");
        Assert.Contains("MapGroup(\"/api\").RequireAuthorization().AddEndpointFilter<InternalAdminEndpointFilter>()", api, StringComparison.Ordinal);
        Assert.Contains("IInternalAdminAuthorizationService", filter, StringComparison.Ordinal);
        Assert.Contains("RequirePpkiAdminAsync", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("ClaimTypes.Role", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_internal_resources_are_not_filtered_by_actor_ownership()
    {
        var files = new[]
        {
            Source("backend", "src", "Ppki.Infrastructure", "AuditReadService.cs"),
            Source("backend", "src", "Ppki.Infrastructure", "FixPlanSourceReader.cs"),
            Source("backend", "src", "Ppki.Infrastructure", "FixExecutionRepository.cs"),
            Source("backend", "src", "Ppki.Infrastructure", "ReauditService.cs"),
            Source("backend", "src", "Ppki.Infrastructure", "AuditComparisonService.cs"),
            Source("backend", "src", "Ppki.Infrastructure", "FindingResolutionService.cs")
        };
        Assert.All(files, source => Assert.DoesNotContain("OwnerUserId == ownerUserId", source, StringComparison.Ordinal));

        var migration = Source("supabase", "migrations", "202608060001_shared_ppki_admin_access.sql");
        Assert.Contains("owner_user_id remains provenance", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("owner_user_id = (select auth.uid())", migration, StringComparison.Ordinal);
        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(migration,
            @"using \(public\.is_ppki_admin\(\)\);").Count);
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
        Assert.Contains("InternalAdminEndpointFilter", api, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewerId", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roleClaim", api, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frontend_review_workflow_uses_existing_routes_without_actor_or_role_input()
    {
        var client = Source("apps", "web", "src", "lib", "remediation-api.ts");
        var panel = Source("apps", "web", "src", "components", "finding-governance-panel.tsx");
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        Assert.Contains("/finding-reviews/${encodeURIComponent(caseId)}/decisions", client, StringComparison.Ordinal);
        Assert.Contains("review.allowedDecisions.map", panel, StringComparison.Ordinal);
        Assert.Contains("InternalAdminEndpointFilter", api, StringComparison.Ordinal);
        Assert.DoesNotContain("allowedDecisions =", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("actorUserId", client, StringComparison.Ordinal);
        Assert.DoesNotContain("role", client, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supabase.from", client, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage/v1", client, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AuditFinding", client, StringComparison.Ordinal);
        Assert.DoesNotContain("method: \"PUT\"", client, StringComparison.Ordinal);
        Assert.DoesNotContain("method: \"PATCH\"", client, StringComparison.Ordinal);
        Assert.DoesNotContain("method: \"DELETE\"", client, StringComparison.Ordinal);
    }

    private static string Source(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path]));
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "backend"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
