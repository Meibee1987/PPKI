using Ppki.DocxEngine;

namespace Ppki.RuleEngine;

public sealed class BodyFontValidator : IDocumentRuleValidator
{
    public string ValidationKey => "body.font-times-new-roman-12";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        if (!LayoutValidationSupport.TryPrepare(context.Snapshot, LayoutValidationSupport.VisibleRunSelector,
                out var configuration, out var failure)) return failure;
        using (configuration)
        try
        {
            var root = configuration.RootElement;
            var expectedFont = LayoutValidationSupport.String(root, "fontFamily", "Times New Roman");
            var expectedSize = LayoutUnitConverter.ToHalfPoints(
                LayoutValidationSupport.Decimal(root, "fontSize", 12m),
                LayoutValidationSupport.String(root, "fontSizeUnit", "pt"));
            var slots = LayoutValidationSupport.Strings(root, "fontSlots", ["ascii", "highAnsi"]);
            if (slots.Any(value => value is not ("ascii" or "highAnsi")))
                throw new LayoutRuleConfigurationException("font-slot-unsupported");

            var findings = new List<RuleFindingCandidate>();
            foreach (var paragraph in LayoutValidationSupport.NormalParagraphs(context.Document))
            foreach (var run in LayoutValidationSupport.VisibleRuns(paragraph))
            {
                if (findings.Count >= context.Options.MaximumFindings) break;
                context.CancellationToken.ThrowIfCancellationRequested();
                var formatting = run.EffectiveFormatting;
                var order = 0;
                foreach (var slot in slots)
                {
                    var value = slot == "ascii"
                        ? formatting?.FontAscii ?? MissingString("fontAscii")
                        : formatting?.FontHighAnsi ?? MissingString("fontHighAnsi");
                    if (findings.Count < context.Options.MaximumFindings && (value.State != FormattingResolutionState.Resolved
                        || !string.Equals(value.Value, expectedFont, StringComparison.OrdinalIgnoreCase))
                    )
                    {
                        findings.Add(new(
                            $"layout.font-{slot}.mismatch",
                            LayoutValidationSupport.Actual($"font.{slot}", value, "font-family", run.Location),
                            LayoutValidationSupport.Expected(context.Snapshot, $"font.{slot}", [expectedFont], "font-family"),
                            LayoutValidationSupport.Location(run.Location), order));
                    }
                    order++;
                }

                var size = formatting?.FontSizeHalfPoints ?? MissingInt("fontSizeHalfPoints");
                if (findings.Count < context.Options.MaximumFindings
                    && (size.State != FormattingResolutionState.Resolved || size.Value != expectedSize))
                {
                    findings.Add(new(
                        "layout.font-size.mismatch",
                        LayoutValidationSupport.Actual("fontSize", size, "half-point", run.Location),
                        LayoutValidationSupport.Expected(context.Snapshot, "fontSize",
                            [expectedSize.ToString(System.Globalization.CultureInfo.InvariantCulture)], "half-point"),
                        LayoutValidationSupport.Location(run.Location), order));
                }
            }
            return new(ValidationApplicability.Applicable, findings.ToArray());
        }
        catch (LayoutRuleConfigurationException exception)
        {
            return RuleValidationResult.Invalid(exception.Code);
        }
    }

    private static ResolvedFormattingValue<string?> MissingString(string property) => new(null,
        FormattingResolutionState.Unspecified, new(FormattingSourceKind.Unspecified, property));
    private static ResolvedFormattingValue<int?> MissingInt(string property) => new(null,
        FormattingResolutionState.Unspecified, new(FormattingSourceKind.Unspecified, property));
}
