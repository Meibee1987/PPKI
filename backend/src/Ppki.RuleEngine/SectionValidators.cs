using Ppki.DocxEngine;

namespace Ppki.RuleEngine;

public sealed class PageSizeA4Validator : IDocumentRuleValidator
{
    public string ValidationKey => "section.page-size-a4";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        if (!LayoutValidationSupport.TryPrepare(context.Snapshot, LayoutValidationSupport.AllSections,
                out var configuration, out var failure)) return failure;
        using (configuration)
        try
        {
            var root = configuration.RootElement;
            var unit = LayoutValidationSupport.String(root, "unit", "cm");
            var width = LayoutUnitConverter.ToTwips(LayoutValidationSupport.Decimal(root, "width", 21m), unit);
            var height = LayoutUnitConverter.ToTwips(LayoutValidationSupport.Decimal(root, "height", 29.7m), unit);
            var tolerance = LayoutUnitConverter.ToTwips(LayoutValidationSupport.Decimal(root, "tolerance", 0m),
                LayoutValidationSupport.String(root, "toleranceUnit", "twip"));
            var allowLandscape = LayoutValidationSupport.Boolean(root, "allowLandscape", true);
            if (tolerance < 0) throw new LayoutRuleConfigurationException("validation-tolerance-invalid");

            var findings = new List<RuleFindingCandidate>();
            foreach (var section in context.Document.Sections.OrderBy(value => value.Index))
            {
                if (findings.Count >= context.Options.MaximumFindings) break;
                context.CancellationToken.ThrowIfCancellationRequested();
                var location = LayoutValidationSupport.SectionLocation(section);
                var effective = section.EffectiveFormatting;
                var widthValue = effective?.PageWidthTwips;
                var heightValue = effective?.PageHeightTwips;
                var resolved = widthValue?.State == FormattingResolutionState.Resolved
                    && heightValue?.State == FormattingResolutionState.Resolved
                    && widthValue.Value is not null && heightValue.Value is not null;
                var actualWidth = widthValue?.Value;
                var actualHeight = heightValue?.Value;
                var portrait = resolved && Close(actualWidth!.Value, width, tolerance) && Close(actualHeight!.Value, height, tolerance);
                var landscape = allowLandscape && resolved
                    && Close(actualWidth!.Value, height, tolerance) && Close(actualHeight!.Value, width, tolerance);
                if (portrait || landscape) continue;

                var provenance = widthValue?.Provenance ?? new FormattingProvenance(
                    FormattingSourceKind.Unspecified, "pageWidthTwips");
                findings.Add(new(
                    "layout.page-size.mismatch",
                    new(
                        "pageSize",
                        $"{LayoutValidationSupport.Invariant(actualWidth)}x{LayoutValidationSupport.Invariant(actualHeight)}",
                        $"{LayoutValidationSupport.Invariant(actualWidth)}x{LayoutValidationSupport.Invariant(actualHeight)}",
                        "twip",
                        resolved ? FormattingResolutionState.Resolved : widthValue?.State ?? FormattingResolutionState.Unspecified,
                        provenance.SourceKind,
                        provenance.SourceStyleId,
                        provenance.Inherited,
                        provenance.DiagnosticCode,
                        location.SectionIndex, null, null),
                    LayoutValidationSupport.Expected(context.Snapshot, "pageSize",
                        allowLandscape ? [$"{width}x{height}", $"{height}x{width}"] : [$"{width}x{height}"],
                        "twip", tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    LayoutValidationSupport.Location(location),
                    0));
            }
            return new(ValidationApplicability.Applicable, findings.ToArray());
        }
        catch (LayoutRuleConfigurationException exception)
        {
            return RuleValidationResult.Invalid(exception.Code);
        }
    }

    private static bool Close(long actual, long expected, long tolerance) =>
        Math.Abs((decimal)actual - expected) <= tolerance;
}

public abstract class MarginValidatorBase(
    string validationKey,
    string property,
    decimal expectedCm,
    int propertyOrder) : IDocumentRuleValidator
{
    public string ValidationKey { get; } = validationKey;

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        if (!LayoutValidationSupport.TryPrepare(context.Snapshot, LayoutValidationSupport.AllSections,
                out var configuration, out var failure)) return failure;
        using (configuration)
        try
        {
            var root = configuration.RootElement;
            var unit = LayoutValidationSupport.String(root, "unit", "cm");
            var expected = LayoutUnitConverter.ToTwips(LayoutValidationSupport.Decimal(root, "value", expectedCm), unit);
            var tolerance = LayoutUnitConverter.ToTwips(LayoutValidationSupport.Decimal(root, "tolerance", 0m),
                LayoutValidationSupport.String(root, "toleranceUnit", "twip"));
            if (tolerance < 0) throw new LayoutRuleConfigurationException("validation-tolerance-invalid");
            var findings = new List<RuleFindingCandidate>();
            foreach (var section in context.Document.Sections.OrderBy(value => value.Index))
            {
                if (findings.Count >= context.Options.MaximumFindings) break;
                context.CancellationToken.ThrowIfCancellationRequested();
                var location = LayoutValidationSupport.SectionLocation(section);
                var value = Select(section.EffectiveFormatting);
                if (!LayoutValidationSupport.Mismatch(value, expected, tolerance)) continue;
                findings.Add(new(
                    $"layout.{property}.mismatch",
                    LayoutValidationSupport.Actual(property, value, "twip", location),
                    LayoutValidationSupport.Expected(context.Snapshot, property,
                        [expected.ToString(System.Globalization.CultureInfo.InvariantCulture)], "twip",
                        tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    LayoutValidationSupport.Location(location),
                    propertyOrder));
            }
            return new(ValidationApplicability.Applicable, findings.ToArray());
        }
        catch (LayoutRuleConfigurationException exception)
        {
            return RuleValidationResult.Invalid(exception.Code);
        }
    }

    private ResolvedFormattingValue<long?> Select(EffectiveSectionFormatting? formatting) => property switch
    {
        "marginLeft" => formatting?.MarginLeftTwips ?? Missing("marginLeftTwips"),
        "marginRight" => formatting?.MarginRightTwips ?? Missing("marginRightTwips"),
        "marginTop" => formatting?.MarginTopTwips ?? Missing("marginTopTwips"),
        "marginBottom" => formatting?.MarginBottomTwips ?? Missing("marginBottomTwips"),
        _ => throw new InvalidOperationException("Unsupported margin property.")
    };

    private static ResolvedFormattingValue<long?> Missing(string propertyName) => new(
        null,
        FormattingResolutionState.Unspecified,
        new(FormattingSourceKind.Unspecified, propertyName));
}

public sealed class MarginLeftValidator() : MarginValidatorBase("section.margin-left-4cm", "marginLeft", 4m, 0);
public sealed class MarginRightValidator() : MarginValidatorBase("section.margin-right-3cm", "marginRight", 3m, 1);
public sealed class MarginTopValidator() : MarginValidatorBase("section.margin-top-3cm", "marginTop", 3m, 2);
public sealed class MarginBottomValidator() : MarginValidatorBase("section.margin-bottom-3cm", "marginBottom", 3m, 3);
