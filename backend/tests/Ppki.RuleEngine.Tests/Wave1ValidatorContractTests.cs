using System.Text.Json;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class Wave1ValidatorContractTests
{
    [Fact]
    public void Structural_registry_resolves_supported_keys_rejects_duplicates_and_unknown_is_not_pass()
    {
        var validators = Wave1ValidatorTestData.StructuralValidators();
        var registry = new DocumentRuleValidatorRegistry(validators);
        Assert.Equal(16, registry.ValidationKeys.Count);
        Assert.True(registry.TryResolve("heading.chapter-bold", out var heading));
        Assert.IsType<ChapterBoldValidator>(heading);
        Assert.True(registry.TryResolve("abstract.skripsi-language-pair", out var abstractValidator));
        Assert.IsType<SkripsiAbstractLanguagePairValidator>(abstractValidator);
        Assert.Throws<InvalidOperationException>(() => new DocumentRuleValidatorRegistry(
            [new HeadingDepthValidator(), new HeadingDepthValidator()]));

        var unknown = Wave1ValidatorTestData.Engine().Validate(
            new ParsedDocument([], []),
            [Wave1ValidatorTestData.Snapshot("systematics.required-order")],
            DocumentKind.Skripsi,
            CancellationToken.None);
        Assert.Equal(ValidationApplicability.Unsupported, unknown.Outcomes[0].Result.Applicability);
        Assert.Empty(unknown.Findings);
    }

    [Fact]
    public async Task Layout_validator_golden_result_is_unchanged_with_shared_wave1_registry()
    {
        var document = await LayoutValidatorTestData.ParseFixture("minimal-invalid-layout");
        var result = Wave1ValidatorTestData.Engine().Validate(
            document, LayoutValidatorTestData.DefaultSnapshots(), DocumentKind.Skripsi, CancellationToken.None);

        Assert.Equal(11, result.Findings.Count);
        Assert.Equal([
            "pageSize", "font.ascii", "font.highAnsi", "fontSize",
            "marginLeft", "marginRight", "marginTop", "marginBottom",
            "lineSpacingValue", "firstLineIndent", "alignment"
        ], result.Findings.Select(value => value.Finding.Actual.Property));
    }

    [Fact]
    public async Task Repeat_parallel_registry_order_deduplication_and_cap_are_deterministic()
    {
        var document = Wave1ValidatorTestData.HeadingDocument(
            new HeadingSpec(4, "SENSITIVE-STRUCTURAL-MARKER"),
            new HeadingSpec(5, "SENSITIVE-STRUCTURAL-MARKER"));
        var snapshot = Wave1ValidatorTestData.Snapshot("heading.maximum-depth-3");
        var validators = LayoutValidatorTestData.Validators().Concat(Wave1ValidatorTestData.StructuralValidators()).ToArray();
        var normal = new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry(validators));
        var reverse = new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry(validators.Reverse()));
        var first = normal.Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        var second = reverse.Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        Assert.Equal(LayoutFindingCanonicalProjection.Serialize(first), LayoutFindingCanonicalProjection.Serialize(second));
        Assert.Equal(LayoutFindingCanonicalProjection.Sha256(first), LayoutFindingCanonicalProjection.Sha256(second));

        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            normal.Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None))));
        Assert.Single(parallel.Select(LayoutFindingCanonicalProjection.Sha256).Distinct(StringComparer.Ordinal));

        var duplicate = normal.Validate(document, [snapshot, snapshot], DocumentKind.Skripsi, CancellationToken.None);
        Assert.Equal(2, duplicate.Outcomes.Count);
        Assert.Equal(2, duplicate.Findings.Count);
        var capped = Wave1ValidatorTestData.Engine(1).Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        Assert.Single(capped.Findings);
        Assert.True(capped.FindingsTruncated);
    }

    [Fact]
    public void Historical_snapshot_parameters_are_isolated_from_live_rule_changes()
    {
        var document = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(4, "LEVEL FOUR"));
        var snapshot = Wave1ValidatorTestData.Snapshot("heading.maximum-depth-3",
            validationJson: "{\"maximumLevel\":4}");
        var first = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        var liveRule = new RuleDefinition
        {
            RuleCode = snapshot.RuleCode,
            Domain = "HDG",
            AppliesTo = "Semua",
            Element = "Changed live element",
            OfficialRequirement = "Changed live requirement",
            ExpectedValuePattern = "Maximum 1",
            ValidationKey = "heading.maximum-depth-1",
            IsImplemented = true
        };
        Assert.NotEqual(snapshot.ValidationKey, liveRule.ValidationKey);
        var second = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.Empty(first.Findings);
        Assert.Equal(LayoutFindingCanonicalProjection.Serialize(first), LayoutFindingCanonicalProjection.Serialize(second));
    }

    [Fact]
    public void Catalog_inventory_is_exact_and_only_supported_deterministic_rules_are_mapped()
    {
        var root = RepositoryRoot();
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "rules", "ppki-ipb-2019", "rules.json")));
        var rules = catalog.RootElement.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(18, rules.Count(value => value.GetProperty("domain").GetString() == "HDG"));
        Assert.Equal(19, rules.Count(value => value.GetProperty("domain").GetString() == "ABS"));
        Assert.Equal(25, rules.Count(value => value.GetProperty("domain").GetString() == "STR"));

        var manifest = RuleCoverageManifest.Entries;
        foreach (var supported in new[]
        {
            "PPKI-HDG-001", "PPKI-HDG-002", "PPKI-HDG-003", "PPKI-HDG-004", "PPKI-HDG-005",
            "PPKI-HDG-006", "PPKI-HDG-007", "PPKI-HDG-009", "PPKI-HDG-011", "PPKI-HDG-013",
            "PPKI-ABS-001", "PPKI-ABS-003", "PPKI-ABS-004", "PPKI-ABS-011", "PPKI-ABS-013", "PPKI-ABS-019"
        })
            Assert.Contains(manifest, value => value.RuleCode == supported
                && value.Status == RuleImplementationStatus.Implemented);
        Assert.DoesNotContain(manifest, value => value.RuleCode.StartsWith("PPKI-STR-", StringComparison.Ordinal)
            && value.Status == RuleImplementationStatus.Implemented);
        Assert.Contains(manifest, value => value.RuleCode == "PPKI-HDG-008"
            && value.Status == RuleImplementationStatus.Manual);
        Assert.DoesNotContain(manifest, value => value.RuleCode == "PPKI-ABS-002");
    }

    [Fact]
    public async Task Finding_mapper_count_matches_validation_and_fixture_remains_immutable()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-document-sections-layout");
        var checksum = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var document = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var snapshot = Wave1ValidatorTestData.Snapshot("abstract.skripsi-word-count-max-200",
            appliesTo: "Skripsi", validationJson: "{\"maximumWords\":1}", domain: "ABS");
        var validation = Wave1ValidatorTestData.Engine().Validate(
            document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        var mapped = AuditFindingMapper.Map(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), validation);

        Assert.Equal(validation.Findings.Count, mapped.Count);
        Assert.All(mapped, finding =>
        {
            Assert.DoesNotContain("ABSTRAK", finding.ActualValueJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ABSTRACT", finding.ActualValueJson, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(checksum, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
    }

    [Fact]
    public void Invalid_configuration_and_ambiguous_semantic_sections_are_not_silent_passes()
    {
        var invalid = Wave1ValidatorTestData.Engine().Validate(
            new ParsedDocument([], []),
            [Wave1ValidatorTestData.Snapshot("heading.maximum-depth-3", validationJson: "{\"maximumLevel\":0}")],
            DocumentKind.Skripsi, CancellationToken.None);
        Assert.Equal(ValidationApplicability.InvalidRuleConfiguration, invalid.Outcomes[0].Result.Applicability);

        var ambiguous = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, "BAB I",
            SemanticSectionKind.Chapter, SemanticState: SemanticClassificationState.Ambiguous));
        var chapter = Wave1ValidatorTestData.Engine().Validate(ambiguous,
            [Wave1ValidatorTestData.Snapshot("heading.chapter-bold")], DocumentKind.Skripsi, CancellationToken.None);
        Assert.Empty(chapter.Findings);
        Assert.Equal(ValidationApplicability.Unsupported, chapter.Outcomes[0].Result.Applicability);
        Assert.Equal("chapter-classification-unresolved", chapter.Outcomes[0].Result.DiagnosticCode);
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
