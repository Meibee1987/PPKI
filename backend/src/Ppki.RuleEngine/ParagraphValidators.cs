using Ppki.DocxEngine;

namespace Ppki.RuleEngine;

public sealed class LineSpacingValidator : IDocumentRuleValidator
{
    public string ValidationKey => "body.line-spacing-single";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        if (!LayoutValidationSupport.TryPrepare(context.Snapshot, LayoutValidationSupport.NormalBodyParagraphs,
                out var configuration, out var failure)) return failure;
        using (configuration)
        try
        {
            var root = configuration.RootElement;
            var expectedValue = LayoutUnitConverter.ToTwips(
                LayoutValidationSupport.Decimal(root, "value", 240m),
                LayoutValidationSupport.String(root, "unit", "twip"));
            var expectedRule = LayoutValidationSupport.String(root, "rule", "auto").ToLowerInvariant();
            var findings = new List<RuleFindingCandidate>();
            foreach (var paragraph in LayoutValidationSupport.NormalParagraphs(context.Document))
            {
                if (findings.Count >= context.Options.MaximumFindings) break;
                context.CancellationToken.ThrowIfCancellationRequested();
                var formatting = paragraph.EffectiveFormatting;
                var value = formatting?.LineSpacingValue ?? MissingLong("lineSpacingValue");
                var rule = formatting?.LineSpacingRule ?? MissingString("lineSpacingRule");
                var location = paragraph.Location!;
                if (LayoutValidationSupport.Mismatch(value, expectedValue))
                    findings.Add(Finding(context, location, "lineSpacingValue", value,
                        expectedValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "twip", 0));
                if (findings.Count < context.Options.MaximumFindings && (rule.State != FormattingResolutionState.Resolved
                    || !string.Equals(rule.Value, expectedRule, StringComparison.OrdinalIgnoreCase))
                )
                    findings.Add(Finding(context, location, "lineSpacingRule", rule, expectedRule, "enum", 1));
            }
            return new(ValidationApplicability.Applicable, findings.ToArray());
        }
        catch (LayoutRuleConfigurationException exception)
        {
            return RuleValidationResult.Invalid(exception.Code);
        }
    }

    private static RuleFindingCandidate Finding<T>(RuleValidationContext context, DocumentElementLocation location,
        string property, ResolvedFormattingValue<T> value, string expected, string unit, int order) => new(
        $"layout.{property}.mismatch",
        LayoutValidationSupport.Actual(property, value, unit, location),
        LayoutValidationSupport.Expected(context.Snapshot, property, [expected], unit),
        LayoutValidationSupport.Location(location), order);

    private static ResolvedFormattingValue<long?> MissingLong(string property) => new(null,
        FormattingResolutionState.Unspecified, new(FormattingSourceKind.Unspecified, property));
    private static ResolvedFormattingValue<string?> MissingString(string property) => new(null,
        FormattingResolutionState.Unspecified, new(FormattingSourceKind.Unspecified, property));
}

public sealed class FirstLineIndentValidator : IDocumentRuleValidator
{
    public string ValidationKey => "body.first-line-indent-1cm";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        if (!LayoutValidationSupport.TryPrepare(context.Snapshot, LayoutValidationSupport.NormalBodyParagraphs,
                out var configuration, out var failure)) return failure;
        using (configuration)
        try
        {
            var root = configuration.RootElement;
            var expected = LayoutUnitConverter.ToTwips(LayoutValidationSupport.Decimal(root, "value", 1m),
                LayoutValidationSupport.String(root, "unit", "cm"));
            var findings = new List<RuleFindingCandidate>();
            foreach (var paragraph in LayoutValidationSupport.NormalParagraphs(context.Document))
            {
                if (findings.Count >= context.Options.MaximumFindings) break;
                context.CancellationToken.ThrowIfCancellationRequested();
                var location = paragraph.Location!;
                var value = paragraph.EffectiveFormatting?.FirstLineIndentTwips ?? new(null,
                    FormattingResolutionState.Unspecified,
                    new(FormattingSourceKind.Unspecified, "firstLineIndentTwips"));
                if (!LayoutValidationSupport.Mismatch(value, expected)) continue;
                findings.Add(new(
                    "layout.first-line-indent.mismatch",
                    LayoutValidationSupport.Actual("firstLineIndent", value, "twip", location),
                    LayoutValidationSupport.Expected(context.Snapshot, "firstLineIndent",
                        [expected.ToString(System.Globalization.CultureInfo.InvariantCulture)], "twip"),
                    LayoutValidationSupport.Location(location), 0));
            }
            return new(ValidationApplicability.Applicable, findings.ToArray());
        }
        catch (LayoutRuleConfigurationException exception)
        {
            return RuleValidationResult.Invalid(exception.Code);
        }
    }
}

public sealed class JustifiedValidator : IDocumentRuleValidator
{
    public string ValidationKey => "body.justified";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        if (!LayoutValidationSupport.TryPrepare(context.Snapshot, LayoutValidationSupport.NormalBodyParagraphs,
                out var configuration, out var failure)) return failure;
        using (configuration)
        try
        {
            var accepted = LayoutValidationSupport.Strings(configuration.RootElement, "accepted",
                [ParsedAlignment.Justified.ToString()]);
            var findings = new List<RuleFindingCandidate>();
            foreach (var paragraph in LayoutValidationSupport.NormalParagraphs(context.Document))
            {
                if (findings.Count >= context.Options.MaximumFindings) break;
                context.CancellationToken.ThrowIfCancellationRequested();
                var location = paragraph.Location!;
                var value = paragraph.EffectiveFormatting?.Alignment ?? new(null,
                    FormattingResolutionState.Unspecified,
                    new(FormattingSourceKind.Unspecified, "alignment"));
                if (value.State == FormattingResolutionState.Resolved && value.Value is not null
                    && accepted.Contains(value.Value.Value.ToString(), StringComparer.OrdinalIgnoreCase)) continue;
                findings.Add(new(
                    "layout.alignment.mismatch",
                    LayoutValidationSupport.Actual("alignment", value, "enum", location),
                    LayoutValidationSupport.Expected(context.Snapshot, "alignment", accepted, "enum"),
                    LayoutValidationSupport.Location(location), 0));
            }
            return new(ValidationApplicability.Applicable, findings.ToArray());
        }
        catch (LayoutRuleConfigurationException exception)
        {
            return RuleValidationResult.Invalid(exception.Code);
        }
    }
}
