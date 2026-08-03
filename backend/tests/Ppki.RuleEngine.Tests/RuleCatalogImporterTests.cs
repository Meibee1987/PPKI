using Ppki.Domain;
using Ppki.Infrastructure;
using System.Reflection;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class RuleCatalogImporterTests
{
    [Fact]
    public void Existing_manual_rules_receive_current_layout_heading_and_abstract_mappings()
    {
        var layout = Rule("PPKI-LAY-003");
        var heading = Rule("PPKI-HDG-001");
        var abstractRule = Rule("PPKI-ABS-001");
        heading.Severity = RuleSeverity.Warning;
        heading.FixMode = FixMode.Confirm;
        heading.SourceSection = "Synthetic source";

        var changed = Reconcile([layout, heading, abstractRule]);

        Assert.Equal(3, changed);
        AssertMapping(layout, "section.page-size-a4");
        AssertMapping(heading, "heading.chapter-number-upper-roman-no-period");
        AssertMapping(abstractRule, "abstract.skripsi-language-pair");
        Assert.Equal(RuleSeverity.Warning, heading.Severity);
        Assert.Equal(FixMode.Confirm, heading.FixMode);
        Assert.Equal("Synthetic source", heading.SourceSection);
        Assert.Equal("Synthetic requirement", heading.OfficialRequirement);
    }

    [Fact]
    public void Reconciliation_is_idempotent_and_matches_rule_codes_case_insensitively()
    {
        var rule = Rule("ppki-hdg-002");

        Assert.Equal(1, Reconcile([rule]));
        AssertMapping(rule, "heading.maximum-depth-3");
        Assert.Equal(0, Reconcile([rule]));
    }

    [Fact]
    public void Unmapped_existing_rules_and_other_metadata_are_not_modified()
    {
        var unmapped = Rule("PPKI-STR-001");
        unmapped.ValidationKey = "custom.reviewed-validator";
        unmapped.IsImplemented = true;
        var originalRequirement = unmapped.OfficialRequirement;

        var changed = Reconcile([unmapped]);

        Assert.Equal(0, changed);
        Assert.Equal("custom.reviewed-validator", unmapped.ValidationKey);
        Assert.True(unmapped.IsImplemented);
        Assert.Equal(originalRequirement, unmapped.OfficialRequirement);
    }

    private static RuleDefinition Rule(string code) => new()
    {
        RuleCode = code,
        Domain = "Synthetic",
        AppliesTo = "Semua",
        Element = "Synthetic element",
        OfficialRequirement = "Synthetic requirement",
        ExpectedValuePattern = "Synthetic expected value",
        ValidationKey = "manual.not-implemented",
        IsImplemented = false
    };

    private static int Reconcile(IEnumerable<RuleDefinition> rules)
    {
        var method = typeof(RuleCatalogImporter).GetMethod(
            "ReconcileImplementedMappings",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<int>(method.Invoke(null, [rules]));
    }

    private static void AssertMapping(RuleDefinition rule, string validationKey)
    {
        Assert.True(rule.IsImplemented);
        Assert.Equal(validationKey, rule.ValidationKey);
    }
}
