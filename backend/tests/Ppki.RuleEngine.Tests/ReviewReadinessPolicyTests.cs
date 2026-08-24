using System.Text.Json;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;
using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class ReviewReadinessCatalogTests
{
    [Fact]
    public void Authoritative_catalog_has_explicit_exact_policy_for_all_317_rules()
    {
        var catalog = RuleCatalogImporter.ParseAndValidate(File.ReadAllText(CatalogPath()));

        Assert.Equal(317, catalog.Rules.Count);
        Assert.Equal(22, catalog.Rules.Count(value => value.ReviewBlockingPolicy == "Blocking"));
        Assert.Equal(3, catalog.Rules.Count(value => value.ReviewBlockingPolicy == "NonBlocking"));
        Assert.Equal(292, catalog.Rules.Count(value => value.ReviewBlockingPolicy == "PendingApproval"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blocking")]
    [InlineData("Unknown")]
    [InlineData("Error")]
    public void Missing_unknown_or_incorrectly_cased_policy_fails_import(string? policy)
    {
        var property = policy is null ? "" : $",\"review_blocking_policy\":{JsonSerializer.Serialize(policy)}";
        var json = $$"""{"rules":[{"rule_id":"RULE-A"{{property}}}]}""";

        Assert.Throws<InvalidOperationException>(() => RuleCatalogImporter.ParseAndValidate(json));
    }

    [Fact]
    public void PendingApproval_is_valid_for_an_unsupported_catalog_rule()
    {
        var catalog = RuleCatalogImporter.ParseAndValidate(
            "{\"rules\":[{\"rule_id\":\"RULE-A\",\"review_blocking_policy\":\"PendingApproval\"}]}");

        Assert.Equal("PendingApproval", Assert.Single(catalog.Rules).ReviewBlockingPolicy);
    }

    [Fact]
    public void Approved_nonblocking_exceptions_are_explicit_and_not_inferred_from_other_fields()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CatalogPath()));
        var rules = document.RootElement.GetProperty("rules").EnumerateArray()
            .ToDictionary(value => value.GetProperty("rule_id").GetString()!, StringComparer.Ordinal);

        foreach (var code in new[] { "PPKI-HDG-002", "PPKI-ABS-011", "PPKI-ABS-019" })
            Assert.Equal("NonBlocking", rules[code].GetProperty("review_blocking_policy").GetString());
        Assert.Equal("PendingApproval", rules["PPKI-LAY-001"].GetProperty("review_blocking_policy").GetString());
        Assert.Equal("Error", rules["PPKI-LAY-001"].GetProperty("severity").GetString());
    }

    [Fact]
    public void Runtime_snapshot_creation_fails_closed_for_PendingApproval()
    {
        var rule = Rule(ReviewBlockingPolicy.PendingApproval);
        var exception = Assert.Throws<ReviewReadinessPolicyResolutionException>(() =>
            new ResolvedRuleSetSnapshotBuilder().Build(Guid.NewGuid(), [rule], "profile", 0));

        Assert.Equal("review-readiness-policy-pending-approval", exception.DiagnosticCode);
    }

    [Fact]
    public void Snapshot_policy_is_immutable_when_live_catalog_rule_changes_later()
    {
        var rule = Rule(ReviewBlockingPolicy.Blocking);
        var snapshot = Assert.Single(new ResolvedRuleSetSnapshotBuilder()
            .Build(Guid.NewGuid(), [rule], "profile", 0));

        rule.ReviewBlockingPolicy = ReviewBlockingPolicy.NonBlocking;
        rule.ReadinessPolicyVersion = "later-policy-v2";

        Assert.Equal(ReviewBlockingPolicy.Blocking, snapshot.ReviewBlockingPolicy);
        Assert.Equal(ReviewReadinessPolicy.Version, snapshot.ReadinessPolicyVersion);
    }

    [Fact]
    public void Existing_mutable_catalog_rows_are_reconciled_from_explicit_source_policy()
    {
        var rule = Rule(ReviewBlockingPolicy.PendingApproval);
        rule.RuleCode = "PPKI-HDG-002";
        var catalog = RuleCatalogImporter.ParseAndValidate(
            "{\"rules\":[{\"rule_id\":\"PPKI-HDG-002\",\"review_blocking_policy\":\"NonBlocking\"}]}");

        Assert.Equal(1, RuleCatalogImporter.ReconcileReviewPolicies([rule], catalog.Rules));
        Assert.Equal(ReviewBlockingPolicy.NonBlocking, rule.ReviewBlockingPolicy);
        Assert.Equal(ReviewReadinessPolicy.Version, rule.ReadinessPolicyVersion);
        Assert.Equal(0, RuleCatalogImporter.ReconcileReviewPolicies([rule], catalog.Rules));
    }

    [Fact]
    public void Additive_migration_preserves_legacy_snapshot_unknowns_without_backfill()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "policy-fixtures",
            "202608240001_review_readiness_policy.sql"));

        Assert.Contains("add column review_blocking_policy", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null means legacy Unknown", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("update public.audit_rule_snapshots", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("severity", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static RuleDefinition Rule(ReviewBlockingPolicy policy) => new()
    {
        RuleCode = "RULE-A", Domain = "LAY", AppliesTo = "Semua", Element = "Page",
        OfficialRequirement = "Synthetic", ExpectedValuePattern = "Synthetic",
        Severity = RuleSeverity.Error, FixMode = FixMode.Manual,
        ValidationKey = "synthetic", IsImplemented = true,
        ReviewBlockingPolicy = policy, ReadinessPolicyVersion = ReviewReadinessPolicy.Version
    };

    private static string CatalogPath() => Path.Combine(
        AppContext.BaseDirectory, "policy-fixtures", "rules.json");
}

public sealed class ReviewReadinessProjectionTests
{
    [Theory]
    [InlineData(AuditJobStatus.Queued)]
    [InlineData(AuditJobStatus.Processing)]
    public void Active_audit_is_in_progress(AuditJobStatus status)
    {
        var result = Resolve(status, []);
        Assert.Equal(ReviewReadinessState.AuditInProgress, result.State);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(AuditJobStatus.Failed, ReviewReadinessReason.AuditFailed)]
    [InlineData(AuditJobStatus.Cancelled, ReviewReadinessReason.AuditCancelled)]
    public void Terminal_incomplete_audit_is_unknown_with_typed_reason(
        AuditJobStatus status, ReviewReadinessReason reason)
    {
        var result = Resolve(status, []);
        Assert.Equal(ReviewReadinessState.Unknown, result.State);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Zero_applicable_rules_is_unknown()
    {
        var result = ReviewReadinessProjection.Resolve(AuditJobStatus.Completed, 0, [], []);
        Assert.Equal(ReviewReadinessReason.NoApplicableRules, result.Reason);
    }

    [Fact]
    public void Legacy_or_unknown_snapshot_policy_is_never_guessed()
    {
        var result = ReviewReadinessProjection.Resolve(AuditJobStatus.Completed, 1,
            [new(1, null, null)], []);
        Assert.Equal(ReviewReadinessState.Unknown, result.State);
        Assert.Equal(ReviewReadinessReason.PolicyUnknown, result.Reason);
    }

    [Fact]
    public void NonBlocking_finding_does_not_affect_readiness()
    {
        var result = Resolve(AuditJobStatus.Completed,
            [Finding(ReviewBlockingPolicy.NonBlocking)]);
        Assert.Equal(ReviewReadinessState.ReadyForReview, result.State);
        Assert.Equal(0, result.BlockingFindingCount);
    }

    [Theory]
    [InlineData(FindingStatus.Open, null, null)]
    [InlineData(FindingStatus.Fixed, null, null)]
    [InlineData(FindingStatus.Ignored, null, FindingReviewEventType.Ignored)]
    [InlineData(FindingStatus.ManualReview, null, FindingReviewEventType.AcceptedRisk)]
    [InlineData(FindingStatus.ManualReview, null, FindingReviewEventType.ManualRemediationReported)]
    [InlineData(FindingStatus.ManualReview, null, FindingReviewEventType.ManualRemediationApproved)]
    [InlineData(FindingStatus.ManualReview, null, FindingReviewEventType.ReviewRequested)]
    [InlineData(FindingStatus.ManualReview, null, FindingReviewEventType.NeedsRevision)]
    [InlineData(FindingStatus.ManualReview, null, FindingReviewEventType.Rejected)]
    [InlineData(FindingStatus.Open, FindingResolutionEventType.FixAppliedObserved, null)]
    [InlineData(FindingStatus.Open, FindingResolutionEventType.ReauditPendingObserved, null)]
    [InlineData(FindingStatus.Fixed, FindingResolutionEventType.VerificationStillDetectedObserved, null)]
    public void Blocking_finding_remains_effective_without_verified_resolution(
        FindingStatus status,
        FindingResolutionEventType? resolution,
        FindingReviewEventType? review)
    {
        var result = Resolve(AuditJobStatus.Completed,
            [Finding(ReviewBlockingPolicy.Blocking, status, resolution, review)]);
        Assert.Equal(ReviewReadinessState.NeedsFix, result.State);
        Assert.Equal(1, result.BlockingFindingCount);
    }

    [Fact]
    public void VerifiedResolved_is_the_only_evidence_that_clears_a_blocker()
    {
        var result = Resolve(AuditJobStatus.Completed,
            [Finding(ReviewBlockingPolicy.Blocking, FindingStatus.Open,
                FindingResolutionEventType.VerificationResolvedObserved)]);
        Assert.Equal(ReviewReadinessState.ReadyForReview, result.State);
        Assert.Equal(0, result.BlockingFindingCount);
    }

    private static ReviewReadinessResult Resolve(
        AuditJobStatus status, IReadOnlyList<ReviewReadinessFinding> findings) =>
        ReviewReadinessProjection.Resolve(status, 1,
            [new(2, ReviewBlockingPolicy.Blocking, ReviewReadinessPolicy.Version)], findings);

    private static ReviewReadinessFinding Finding(
        ReviewBlockingPolicy policy,
        FindingStatus status = FindingStatus.Open,
        FindingResolutionEventType? resolution = null,
        FindingReviewEventType? review = null) => new(policy, status, resolution, review);
}
