using System.Globalization;
using System.Text.Json;
using Ppki.DocxEngine;

namespace Ppki.RuleEngine;

public sealed class HeadingDepthValidator : IDocumentRuleValidator
{
    public string ValidationKey => "heading.maximum-depth-3";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        try
        {
            var maximumLevel = StructuralValidationSupport.Integer(configuration, "maximumLevel", 3);
            if (maximumLevel is < 1 or > 9) return RuleValidationResult.Invalid("heading-depth-invalid");
            var findings = StructuralValidationSupport.Headings(context, configuration)
                .Where(value => value.Heading.Level > maximumLevel)
                .Take(context.Options.MaximumFindings == int.MaxValue
                    ? int.MaxValue : context.Options.MaximumFindings + 1)
                .Select(value => HeadingFinding(
                    context, value.Heading, value.Paragraph, "headingLevel",
                    value.Heading.Level.ToString(CultureInfo.InvariantCulture),
                    [$"1..{maximumLevel}"], "level", 0, "heading-depth-exceeded"))
                .ToArray();
            return RuleValidationResult.Applicable(findings);
        }
        catch (LayoutRuleConfigurationException exception)
        {
            return RuleValidationResult.Invalid(exception.Code);
        }
    }

    internal static RuleFindingCandidate HeadingFinding(
        RuleValidationContext context,
        ParsedHeading heading,
        ParsedParagraph paragraph,
        string property,
        string? actual,
        IReadOnlyList<string> expected,
        string unit,
        int order,
        string messageKey,
        FormattingResolutionState state = FormattingResolutionState.Resolved,
        FormattingSourceKind sourceKind = FormattingSourceKind.Unspecified,
        string? sourceStyleId = null,
        bool inherited = false,
        string? diagnosticCode = null) => new(
            messageKey,
            StructuralValidationSupport.Actual(property, actual, actual, unit, heading.Location,
                state, sourceKind, sourceStyleId, inherited, diagnosticCode),
            LayoutValidationSupport.Expected(context.Snapshot, property, expected, unit),
            LayoutValidationSupport.Location(heading.Location),
            order);
}

public sealed class ChapterNumberingValidator : IDocumentRuleValidator
{
    public string ValidationKey => "heading.chapter-number-upper-roman-no-period";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        if (StructuralValidationSupport.HasUnresolvedChapterClassification(context.Document))
            return RuleValidationResult.Unsupported("chapter-classification-unresolved");
        IReadOnlyList<(ParsedHeading Heading, ParsedParagraph Paragraph)> headings;
        try { headings = StructuralValidationSupport.Headings(context, configuration); }
        catch (LayoutRuleConfigurationException exception) { return RuleValidationResult.Invalid(exception.Code); }

        var chapterIndexes = StructuralValidationSupport.ConfirmedChapterHeadingIndexes(context.Document);
        var findings = new List<RuleFindingCandidate>();
        foreach (var value in headings.Where(item => chapterIndexes.Contains(item.Heading.Index)))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var numbering = value.Heading.Numbering ?? value.Paragraph.EffectiveNumbering;
            var label = numbering?.Label;
            var format = label?.State == NumberingResolutionState.Resolved
                ? label.Format.ToString() : "unresolved";
            if (label?.State != NumberingResolutionState.Resolved || label.Format != ParsedNumberingFormat.UpperRoman)
                findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                    "numberingFormat", format, [ParsedNumberingFormat.UpperRoman.ToString()], "enum", 0,
                    "heading-numbering-format-mismatch", diagnosticCode: numbering?.DiagnosticCode ?? label?.DiagnosticCode));

            var trailingPeriod = label?.State == NumberingResolutionState.Resolved && label.Value?.EndsWith(".", StringComparison.Ordinal) == true;
            if (label?.State != NumberingResolutionState.Resolved || trailingPeriod)
                findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                    "numberingTrailingPeriod", trailingPeriod ? "true" : label?.State == NumberingResolutionState.Resolved ? "false" : "unresolved",
                    ["false"], "boolean", 1, "heading-numbering-trailing-period"));
            if (findings.Count >= context.Options.MaximumFindings) break;
        }
        return RuleValidationResult.Applicable(findings.Take(context.Options.MaximumFindings).ToArray());
    }
}

public sealed class ChapterUppercaseValidator : IDocumentRuleValidator
{
    public string ValidationKey => "heading.chapter-uppercase";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        if (StructuralValidationSupport.HasUnresolvedChapterClassification(context.Document))
            return RuleValidationResult.Unsupported("chapter-classification-unresolved");
        IReadOnlyList<(ParsedHeading Heading, ParsedParagraph Paragraph)> headings;
        try { headings = StructuralValidationSupport.Headings(context, configuration); }
        catch (LayoutRuleConfigurationException exception) { return RuleValidationResult.Invalid(exception.Code); }
        var chapterIndexes = StructuralValidationSupport.ConfirmedChapterHeadingIndexes(context.Document);
        var findings = new List<RuleFindingCandidate>();
        foreach (var value in headings.Where(item => chapterIndexes.Contains(item.Heading.Index)))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var inspection = StructuralValidationSupport.InspectHeadingText(value.Paragraph);
            if (inspection.LimitExceeded) return RuleValidationResult.Unsupported("heading-text-limit-exceeded");
            if (!inspection.HasVisibleText || !inspection.IsUppercase)
                findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                    inspection.HasVisibleText ? "uppercase" : "visibleText",
                    inspection.HasVisibleText ? inspection.IsUppercase.ToString().ToLowerInvariant() : "false",
                    ["true"], "boolean", 0, inspection.HasVisibleText
                        ? "heading-uppercase-mismatch" : "heading-empty"));
            if (findings.Count >= context.Options.MaximumFindings) break;
        }
        return RuleValidationResult.Applicable(findings.ToArray());
    }
}

public sealed class ChapterBoldValidator : HeadingRunFormattingValidator
{
    public ChapterBoldValidator() : base("heading.chapter-bold", HeadingScope.Chapter, expectedBold: true,
        checkPunctuation: false, checkUnderline: false) { }
}

public sealed class ChapterDecorationValidator : HeadingRunFormattingValidator
{
    public ChapterDecorationValidator() : base("heading.chapter-no-period-no-underline", HeadingScope.Chapter,
        expectedBold: null, checkPunctuation: true, checkUnderline: true) { }
}

public sealed class ChapterAlignmentValidator : HeadingAlignmentValidator
{
    public ChapterAlignmentValidator() : base("heading.chapter-centered", HeadingScope.Chapter, ParsedAlignment.Center) { }
}

public sealed class SubheadingNumberingAlignmentValidator : HeadingNumberingAlignmentValidator
{
    public SubheadingNumberingAlignmentValidator() : base("heading.subheading-decimal-left", 2) { }
}

public sealed class SubheadingDecorationValidator : HeadingRunFormattingValidator
{
    public SubheadingDecorationValidator() : base("heading.subheading-bold-no-period-no-underline", HeadingScope.Level2,
        expectedBold: true, checkPunctuation: true, checkUnderline: true) { }
}

public sealed class SubSubheadingNumberingAlignmentValidator : HeadingNumberingAlignmentValidator
{
    public SubSubheadingNumberingAlignmentValidator() : base("heading.subsubheading-decimal-left", 3) { }
}

public sealed class SubSubheadingDecorationValidator : HeadingRunFormattingValidator
{
    public SubSubheadingDecorationValidator() : base("heading.subsubheading-regular-no-period-no-underline", HeadingScope.Level3,
        expectedBold: false, checkPunctuation: true, checkUnderline: true) { }
}

public abstract class HeadingAlignmentValidator(
    string validationKey,
    HeadingScope scope,
    ParsedAlignment expectedAlignment) : IDocumentRuleValidator
{
    public string ValidationKey { get; } = validationKey;

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        if (scope == HeadingScope.Chapter && StructuralValidationSupport.HasUnresolvedChapterClassification(context.Document))
            return RuleValidationResult.Unsupported("chapter-classification-unresolved");
        try
        {
            var findings = HeadingScopes.Select(context, configuration, scope)
                .Where(value => value.Paragraph.EffectiveFormatting?.Alignment.State != FormattingResolutionState.Resolved
                    || value.Paragraph.EffectiveFormatting.Alignment.Value != expectedAlignment)
                .Take(context.Options.MaximumFindings)
                .Select(value =>
                {
                    var actual = value.Paragraph.EffectiveFormatting?.Alignment;
                    return HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                        "alignment", LayoutValidationSupport.Invariant(actual?.Value), [expectedAlignment.ToString()], "enum", 0,
                        "heading-alignment-mismatch", actual?.State ?? FormattingResolutionState.Unspecified,
                        actual?.Provenance.SourceKind ?? FormattingSourceKind.Unspecified,
                        actual?.Provenance.SourceStyleId, actual?.Provenance.Inherited ?? false,
                        actual?.Provenance.DiagnosticCode);
                }).ToArray();
            return RuleValidationResult.Applicable(findings);
        }
        catch (LayoutRuleConfigurationException exception) { return RuleValidationResult.Invalid(exception.Code); }
    }
}

public abstract class HeadingNumberingAlignmentValidator(string validationKey, int level) : IDocumentRuleValidator
{
    public string ValidationKey { get; } = validationKey;

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        IReadOnlyList<(ParsedHeading Heading, ParsedParagraph Paragraph)> headings;
        try { headings = HeadingScopes.Select(context, configuration, level == 2 ? HeadingScope.Level2 : HeadingScope.Level3); }
        catch (LayoutRuleConfigurationException exception) { return RuleValidationResult.Invalid(exception.Code); }
        var findings = new List<RuleFindingCandidate>();
        foreach (var value in headings)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var label = (value.Heading.Numbering ?? value.Paragraph.EffectiveNumbering)?.Label;
            var validLabel = label?.State == NumberingResolutionState.Resolved
                && IsArabicHierarchy(label.Value, level);
            if (!validLabel)
                findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                    "numberingPattern", NumberingCategory(label, level), [$"arabic-dotted-level-{level}"], "category", 0,
                    "heading-numbering-pattern-mismatch", diagnosticCode: label?.DiagnosticCode));
            var alignment = value.Paragraph.EffectiveFormatting?.Alignment;
            if (alignment?.State != FormattingResolutionState.Resolved || alignment.Value != ParsedAlignment.Left)
                findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                    "alignment", LayoutValidationSupport.Invariant(alignment?.Value), [ParsedAlignment.Left.ToString()], "enum", 1,
                    "heading-alignment-mismatch", alignment?.State ?? FormattingResolutionState.Unspecified,
                    alignment?.Provenance.SourceKind ?? FormattingSourceKind.Unspecified,
                    alignment?.Provenance.SourceStyleId, alignment?.Provenance.Inherited ?? false,
                    alignment?.Provenance.DiagnosticCode));
            if (findings.Count >= context.Options.MaximumFindings) break;
        }
        return RuleValidationResult.Applicable(findings.Take(context.Options.MaximumFindings).ToArray());
    }

    private static bool IsArabicHierarchy(string? label, int level)
    {
        if (string.IsNullOrEmpty(label) || label.EndsWith(".", StringComparison.Ordinal)) return false;
        var components = label.Split('.', StringSplitOptions.None);
        return components.Length == level && components.All(component => component.Length > 0
            && component.All(character => character is >= '0' and <= '9'));
    }

    private static string NumberingCategory(ResolvedNumberingLabel? label, int level)
    {
        if (label?.State != NumberingResolutionState.Resolved) return "unresolved";
        if (label.Value?.EndsWith(".", StringComparison.Ordinal) == true) return "trailing-period";
        return IsArabicHierarchy(label.Value, level) ? $"arabic-dotted-level-{level}" : "other";
    }
}

public abstract class HeadingRunFormattingValidator(
    string validationKey,
    HeadingScope scope,
    bool? expectedBold,
    bool checkPunctuation,
    bool checkUnderline) : IDocumentRuleValidator
{
    public string ValidationKey { get; } = validationKey;

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        if (scope == HeadingScope.Chapter && StructuralValidationSupport.HasUnresolvedChapterClassification(context.Document))
            return RuleValidationResult.Unsupported("chapter-classification-unresolved");
        IReadOnlyList<(ParsedHeading Heading, ParsedParagraph Paragraph)> headings;
        try { headings = HeadingScopes.Select(context, configuration, scope); }
        catch (LayoutRuleConfigurationException exception) { return RuleValidationResult.Invalid(exception.Code); }
        var findings = new List<RuleFindingCandidate>();
        foreach (var value in headings)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var runs = StructuralValidationSupport.VisibleRuns(value.Paragraph);
            if (expectedBold is not null)
            {
                var bold = BooleanCategory(runs.Select(run => run.EffectiveFormatting?.Bold).ToArray());
                if (bold != expectedBold.Value.ToString().ToLowerInvariant())
                {
                    var provenance = runs.Select(run => run.EffectiveFormatting?.Bold).FirstOrDefault(item => item is not null);
                    findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                        "bold", bold, [expectedBold.Value.ToString().ToLowerInvariant()], "category", 0,
                        "heading-bold-mismatch", provenance?.State ?? FormattingResolutionState.Unspecified,
                        provenance?.Provenance.SourceKind ?? FormattingSourceKind.Unspecified,
                        provenance?.Provenance.SourceStyleId, provenance?.Provenance.Inherited ?? false,
                        provenance?.Provenance.DiagnosticCode));
                }
            }

            if (checkUnderline)
            {
                var underline = UnderlineCategory(runs);
                if (underline != "none")
                    findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                        "underline", underline, ["none"], "category", 1, "heading-underline-not-allowed"));
            }

            if (checkPunctuation)
            {
                var inspection = StructuralValidationSupport.InspectHeadingText(value.Paragraph);
                if (inspection.LimitExceeded) return RuleValidationResult.Unsupported("heading-text-limit-exceeded");
                if (inspection.TrailingPunctuation == TrailingPunctuationCategory.Period)
                    findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                        "trailingPunctuation", "period", ["not-period"], "category", 2,
                        "heading-trailing-period"));
                else if (!inspection.HasVisibleText)
                    findings.Add(HeadingDepthValidator.HeadingFinding(context, value.Heading, value.Paragraph,
                        "visibleText", "false", ["true"], "boolean", 2, "heading-empty"));
            }
            if (findings.Count >= context.Options.MaximumFindings) break;
        }
        return RuleValidationResult.Applicable(findings.Take(context.Options.MaximumFindings).ToArray());
    }

    private static string BooleanCategory(IReadOnlyList<ResolvedFormattingValue<bool?>?> values)
    {
        if (values.Count == 0) return "empty";
        if (values.Any(value => value?.State != FormattingResolutionState.Resolved || value.Value is null)) return "unresolved";
        var distinct = values.Select(value => value!.Value!.Value).Distinct().ToArray();
        return distinct.Length == 1 ? distinct[0].ToString().ToLowerInvariant() : "mixed";
    }

    private static string UnderlineCategory(IReadOnlyList<ParsedRun> runs)
    {
        if (runs.Count == 0) return "none";
        var values = runs.Select(run => run.EffectiveFormatting?.Underline.Value).ToArray();
        if (values.All(value => string.IsNullOrEmpty(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase)))
            return "none";
        var normalized = values.Select(value => string.IsNullOrEmpty(value) ? "none" : "present")
            .Distinct(StringComparer.Ordinal).ToArray();
        return normalized.Length == 1 ? normalized[0] : "mixed";
    }
}

public enum HeadingScope { Chapter, Level2, Level3 }

internal static class HeadingScopes
{
    public static IReadOnlyList<(ParsedHeading Heading, ParsedParagraph Paragraph)> Select(
        RuleValidationContext context,
        JsonElement configuration,
        HeadingScope scope)
    {
        var headings = StructuralValidationSupport.Headings(context, configuration);
        if (scope == HeadingScope.Chapter)
        {
            var chapters = StructuralValidationSupport.ConfirmedChapterHeadingIndexes(context.Document);
            return headings.Where(value => chapters.Contains(value.Heading.Index)).ToArray();
        }
        var level = scope == HeadingScope.Level2 ? 2 : 3;
        return headings.Where(value => value.Heading.Level == level).ToArray();
    }
}
