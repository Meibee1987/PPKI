using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class AuditScoringTests
{
    private static readonly AuditScoringPolicy Policy = new(
        "ppki-score-v1-test", 0m, 100m, 20m, 8m, 3m, 0m, 2,
        MidpointRounding.AwayFromZero);

    private readonly AuditScoreCalculator calculator = new();

    [Fact]
    public void Completed_audit_requires_an_explicit_policy()
    {
        var result = calculator.Calculate(Input(1), policy: null);

        Assert.Equal(AuditScoreState.NotConfigured, result.State);
        Assert.Null(result.Score);
        Assert.Equal("scoring-policy-not-configured", result.DiagnosticCode);
    }

    [Fact]
    public void Incomplete_and_empty_audits_have_explicit_non_numeric_states()
    {
        var incomplete = calculator.Calculate(
            new(AuditJobStatus.Processing, 1, []), Policy);
        var empty = calculator.Calculate(
            new(AuditJobStatus.Completed, 0, []), Policy);

        Assert.Equal(AuditScoreState.AuditIncomplete, incomplete.State);
        Assert.Null(incomplete.Score);
        Assert.Equal(AuditScoreState.NotApplicable, empty.State);
        Assert.Null(empty.Score);
    }

    [Fact]
    public void Versioned_policy_calculation_is_deterministic_and_scores_each_persisted_finding()
    {
        var input = Input(3,
            new AuditScoreFinding("RULE-A", RuleSeverity.Warning),
            new AuditScoreFinding("RULE-A", RuleSeverity.Error),
            new AuditScoreFinding("RULE-B", RuleSeverity.Warning),
            new AuditScoreFinding("RULE-C", RuleSeverity.Info));

        var first = calculator.Calculate(input, Policy);
        var repeated = Enumerable.Range(0, 20)
            .Select(_ => calculator.Calculate(input, Policy))
            .ToArray();

        Assert.Equal(AuditScoreState.Calculated, first.State);
        Assert.Equal(30m, first.Score);
        Assert.Equal("ppki-score-v1-test", first.PolicyVersion);
        Assert.Equal(4, first.Breakdown!.ScoredFindingCount);
        Assert.Equal(3, first.Breakdown!.DistinctViolatedRules);
        Assert.Equal(14m, first.Breakdown.TotalPenalty);
        Assert.All(repeated, value => Assert.Equal(first, value));
    }

    [Fact]
    public void Same_rule_at_two_semantically_distinct_locations_is_scored_twice()
    {
        // Sprint 02 persistence has already kept these as two different rows
        // because their full semantic identities have different locations.
        var sectionZero = new AuditScoreFinding("RULE-A", RuleSeverity.Warning);
        var sectionOne = new AuditScoreFinding("RULE-A", RuleSeverity.Warning);

        var result = calculator.Calculate(Input(1, sectionZero, sectionOne), Policy);

        Assert.Equal(70m, result.Score);
        Assert.Equal(2, result.Breakdown!.ScoredFindingCount);
        Assert.Equal(1, result.Breakdown.DistinctViolatedRules);
        Assert.Equal(6m, result.Breakdown.TotalPenalty);
    }

    [Fact]
    public void Different_rules_are_scored_as_separate_persisted_findings()
    {
        var result = calculator.Calculate(Input(2,
            new AuditScoreFinding("RULE-A", RuleSeverity.Warning),
            new AuditScoreFinding("RULE-B", RuleSeverity.Warning)), Policy);

        Assert.Equal(70m, result.Score);
        Assert.Equal(2, result.Breakdown!.ScoredFindingCount);
        Assert.Equal(2, result.Breakdown.DistinctViolatedRules);
    }

    [Fact]
    public void Persistence_semantic_identity_uses_rule_location_property_and_normalized_value()
    {
        var first = Candidate("main/s:0/b:1/p:1", "alignment", "left");
        var exactDuplicate = Candidate("main/s:0/b:1/p:1", "alignment", "left");
        var differentLocation = Candidate("main/s:1/b:1/p:1", "alignment", "left");
        var differentProperty = Candidate("main/s:0/b:1/p:1", "lineSpacing", "left");
        var differentActual = Candidate("main/s:0/b:1/p:1", "alignment", "right");

        Assert.Equal(first.SemanticKey("RULE-A"), exactDuplicate.SemanticKey("RULE-A"));
        Assert.NotEqual(first.SemanticKey("RULE-A"), differentLocation.SemanticKey("RULE-A"));
        Assert.NotEqual(first.SemanticKey("RULE-A"), differentProperty.SemanticKey("RULE-A"));
        Assert.NotEqual(first.SemanticKey("RULE-A"), differentActual.SemanticKey("RULE-A"));
        Assert.NotEqual(first.SemanticKey("RULE-A"), first.SemanticKey("RULE-B"));
    }

    [Fact]
    public void Parallel_calculations_are_identical()
    {
        var input = Input(2,
            new AuditScoreFinding("RULE-A", RuleSeverity.Error),
            new AuditScoreFinding("RULE-B", RuleSeverity.Info));

        var results = Enumerable.Range(0, 32).AsParallel()
            .Select(_ => calculator.Calculate(input, Policy))
            .ToArray();

        Assert.All(results, value => Assert.Equal(results[0], value));
    }

    [Fact]
    public void Info_has_no_penalty_only_when_the_explicit_policy_says_zero()
    {
        var zero = calculator.Calculate(
            Input(1, new AuditScoreFinding("RULE-A", RuleSeverity.Info)), Policy);
        var weighted = calculator.Calculate(
            Input(1, new AuditScoreFinding("RULE-A", RuleSeverity.Info)),
            Policy with { InfoWeight = 2m });

        Assert.Equal(100m, zero.Score);
        Assert.Equal(90m, weighted.Score);
    }

    [Fact]
    public void Live_rule_mutation_cannot_change_historical_snapshot_score()
    {
        var input = Input(1, new AuditScoreFinding("RULE-A", RuleSeverity.Error));
        var liveRule = new RuleDefinition
        {
            RuleCode = "RULE-A",
            Domain = "Layout",
            AppliesTo = "Document",
            Element = "Page",
            OfficialRequirement = "live catalog value",
            ExpectedValuePattern = "{}",
            Severity = RuleSeverity.Error,
            FixMode = FixMode.Report,
            ValidationKey = "page.size"
        };
        var before = calculator.Calculate(input, Policy);

        liveRule.Severity = RuleSeverity.Info;
        liveRule.FixMode = FixMode.Auto;
        var after = calculator.Calculate(input, Policy);

        Assert.Equal(before, after);
    }

    [Fact]
    public void Explicit_rounding_and_score_range_are_honoured()
    {
        var thirdPolicy = Policy with
        {
            MaximumPenalty = 3m,
            ErrorWeight = 1m,
            WarningWeight = 1m
        };
        var rounded = calculator.Calculate(
            Input(1, new AuditScoreFinding("RULE-A", RuleSeverity.Error)), thirdPolicy);
        var clamped = calculator.Calculate(
            Input(4,
                new AuditScoreFinding("A", RuleSeverity.Error),
                new AuditScoreFinding("B", RuleSeverity.Error),
                new AuditScoreFinding("C", RuleSeverity.Error),
                new AuditScoreFinding("D", RuleSeverity.Error)),
            thirdPolicy);

        Assert.Equal(66.67m, rounded.Score);
        Assert.Equal(0m, clamped.Score);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Invalid_policy_is_rejected_without_inventing_a_score(string version)
    {
        var result = calculator.Calculate(Input(1), Policy with { Version = version });

        Assert.Equal(AuditScoreState.InvalidConfiguration, result.State);
        Assert.Null(result.Score);
        Assert.Equal("scoring-configuration-invalid", result.DiagnosticCode);
    }

    [Fact]
    public void Invalid_finding_snapshot_is_rejected_safely()
    {
        var result = calculator.Calculate(
            Input(1, new AuditScoreFinding(" ", RuleSeverity.Error)), Policy);

        Assert.Equal(AuditScoreState.InvalidConfiguration, result.State);
        Assert.Null(result.Score);
    }

    [Fact]
    public void Overflowing_policy_is_invalid_instead_of_throwing()
    {
        var policy = Policy with
        {
            MinimumScore = decimal.MinValue,
            MaximumScore = decimal.MaxValue
        };

        var result = calculator.Calculate(
            Input(1, new AuditScoreFinding("RULE-A", RuleSeverity.Error)), policy);

        Assert.Equal(AuditScoreState.InvalidConfiguration, result.State);
        Assert.Null(result.Score);
    }

    private static AuditScoreInput Input(
        int applicableRuleCount,
        params AuditScoreFinding[] findings) =>
        new(AuditJobStatus.Completed, applicableRuleCount, findings);

    private static RuleFindingCandidate Candidate(
        string compactLocation,
        string property,
        string normalizedValue) => new(
            "finding.reason",
            new(property, null, normalizedValue, "", FormattingResolutionState.Resolved,
                FormattingSourceKind.DirectFormatting, null, false, null, 0, 1, null),
            new(property, [], "", null, "snapshot", "test.validation"),
            new(compactLocation, 0, 1, 1, null),
            0);
}
