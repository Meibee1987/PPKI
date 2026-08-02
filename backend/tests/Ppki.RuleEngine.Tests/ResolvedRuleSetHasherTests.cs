using System.Text.RegularExpressions;
using Ppki.Domain;
using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class ResolvedRuleSetHasherTests
{
    private readonly ResolvedRuleSetHasher _hasher = new();

    [Fact]
    public void Hash_is_stable_for_equivalent_snapshots_and_input_order()
    {
        var first = Snapshot("RULE-B", ordinal: 2);
        var second = Snapshot("RULE-A", ordinal: 1);
        var equivalentFirst = Clone(first, requirementJson: "{ \"expected\": 12, \"official\": \"A4\" }");
        var equivalentSecond = Clone(second, validationJson: "{ \"tolerance\": 0.1, \"unit\": \"cm\" }");

        Assert.Equal(
            _hasher.Hash([first, second]),
            _hasher.Hash([equivalentSecond, equivalentFirst]));
    }

    [Theory]
    [InlineData("requirement")]
    [InlineData("severity")]
    [InlineData("fix-mode")]
    [InlineData("validation")]
    [InlineData("precedence")]
    public void Hash_changes_for_semantic_rule_changes(string change)
    {
        var baseline = Snapshot("RULE-A", ordinal: 1);
        var changed = change switch
        {
            "requirement" => Clone(baseline, requirementJson: "{\"official\":\"Letter\",\"expected\":12}"),
            "severity" => Clone(baseline, severity: RuleSeverity.Warning),
            "fix-mode" => Clone(baseline, fixMode: FixMode.Manual),
            "validation" => Clone(baseline, validationJson: "{\"unit\":\"cm\",\"tolerance\":0.2}"),
            "precedence" => Clone(baseline, precedence: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        Assert.NotEqual(_hasher.Hash([baseline]), _hasher.Hash([changed]));
    }

    [Fact]
    public void Hash_excludes_audit_identity_and_timestamps_and_is_lowercase_sha256()
    {
        var baseline = Snapshot("RULE-A", ordinal: 1);
        var changedRuntimeIdentity = Clone(baseline);
        changedRuntimeIdentity.Id = Guid.NewGuid();
        changedRuntimeIdentity.AuditJobId = Guid.NewGuid();
        changedRuntimeIdentity.CreatedAt = baseline.CreatedAt.AddYears(5);

        var hash = _hasher.Hash([baseline]);
        Assert.Equal(hash, _hasher.Hash([changedRuntimeIdentity]));
        Assert.Matches(new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant), hash);
    }

    [Fact]
    public void Builder_is_deterministic_and_does_not_accept_document_or_user_content()
    {
        var builder = new ResolvedRuleSetSnapshotBuilder();
        var rule = Rule("RULE-A");
        var first = builder.Build(Guid.Parse("11111111-1111-1111-1111-111111111111"), [rule], "profile", 0);
        var second = builder.Build(Guid.Parse("11111111-1111-1111-1111-111111111111"), [rule], "profile", 0);

        Assert.Single(first);
        Assert.Equal(first[0].RuleCode, second[0].RuleCode);
        Assert.Equal(first[0].Ordinal, second[0].Ordinal);
        Assert.Equal(_hasher.Hash(first), _hasher.Hash(second));
        Assert.DoesNotContain("document", first[0].RequirementJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user", first[0].RequirementJson, StringComparison.OrdinalIgnoreCase);
    }

    private static AuditRuleSnapshot Snapshot(string code, int ordinal) => new()
    {
        AuditJobId = Guid.NewGuid(),
        RuleId = Guid.NewGuid(),
        RuleCode = code,
        Domain = "Layout",
        Subdomain = null,
        AppliesTo = "Document",
        Element = "Page",
        RequirementJson = "{\"official\":\"A4\",\"expected\":12}",
        ValidationKey = "section.page-size-a4",
        ValidationJson = "{\"unit\":\"cm\",\"tolerance\":0.1}",
        Severity = RuleSeverity.Error,
        FixMode = FixMode.Report,
        SourceReferenceJson = "{\"section\":\"4.1\",\"page\":12}",
        Layer = "profile",
        Precedence = 0,
        Ordinal = ordinal,
        SnapshotSchemaVersion = 1
    };

    private static AuditRuleSnapshot Clone(
        AuditRuleSnapshot source,
        string? requirementJson = null,
        string? validationJson = null,
        RuleSeverity? severity = null,
        FixMode? fixMode = null,
        int? precedence = null) => new()
    {
        AuditJobId = source.AuditJobId,
        RuleId = source.RuleId,
        RuleCode = source.RuleCode,
        Domain = source.Domain,
        Subdomain = source.Subdomain,
        AppliesTo = source.AppliesTo,
        Element = source.Element,
        RequirementJson = requirementJson ?? source.RequirementJson,
        ValidationKey = source.ValidationKey,
        ValidationJson = validationJson ?? source.ValidationJson,
        Severity = severity ?? source.Severity,
        FixMode = fixMode ?? source.FixMode,
        SourceReferenceJson = source.SourceReferenceJson,
        Layer = source.Layer,
        Precedence = precedence ?? source.Precedence,
        Ordinal = source.Ordinal,
        SnapshotSchemaVersion = source.SnapshotSchemaVersion,
        CreatedAt = source.CreatedAt
    };

    private static RuleDefinition Rule(string code) => new()
    {
        RuleCode = code,
        Domain = "Layout",
        AppliesTo = "Document",
        Element = "Page",
        OfficialRequirement = "A4",
        ExpectedValuePattern = "21 x 29.7 cm",
        Severity = RuleSeverity.Error,
        FixMode = FixMode.Report,
        ValidationKey = "section.page-size-a4",
        IsImplemented = true
    };
}
