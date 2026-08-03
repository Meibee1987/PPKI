using System.Globalization;
using System.Text.Json;
using Ppki.DocxEngine;

namespace Ppki.RuleEngine;

public sealed class SkripsiAbstractLanguagePairValidator : SemanticLanguagePairValidator
{
    public SkripsiAbstractLanguagePairValidator() : base(
        "abstract.skripsi-language-pair",
        [SemanticSectionKind.AbstractIndonesian, SemanticSectionKind.AbstractEnglish]) { }
}

public sealed class ThesisSummaryLanguagePairValidator : SemanticLanguagePairValidator
{
    public ThesisSummaryLanguagePairValidator() : base(
        "summary.thesis-dissertation-language-pair",
        [SemanticSectionKind.SummaryIndonesian, SemanticSectionKind.SummaryEnglish]) { }
}

public sealed class SkripsiAbstractParagraphCountValidator : IDocumentRuleValidator
{
    public string ValidationKey => "abstract.skripsi-narrative-paragraph-count-one";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        try
        {
            var expected = StructuralValidationSupport.Integer(configuration, "paragraphCount", 1);
            if (expected < 0) return RuleValidationResult.Invalid("paragraph-count-invalid");
            var findings = new List<RuleFindingCandidate>();
            foreach (var descriptor in AbstractScopes.Select(context.Document, AbstractScope.Abstract))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var count = StructuralValidationSupport.NarrativeParagraphs(context.Document, descriptor).Count;
                if (count != expected)
                    findings.Add(AbstractFinding(context, descriptor, "narrativeParagraphCount", count,
                        expected, "count", 0, "abstract-paragraph-count-mismatch"));
                if (findings.Count >= context.Options.MaximumFindings) break;
            }
            return RuleValidationResult.Applicable(findings.ToArray());
        }
        catch (LayoutRuleConfigurationException exception) { return RuleValidationResult.Invalid(exception.Code); }
    }

    internal static RuleFindingCandidate AbstractFinding(
        RuleValidationContext context,
        AbstractSectionDescriptor descriptor,
        string property,
        int actual,
        int expected,
        string unit,
        int order,
        string messageKey) => new(
            messageKey,
            StructuralValidationSupport.Actual(property,
                actual.ToString(CultureInfo.InvariantCulture),
                actual.ToString(CultureInfo.InvariantCulture), unit, descriptor.HeadingLocation),
            LayoutValidationSupport.Expected(context.Snapshot, property,
                [expected.ToString(CultureInfo.InvariantCulture)], unit),
            LayoutValidationSupport.Location(descriptor.HeadingLocation),
            order);
}

public sealed class SkripsiAbstractWordCountValidator : IDocumentRuleValidator
{
    public string ValidationKey => "abstract.skripsi-word-count-max-200";

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        try
        {
            var maximum = StructuralValidationSupport.Integer(configuration, "maximumWords", 200);
            if (maximum <= 0) return RuleValidationResult.Invalid("word-count-limit-invalid");
            var findings = new List<RuleFindingCandidate>();
            foreach (var descriptor in AbstractScopes.Select(context.Document, AbstractScope.Abstract))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var paragraphs = StructuralValidationSupport.NarrativeParagraphs(context.Document, descriptor);
                int count;
                try { count = StructuralValidationSupport.CountWords(paragraphs, context.CancellationToken); }
                catch (StructuralValidationLimitException exception)
                {
                    return RuleValidationResult.Unsupported(exception.Code);
                }
                if (count > maximum)
                    findings.Add(new RuleFindingCandidate(
                        "abstract-word-count-exceeded",
                        StructuralValidationSupport.Actual("wordCount", count.ToString(CultureInfo.InvariantCulture),
                            count.ToString(CultureInfo.InvariantCulture), "word", descriptor.HeadingLocation),
                        LayoutValidationSupport.Expected(context.Snapshot, "wordCount", [$"0..{maximum}"], "word"),
                        LayoutValidationSupport.Location(descriptor.HeadingLocation), 0));
                if (findings.Count >= context.Options.MaximumFindings) break;
            }
            return RuleValidationResult.Applicable(findings.ToArray());
        }
        catch (LayoutRuleConfigurationException exception) { return RuleValidationResult.Invalid(exception.Code); }
    }
}

public sealed class SkripsiAbstractSpacingValidator : SemanticSectionSpacingValidator
{
    public SkripsiAbstractSpacingValidator() : base(
        "abstract.skripsi-single-spacing-zero-paragraph-spacing", AbstractScope.Abstract) { }
}

public sealed class AbstractSummarySpacingValidator : SemanticSectionSpacingValidator
{
    public AbstractSummarySpacingValidator() : base(
        "abstract-summary-single-spacing-zero-paragraph-spacing", AbstractScope.All) { }
}

public abstract class SemanticLanguagePairValidator(
    string validationKey,
    IReadOnlyList<SemanticSectionKind> requiredKinds) : IDocumentRuleValidator
{
    public string ValidationKey { get; } = validationKey;

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out _);
        if (applicability is not null) return applicability;
        var findings = new List<RuleFindingCandidate>();
        foreach (var kind in requiredKinds)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var count = context.Document.DocumentStructure.Sections.Count(section =>
                section.Kind == kind && section.ClassificationState == SemanticClassificationState.Confirmed);
            if (count == 0)
            {
                var location = StructuralValidationSupport.DocumentLocation();
                var property = $"sectionPresence.{kind}";
                findings.Add(new RuleFindingCandidate(
                    "semantic-section-required",
                    StructuralValidationSupport.Actual(property, "0", "absent", "presence",
                        new DocumentElementLocation(DocumentPartKind.MainDocument, "/word/document.xml")),
                    LayoutValidationSupport.Expected(context.Snapshot, property, ["present"], "presence"),
                    location,
                    Array.IndexOf(requiredKinds.ToArray(), kind)));
            }
        }
        return RuleValidationResult.Applicable(findings.Take(context.Options.MaximumFindings).ToArray());
    }
}

public abstract class SemanticSectionSpacingValidator(string validationKey, AbstractScope scope) : IDocumentRuleValidator
{
    public string ValidationKey { get; } = validationKey;

    public RuleValidationResult Validate(RuleValidationContext context)
    {
        var applicability = StructuralValidationSupport.CheckApplicability(context, out var configuration);
        if (applicability is not null) return applicability;
        try
        {
            var lineValue = LayoutValidationSupport.Decimal(configuration, "lineSpacingTwips", 240m);
            var before = LayoutValidationSupport.Decimal(configuration, "spacingBeforeTwips", 0m);
            var after = LayoutValidationSupport.Decimal(configuration, "spacingAfterTwips", 0m);
            if (lineValue < 0 || before < 0 || after < 0)
                return RuleValidationResult.Invalid("spacing-parameter-invalid");
            var findings = new List<RuleFindingCandidate>();
            foreach (var descriptor in AbstractScopes.Select(context.Document, scope))
            {
                foreach (var paragraph in StructuralValidationSupport.NarrativeParagraphs(context.Document, descriptor))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    AddMismatch(findings, context, paragraph, "lineSpacingValue",
                        paragraph.EffectiveFormatting?.LineSpacingValue, decimal.ToInt64(lineValue), 0);
                    var rule = paragraph.EffectiveFormatting?.LineSpacingRule;
                    if (rule?.State != FormattingResolutionState.Resolved
                        || !string.Equals(rule.Value, "auto", StringComparison.OrdinalIgnoreCase))
                        findings.Add(FormattingFinding(context, paragraph, "lineSpacingRule", rule, "auto", "enum", 1));
                    AddMismatch(findings, context, paragraph, "spacingBeforeTwips",
                        paragraph.EffectiveFormatting?.SpacingBeforeTwips, decimal.ToInt64(before), 2);
                    AddMismatch(findings, context, paragraph, "spacingAfterTwips",
                        paragraph.EffectiveFormatting?.SpacingAfterTwips, decimal.ToInt64(after), 3);
                    if (findings.Count >= context.Options.MaximumFindings) break;
                }
                if (findings.Count >= context.Options.MaximumFindings) break;
            }
            return RuleValidationResult.Applicable(findings.Take(context.Options.MaximumFindings).ToArray());
        }
        catch (LayoutRuleConfigurationException exception) { return RuleValidationResult.Invalid(exception.Code); }
        catch (OverflowException) { return RuleValidationResult.Invalid("validation-parameter-out-of-range"); }
    }

    private static void AddMismatch(
        ICollection<RuleFindingCandidate> findings,
        RuleValidationContext context,
        ParsedParagraph paragraph,
        string property,
        ResolvedFormattingValue<long?>? value,
        long expected,
        int order)
    {
        if (value?.State != FormattingResolutionState.Resolved || value.Value != expected)
            findings.Add(FormattingFinding(context, paragraph, property, value,
                expected.ToString(CultureInfo.InvariantCulture), "twip", order));
    }

    private static RuleFindingCandidate FormattingFinding<T>(
        RuleValidationContext context,
        ParsedParagraph paragraph,
        string property,
        ResolvedFormattingValue<T>? value,
        string expected,
        string unit,
        int order)
    {
        var location = paragraph.Location ?? new DocumentElementLocation(
            DocumentPartKind.MainDocument, "/word/document.xml",
            ParagraphIndex: paragraph.Index, ElementKind: DocumentElementKind.Paragraph);
        var actual = value is null ? null : LayoutValidationSupport.Invariant(value.Value);
        return new(
            "semantic-section-spacing-mismatch",
            StructuralValidationSupport.Actual(property, actual,
                actual, unit, location,
                value?.State ?? FormattingResolutionState.Unspecified,
                value?.Provenance.SourceKind ?? FormattingSourceKind.Unspecified,
                value?.Provenance.SourceStyleId,
                value?.Provenance.Inherited ?? false,
                value?.Provenance.DiagnosticCode),
            LayoutValidationSupport.Expected(context.Snapshot, property, [expected], unit),
            LayoutValidationSupport.Location(location),
            order);
    }
}

public enum AbstractScope { Abstract, Summary, All }

internal static class AbstractScopes
{
    public static IReadOnlyList<AbstractSectionDescriptor> Select(ParsedDocument document, AbstractScope scope) =>
        document.DocumentStructure.AbstractSections
            .Where(value => scope == AbstractScope.All
                || scope == AbstractScope.Abstract && value.Kind is SemanticSectionKind.AbstractIndonesian or SemanticSectionKind.AbstractEnglish
                || scope == AbstractScope.Summary && value.Kind is SemanticSectionKind.SummaryIndonesian or SemanticSectionKind.SummaryEnglish)
            .OrderBy(value => value.HeadingLocation.BodyElementIndex)
            .ThenBy(value => value.SectionIndex)
            .ToArray();
}
