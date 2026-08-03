using Ppki.DocxEngine;
using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class LayoutValidatorContractTests
{
    [Fact]
    public void Registry_resolves_supported_keys_in_stable_order_and_unknown_is_not_pass()
    {
        var registry = new DocumentRuleValidatorRegistry(LayoutValidatorTestData.Validators().Reverse());
        Assert.Equal(LayoutValidatorTestData.Validators().Select(value => value.ValidationKey).Order(StringComparer.Ordinal),
            registry.ValidationKeys);
        Assert.True(registry.TryResolve("SECTION.PAGE-SIZE-A4", out var validator));
        Assert.IsType<PageSizeA4Validator>(validator);

        var result = LayoutValidatorTestData.Engine().Validate(new ParsedDocument([], []),
            [LayoutValidatorTestData.Snapshot("unknown.validation-key")], CancellationToken.None);
        Assert.Equal(ValidationApplicability.Unsupported, Assert.Single(result.Outcomes).Result.Applicability);
        Assert.Equal("validator-key-unsupported", result.Outcomes[0].Result.DiagnosticCode);
    }

    [Fact]
    public void Registry_rejects_duplicate_keys_and_options_are_positive()
    {
        Assert.Throws<InvalidOperationException>(() => new DocumentRuleValidatorRegistry(
            [new PageSizeA4Validator(), new PageSizeA4Validator()]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentLayoutValidationEngine(
            new DocumentRuleValidatorRegistry(LayoutValidatorTestData.Validators()),
            new LayoutValidatorOptions { MaximumFindings = 0 }));
    }

    [Theory]
    [InlineData(12, "pt", 24)]
    [InlineData(11.25, "pt", 23)]
    [InlineData(24, "half-point", 24)]
    public void Point_to_half_point_rounding_is_deterministic(decimal value, string unit, int expected) =>
        Assert.Equal(expected, LayoutUnitConverter.ToHalfPoints(value, unit));

    [Theory]
    [InlineData(1, "cm", 567)]
    [InlineData(10, "mm", 567)]
    [InlineData(1, "in", 1440)]
    [InlineData(12, "pt", 240)]
    public void Length_to_twips_rounding_is_deterministic(decimal value, string unit, long expected) =>
        Assert.Equal(expected, LayoutUnitConverter.ToTwips(value, unit));

    [Fact]
    public void Unknown_unit_and_invalid_selector_are_never_treated_as_pass()
    {
        Assert.ThrowsAny<Exception>(() => LayoutUnitConverter.ToTwips(1, "pixel"));
        var document = new ParsedDocument([LayoutValidatorTestData.Section(11906, 16838, 1701, 1701, 1701, 2268)], []);
        var invalidUnit = LayoutValidatorTestData.Engine().Validate(document,
            [LayoutValidatorTestData.Snapshot("section.page-size-a4", validationJson: "{\"unit\":\"pixel\"}")], CancellationToken.None);
        Assert.Equal(ValidationApplicability.InvalidRuleConfiguration, invalidUnit.Outcomes[0].Result.Applicability);
        var selector = LayoutValidatorTestData.Engine().Validate(document,
            [LayoutValidatorTestData.Snapshot("section.page-size-a4", validationJson: "{\"selector\":\"headings\"}")], CancellationToken.None);
        Assert.Equal(ValidationApplicability.Unsupported, selector.Outcomes[0].Result.Applicability);
        var outOfRange = LayoutValidatorTestData.Engine().Validate(document,
            [LayoutValidatorTestData.Snapshot("section.page-size-a4", validationJson:
                "{\"width\":79228162514264337593543950335,\"unit\":\"in\"}")], CancellationToken.None);
        Assert.Equal(ValidationApplicability.InvalidRuleConfiguration, outOfRange.Outcomes[0].Result.Applicability);
    }

    [Fact]
    public void Tolerance_is_used_only_when_snapshot_configuration_contains_it()
    {
        var document = new ParsedDocument([LayoutValidatorTestData.Section(11907, 16838, 1701, 1701, 1701, 2268)], []);
        var strict = LayoutValidatorTestData.Engine().Validate(document,
            [LayoutValidatorTestData.Snapshot("section.page-size-a4")], CancellationToken.None);
        Assert.Single(strict.Findings);
        var tolerant = LayoutValidatorTestData.Engine().Validate(document,
            [LayoutValidatorTestData.Snapshot("section.page-size-a4", validationJson:
                "{\"tolerance\":1,\"toleranceUnit\":\"twip\"}")], CancellationToken.None);
        Assert.Empty(tolerant.Findings);
    }
}
