using System.Text.Json;
using Ppki.Application;
using Ppki.FixEngine;
using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class RuleCoverageManifestTests
{
    [Fact]
    public void Current_manifest_passes_the_production_quality_gate()
    {
        Validate(RuleCoverageManifest.Entries);
        Assert.True(RuleCoverageManifest.Entries.Count >= RuleCoverageQualityGate.MinimumTargetRuleCount);
        Assert.Equal(25, RuleCoverageManifest.Entries.Count(value => value.Status == RuleImplementationStatus.Implemented));
        Assert.Equal(9, RuleCoverageManifest.Entries.Count(value => value.Status == RuleImplementationStatus.Manual));
    }

    [Fact]
    public void Duplicate_rule_code_is_rejected()
    {
        var entries = RuleCoverageManifest.Entries.Append(RuleCoverageManifest.Entries[0]).ToArray();
        Assert.Contains("duplicate RuleCode", Assert.Throws<InvalidOperationException>(() => Validate(entries)).Message);
    }

    [Fact]
    public void Rule_missing_from_catalog_is_rejected()
    {
        var catalog = CatalogRuleCodes().Where(value => value != RuleCoverageManifest.Entries[0].RuleCode);
        Assert.Contains("absent from the authoritative catalog",
            Assert.Throws<InvalidOperationException>(() => Validate(RuleCoverageManifest.Entries, catalog)).Message);
    }

    [Fact]
    public void Implemented_rule_without_validation_key_is_rejected()
    {
        var entries = ReplaceFirst(value => value with { ValidationKey = null });
        Assert.Contains("must declare a ValidationKey", Assert.Throws<InvalidOperationException>(() => Validate(entries)).Message);
    }

    [Fact]
    public void Unknown_implemented_validation_key_is_rejected()
    {
        var entries = ReplaceFirst(value => value with { ValidationKey = "unknown.validator" });
        Assert.Contains("unregistered ValidationKey", Assert.Throws<InvalidOperationException>(() => Validate(entries)).Message);
    }

    [Fact]
    public void Missing_implementation_version_is_rejected()
    {
        var entries = ReplaceFirst(value => value with { ImplementationVersion = null });
        Assert.Contains("implementation version", Assert.Throws<InvalidOperationException>(() => Validate(entries)).Message);
    }

    [Fact]
    public void Missing_test_coverage_is_rejected()
    {
        var entries = ReplaceFirst(value => value with { TestCoverage = Array.Empty<string>() });
        Assert.Contains("test coverage metadata", Assert.Throws<InvalidOperationException>(() => Validate(entries)).Message);
    }

    [Fact]
    public void Capability_metadata_must_match_the_production_registry()
    {
        var index = RuleCoverageManifest.Entries.ToList().FindIndex(value => value.CapabilityId is not null);
        var entries = RuleCoverageManifest.Entries.ToArray();
        entries[index] = entries[index] with { CapabilityVersion = "9.9" };
        Assert.Contains("does not match the production registry",
            Assert.Throws<InvalidOperationException>(() => Validate(entries)).Message);
    }

    [Fact]
    public void Duplicate_validation_key_is_allowed_for_distinct_catalog_rules()
    {
        var entries = RuleCoverageManifest.Entries.ToArray();
        var first = entries.First(value => value.Status == RuleImplementationStatus.Implemented && value.CapabilityId is null);
        var secondIndex = Array.FindIndex(entries, value => value.Status == RuleImplementationStatus.Implemented
            && value.CapabilityId is null && value.RuleCode != first.RuleCode);
        entries[secondIndex] = entries[secondIndex] with { ValidationKey = first.ValidationKey };
        Validate(entries);
    }

    [Fact]
    public void Test_coverage_identifiers_resolve_to_real_test_classes()
    {
        var testTypes = typeof(RuleCoverageManifestTests).Assembly.GetTypes().Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        var identifiers = RuleCoverageManifest.Entries.Where(value => value.Status == RuleImplementationStatus.Implemented)
            .SelectMany(value => value.TestCoverage).Distinct(StringComparer.Ordinal);
        Assert.All(identifiers, value => Assert.Contains(value, testTypes));
    }

    [Fact]
    public void Generated_document_is_synchronized_and_deterministic()
    {
        var expected = RuleCoverageDocumentation.Render(RuleCoverageManifest.Entries);
        Assert.Equal(expected, RuleCoverageDocumentation.Render(RuleCoverageManifest.Entries.Reverse()));
        var actual = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "RULE_COVERAGE_MVP.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    private static RuleCoverageEntry[] ReplaceFirst(Func<RuleCoverageEntry, RuleCoverageEntry> replace)
    {
        var entries = RuleCoverageManifest.Entries.ToArray();
        var index = Array.FindIndex(entries, value => value.Status == RuleImplementationStatus.Implemented);
        entries[index] = replace(entries[index]);
        return entries;
    }

    private static void Validate(IEnumerable<RuleCoverageEntry> entries, IEnumerable<string>? catalog = null)
    {
        var validators = new DocumentRuleValidatorRegistry(ProductionDocumentValidators.Create());
        var capabilities = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities
            .Select(value => new RuleCoverageCapability(value.ValidationKey, value.CapabilityId, value.CapabilityVersion));
        RuleCoverageQualityGate.Validate(entries, catalog ?? CatalogRuleCodes(), validators.ValidationKeys, capabilities);
    }

    private static IReadOnlyList<string> CatalogRuleCodes()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "rules", "ppki-ipb-2019", "rules.json")));
        return catalog.RootElement.GetProperty("rules").EnumerateArray()
            .Select(value => value.GetProperty("rule_id").GetString()!).ToArray();
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
