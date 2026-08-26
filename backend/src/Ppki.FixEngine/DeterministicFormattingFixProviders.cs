using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.FixEngine;

internal static class FormattingFixSnapshot
{
    public static bool Common(FixPlanFindingSnapshot finding, string validationKey, params string[] ruleCodes) =>
        finding.ValidationKey == validationKey && ruleCodes.Contains(finding.RuleCode, StringComparer.Ordinal)
        && finding.FixMode == FixMode.Auto && finding.FindingState == FindingStatus.Open;

    public static bool TryRead(FixPlanFindingSnapshot finding, out JsonDocument actual, out JsonDocument expected,
        out JsonDocument location)
    {
        actual = expected = location = null!;
        try
        {
            actual = JsonDocument.Parse(finding.ActualJson);
            expected = JsonDocument.Parse(finding.ExpectedJson);
            location = JsonDocument.Parse(finding.LocationJson);
            return true;
        }
        catch (JsonException)
        {
            actual?.Dispose(); expected?.Dispose(); location?.Dispose();
            return false;
        }
    }

    public static string Text(JsonElement root, string name)
    {
        var property = root.EnumerateObject().FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : string.Empty;
    }

    public static bool Integer(JsonElement root, string name, out int value)
    {
        value = 0;
        var property = root.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out value);
    }

    public static bool SingleExpected(JsonElement root, string property, string validationKey, out string value)
    {
        value = string.Empty;
        if (Text(root, "property") != property || Text(root, "validationKey") != validationKey) return false;
        var accepted = root.EnumerateObject().FirstOrDefault(item => item.Name.Equals("acceptedValues", StringComparison.OrdinalIgnoreCase));
        if (accepted.Value.ValueKind != JsonValueKind.Array) return false;
        var values = accepted.Value.EnumerateArray().ToArray();
        if (values.Length != 1 || values[0].ValueKind != JsonValueKind.String) return false;
        value = values[0].GetString() ?? string.Empty;
        return value.Length is > 0 and <= 128;
    }

    public static bool ParagraphLocation(JsonElement root, out int section, out int body, out int paragraph)
    {
        section = body = paragraph = -1;
        if (!Integer(root, "sectionIndex", out section)
            || !Integer(root, "bodyElementIndex", out body)
            || !Integer(root, "paragraphIndex", out paragraph)
            || section < 0 || body < 0 || paragraph < 0) return false;
        return string.Equals(Text(root, "compactLocation"),
            $"maindocument/s:{section}/b:{body}/p:{paragraph}/kind:paragraph",
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool RunLocation(JsonElement root, out int section, out int body, out int paragraph, out int run)
    {
        section = body = paragraph = run = -1;
        if (!Integer(root, "sectionIndex", out section)
            || !Integer(root, "bodyElementIndex", out body)
            || !Integer(root, "paragraphIndex", out paragraph)
            || !Integer(root, "runIndex", out run)
            || section < 0 || body < 0 || paragraph < 0 || run < 0) return false;
        return string.Equals(Text(root, "compactLocation"),
            $"maindocument/s:{section}/b:{body}/p:{paragraph}/r:{run}/kind:run",
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool ActualMatches(JsonElement actual, string? current) =>
        string.Equals(Text(actual, "normalizedValue"), current ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    public static ParsedParagraph Paragraph(FixApplyContext context)
    {
        var target = context.Operation.Target;
        return context.SourceDocument.Paragraphs.SingleOrDefault(value =>
            value.Location?.PartKind == DocumentPartKind.MainDocument
            && value.Location.BodyElementIndex == target.BodyElementIndex
            && value.Location.ParagraphIndex == target.ParagraphIndex
            && (target.SectionIndex is null || value.Location.SectionIndex == target.SectionIndex))
            ?? throw new FixExecutionException("fix-operation-target-precondition-failed");
    }

    public static ParsedParagraph NormalBodyParagraph(FixApplyContext context)
    {
        var paragraph = Paragraph(context);
        if (paragraph.IsInTable || paragraph.IsHeading
            || context.SourceDocument.Headings.Any(value => value.ParagraphIndex == paragraph.Index)
            || !paragraph.RunList.Any(value => !value.IsDeleted && !value.IsHidden
                && value.EffectiveFormatting?.Hidden.Value != true
                && value.TextSegments.Any(segment => !string.IsNullOrWhiteSpace(segment))))
            throw new FixExecutionException("fix-operation-target-precondition-failed");
        return paragraph;
    }

    public static (ParsedHeading Heading, ParsedParagraph Paragraph, IReadOnlyList<ParsedRun> VisibleRuns)
        Heading(FixApplyContext context, int level, bool chapter)
    {
        var paragraph = Paragraph(context);
        var target = context.Operation.Target;
        var heading = context.SourceDocument.Headings.SingleOrDefault(value =>
            value.ParagraphIndex == paragraph.Index
            && value.Location.PartKind == DocumentPartKind.MainDocument
            && value.Location.SectionIndex == target.SectionIndex
            && value.Location.BodyElementIndex == target.BodyElementIndex
            && value.Location.ParagraphIndex == target.ParagraphIndex)
            ?? throw new FixExecutionException("fix-operation-target-precondition-failed");
        if (paragraph.IsInTable || heading.Classification != HeadingClassification.Confirmed
            || heading.Level != level)
            throw new FixExecutionException("fix-operation-target-precondition-failed");
        if (chapter)
        {
            if (context.SourceDocument.DocumentStructure.Sections.Any(value =>
                    value.Kind == SemanticSectionKind.Chapter
                    && value.ClassificationState != SemanticClassificationState.Confirmed)
                || !context.SourceDocument.DocumentStructure.Sections.Any(value =>
                    value.Kind == SemanticSectionKind.Chapter
                    && value.ClassificationState == SemanticClassificationState.Confirmed
                    && value.HeadingIndex == heading.Index))
                throw new FixExecutionException("fix-operation-target-precondition-failed");
        }
        var visibleRuns = paragraph.RunList
            .Where(value => !value.IsDeleted && !value.IsHidden
                && value.EffectiveFormatting?.Hidden.Value != true
                && value.TextSegments.Any(segment => !string.IsNullOrEmpty(segment)))
            .OrderBy(value => value.Index).ToArray();
        if (visibleRuns.Length == 0) throw new FixExecutionException("fix-operation-target-precondition-failed");
        return (heading, paragraph, visibleRuns);
    }

    public static Paragraph XmlParagraph(FixApplyContext context, Body body)
    {
        var element = body.Elements().ElementAtOrDefault(context.Operation.Target.BodyElementIndex!.Value);
        return element as Paragraph ?? throw new FixExecutionException("fix-operation-target-precondition-failed");
    }

    public static FixApplyOutcome Mutate(FixApplyContext context, Func<Body, FixApplyOutcome> mutation)
    {
        using var owned = context.OpenPackage is null
            ? WordprocessingDocument.Open(context.WorkingFilePath, true, new OpenSettings { AutoSave = false }) : null;
        var package = context.OpenPackage ?? owned!;
        var document = package.MainDocumentPart?.Document ?? throw new FixExecutionException("fix-operation-document-missing");
        var body = document.Body ?? throw new FixExecutionException("fix-operation-body-missing");
        var outcome = mutation(body);
        if (owned is not null && outcome == FixApplyOutcome.Changed) document.Save();
        return outcome;
    }

    public static void ExactContract(FixApplyContext context, IFixApplyProvider provider, FixOperationDraft approved,
        string validationKey, params string[] ruleCodes)
    {
        var operation = context.Operation;
        if (approved.Target != operation.Target) throw new FixExecutionException("fix-operation-target-precondition-failed");
        if (approved.PropertyIdentifier != operation.PropertyIdentifier || approved.Expected != operation.Expected
            || operation.CapabilityId != provider.CapabilityId || operation.CapabilityVersion != provider.CapabilityVersion
            || operation.ValidationKey != validationKey || !ruleCodes.Contains(operation.RuleCode, StringComparer.Ordinal)
            || operation.OperationKind != FixOperationKind.SetProperty)
            throw new FixExecutionException("fix-operation-contract-invalid");
    }
}

public sealed class BodyFontFixProvider : IFixPreviewProvider, IFixApplyProvider
{
    public const string Id = "body-font-direct-run";
    public const string Version = "1.0";
    public string CapabilityId => Id;
    public string CapabilityVersion => Version;
    public IReadOnlySet<string> ValidationKeys { get; } = new HashSet<string>(["body.font-times-new-roman-12"], StringComparer.Ordinal);

    public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
    {
        operation = null!; diagnosticCode = "fix-preview-provider-rejected-snapshot";
        if (!FormattingFixSnapshot.Common(finding, "body.font-times-new-roman-12", "PPKI-LAY-005")
            || !FormattingFixSnapshot.TryRead(finding, out var actual, out var expected, out var location)) return false;
        using (actual) using (expected) using (location)
        {
            var property = FormattingFixSnapshot.Text(actual.RootElement, "property");
            if (!FormattingFixSnapshot.RunLocation(location.RootElement, out var section, out var body, out var paragraph, out var run)
                || !FormattingFixSnapshot.SingleExpected(expected.RootElement, property, finding.ValidationKey, out var value)) return false;
            if (property is "font.ascii" or "font.highAnsi")
                operation = new(new("main-document-run", body, section, paragraph, run),
                    property == "font.ascii" ? "run.font-family-ascii" : "run.font-family-high-ansi",
                    new("string-code", value), "source-finding-snapshot-must-match", "set-run-font-family");
            else if (property == "fontSize" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var halfPoints)
                && halfPoints is > 0 and <= 3276)
                operation = new(new("main-document-run", body, section, paragraph, run), "run.font-size",
                    new("half-points", halfPoints.ToString(CultureInfo.InvariantCulture)), "source-finding-snapshot-must-match", "set-run-font-size");
            else return false;
            diagnosticCode = "fix-operation-planned";
            return true;
        }
    }

    public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCreate(context.Finding, out var approved, out _)) throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        FormattingFixSnapshot.ExactContract(context, this, approved, "body.font-times-new-roman-12", "PPKI-LAY-005");
        var (parsedParagraph, parsedRun) = EligibleTarget(context);
        var outcome = FormattingFixSnapshot.Mutate(context, body =>
        {
            var paragraph = FormattingFixSnapshot.XmlParagraph(context, body);
            var run = paragraph.Descendants<Run>().ElementAtOrDefault(context.Operation.Target.RunIndex!.Value)
                ?? throw new FixExecutionException("fix-operation-target-precondition-failed");
            if (context.Operation.PropertyIdentifier is "run.font-family-ascii" or "run.font-family-high-ansi")
            {
                var wanted = context.Operation.Expected.Value;
                var fonts = run.RunProperties?.RunFonts;
                using var actual = JsonDocument.Parse(context.Finding.ActualJson);
                var property = FormattingFixSnapshot.Text(actual.RootElement, "property");
                var ascii = context.Operation.PropertyIdentifier == "run.font-family-ascii";
                var direct = ascii ? fonts?.Ascii?.Value : fonts?.HighAnsi?.Value;
                if (string.Equals(direct, wanted, StringComparison.OrdinalIgnoreCase)) return FixApplyOutcome.NoChange;
                var current = property == "font.ascii" ? parsedRun.EffectiveFormatting?.FontAscii.Value : parsedRun.EffectiveFormatting?.FontHighAnsi.Value;
                if (!FormattingFixSnapshot.ActualMatches(actual.RootElement, current)) throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
                run.RunProperties ??= new RunProperties();
                run.RunProperties.RunFonts ??= new RunFonts();
                if (ascii) run.RunProperties.RunFonts.Ascii = wanted;
                else run.RunProperties.RunFonts.HighAnsi = wanted;
            }
            else
            {
                var wanted = int.Parse(context.Operation.Expected.Value, CultureInfo.InvariantCulture);
                if (run.RunProperties?.FontSize?.Val?.Value == wanted.ToString(CultureInfo.InvariantCulture)) return FixApplyOutcome.NoChange;
                using var actual = JsonDocument.Parse(context.Finding.ActualJson);
                if (!FormattingFixSnapshot.ActualMatches(actual.RootElement,
                    parsedRun.EffectiveFormatting?.FontSizeHalfPoints.Value?.ToString(CultureInfo.InvariantCulture)))
                    throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
                run.RunProperties ??= new RunProperties();
                run.RunProperties.FontSize = new FontSize { Val = wanted.ToString(CultureInfo.InvariantCulture) };
            }
            return FixApplyOutcome.Changed;
        });
        return Task.FromResult(outcome);
    }

    private static (ParsedParagraph Paragraph, ParsedRun Run) EligibleTarget(FixApplyContext context)
    {
        var target = context.Operation.Target;
        var paragraph = FormattingFixSnapshot.Paragraph(context);
        if (paragraph.IsInTable || paragraph.IsHeading
            || context.SourceDocument.Headings.Any(value => value.ParagraphIndex == paragraph.Index))
            throw new FixExecutionException("fix-operation-target-precondition-failed");
        var run = paragraph.RunList.SingleOrDefault(value => value.Index == target.RunIndex
            && value.Location.PartKind == DocumentPartKind.MainDocument
            && value.Location.SectionIndex == target.SectionIndex
            && value.Location.BodyElementIndex == target.BodyElementIndex
            && value.Location.ParagraphIndex == target.ParagraphIndex)
            ?? throw new FixExecutionException("fix-operation-target-precondition-failed");
        if (run.IsDeleted || run.IsHidden || run.EffectiveFormatting?.Hidden.Value == true
            || !run.TextSegments.Any(segment => !string.IsNullOrWhiteSpace(segment)))
            throw new FixExecutionException("fix-operation-target-precondition-failed");
        return (paragraph, run);
    }
}

public abstract class ParagraphPropertyFixProvider : IFixPreviewProvider, IFixApplyProvider
{
    public abstract string CapabilityId { get; }
    public string CapabilityVersion => "1.0";
    protected abstract IReadOnlyDictionary<string, string[]> Contracts { get; }
    public IReadOnlySet<string> ValidationKeys => Contracts.Keys.ToHashSet(StringComparer.Ordinal);
    protected abstract IReadOnlySet<string> Properties { get; }

    public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
    {
        operation = null!; diagnosticCode = "fix-preview-provider-rejected-snapshot";
        if (!Contracts.TryGetValue(finding.ValidationKey, out var rules)
            || !FormattingFixSnapshot.Common(finding, finding.ValidationKey, rules)
            || !FormattingFixSnapshot.TryRead(finding, out var actual, out var expected, out var location)) return false;
        using (actual) using (expected) using (location)
        {
            var property = FormattingFixSnapshot.Text(actual.RootElement, "property");
            if (!Properties.Contains(property)
                || !FormattingFixSnapshot.ParagraphLocation(location.RootElement, out var section, out var body, out var paragraph)
                || !FormattingFixSnapshot.SingleExpected(expected.RootElement, property, finding.ValidationKey, out var value)) return false;
            var descriptor = Descriptor(property, value);
            if (descriptor is null) return false;
            operation = new(new("main-document-paragraph", body, section, paragraph, null), descriptor.Value.Property,
                descriptor.Value.Expected, "source-finding-snapshot-must-match", descriptor.Value.Summary);
            diagnosticCode = "fix-operation-planned";
            return true;
        }
    }

    public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCreate(context.Finding, out var approved, out _)) throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        FormattingFixSnapshot.ExactContract(context, this, approved, context.Finding.ValidationKey, Contracts[context.Finding.ValidationKey]);
        var parsed = RequiresNormalBodyParagraph
            ? FormattingFixSnapshot.NormalBodyParagraph(context)
            : FormattingFixSnapshot.Paragraph(context);
        using var actual = JsonDocument.Parse(context.Finding.ActualJson);
        var property = FormattingFixSnapshot.Text(actual.RootElement, "property");
        var outcome = FormattingFixSnapshot.Mutate(context, body => Mutate(context, parsed, property, actual.RootElement,
            FormattingFixSnapshot.XmlParagraph(context, body)));
        return Task.FromResult(outcome);
    }

    protected abstract (string Property, FixExpectedValueDescriptor Expected, string Summary)? Descriptor(string property, string value);
    protected virtual bool RequiresNormalBodyParagraph => false;
    protected abstract FixApplyOutcome Mutate(FixApplyContext context, ParsedParagraph parsed, string property,
        JsonElement actual, Paragraph paragraph);
}

public sealed class BodyLineSpacingFixProvider : ParagraphPropertyFixProvider
{
    public const string Id = "body-line-spacing-direct-paragraph";
    public override string CapabilityId => Id;
    protected override bool RequiresNormalBodyParagraph => true;
    protected override IReadOnlyDictionary<string, string[]> Contracts { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
        { ["body.line-spacing-single"] = ["PPKI-LAY-017"] };
    protected override IReadOnlySet<string> Properties { get; } = new HashSet<string>(["lineSpacingValue", "lineSpacingRule"], StringComparer.Ordinal);
    protected override (string, FixExpectedValueDescriptor, string)? Descriptor(string property, string value)
    {
        if (property == "lineSpacingValue" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var twips) && twips >= 0)
            return ("paragraph.line-spacing-value", new("twips", twips.ToString(CultureInfo.InvariantCulture)), "set-paragraph-line-spacing-value");
        if (property == "lineSpacingRule" && value is "auto" or "exact" or "atleast")
            return ("paragraph.line-spacing-rule", new("enum-code", value), "set-paragraph-line-spacing-rule");
        return null;
    }
    protected override FixApplyOutcome Mutate(FixApplyContext context, ParsedParagraph parsed, string property, JsonElement actual, Paragraph paragraph)
    {
        paragraph.ParagraphProperties ??= new ParagraphProperties();
        paragraph.ParagraphProperties.SpacingBetweenLines ??= new SpacingBetweenLines();
        var spacing = paragraph.ParagraphProperties.SpacingBetweenLines;
        if (property == "lineSpacingValue")
        {
            var wanted = context.Operation.Expected.Value;
            if (spacing.Line?.Value == wanted) return FixApplyOutcome.NoChange;
            if (!FormattingFixSnapshot.ActualMatches(actual, parsed.EffectiveFormatting?.LineSpacingValue.Value?.ToString(CultureInfo.InvariantCulture)))
                throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
            spacing.Line = wanted;
        }
        else
        {
            var wanted = context.Operation.Expected.Value;
            var wantedRule = wanted switch
            {
                "auto" => LineSpacingRuleValues.Auto,
                "exact" => LineSpacingRuleValues.Exact,
                _ => LineSpacingRuleValues.AtLeast
            };
            if (spacing.LineRule?.Value == wantedRule) return FixApplyOutcome.NoChange;
            if (!FormattingFixSnapshot.ActualMatches(actual, parsed.EffectiveFormatting?.LineSpacingRule.Value))
                throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
            spacing.LineRule = wantedRule;
        }
        return FixApplyOutcome.Changed;
    }
}

public sealed class AbstractParagraphSpacingFixProvider : ParagraphPropertyFixProvider
{
    public const string Id = "abstract-spacing-direct-paragraph";
    public override string CapabilityId => Id;
    protected override IReadOnlyDictionary<string, string[]> Contracts { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["abstract.skripsi-single-spacing-zero-paragraph-spacing"] = ["PPKI-ABS-011"],
        ["abstract-summary-single-spacing-zero-paragraph-spacing"] = ["PPKI-ABS-019"]
    };
    protected override IReadOnlySet<string> Properties { get; } = new HashSet<string>(
        ["lineSpacingValue", "lineSpacingRule", "spacingBeforeTwips", "spacingAfterTwips"], StringComparer.Ordinal);
    protected override (string, FixExpectedValueDescriptor, string)? Descriptor(string property, string value)
    {
        if (property == "lineSpacingRule" && value == "auto")
            return ("paragraph.line-spacing-rule", new("enum-code", value), "set-abstract-line-spacing-rule");
        if (property is "lineSpacingValue" or "spacingBeforeTwips" or "spacingAfterTwips"
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var twips) && twips >= 0)
            return (property switch { "lineSpacingValue" => "paragraph.line-spacing-value", "spacingBeforeTwips" => "paragraph.spacing-before", _ => "paragraph.spacing-after" },
                new("twips", twips.ToString(CultureInfo.InvariantCulture)), "set-abstract-paragraph-spacing");
        return null;
    }
    protected override FixApplyOutcome Mutate(FixApplyContext context, ParsedParagraph parsed, string property, JsonElement actual, Paragraph paragraph)
    {
        paragraph.ParagraphProperties ??= new ParagraphProperties();
        paragraph.ParagraphProperties.SpacingBetweenLines ??= new SpacingBetweenLines();
        var spacing = paragraph.ParagraphProperties.SpacingBetweenLines;
        var wanted = context.Operation.Expected.Value;
        string? current;
        if (property == "lineSpacingRule")
        {
            if (spacing.LineRule?.Value == LineSpacingRuleValues.Auto) return FixApplyOutcome.NoChange;
            current = parsed.EffectiveFormatting?.LineSpacingRule.Value;
            if (!FormattingFixSnapshot.ActualMatches(actual, current)) throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
            spacing.LineRule = LineSpacingRuleValues.Auto;
        }
        else
        {
            var direct = property switch { "lineSpacingValue" => spacing.Line?.Value, "spacingBeforeTwips" => spacing.Before?.Value, _ => spacing.After?.Value };
            if (direct == wanted) return FixApplyOutcome.NoChange;
            current = property switch
            {
                "lineSpacingValue" => parsed.EffectiveFormatting?.LineSpacingValue.Value?.ToString(CultureInfo.InvariantCulture),
                "spacingBeforeTwips" => parsed.EffectiveFormatting?.SpacingBeforeTwips.Value?.ToString(CultureInfo.InvariantCulture),
                _ => parsed.EffectiveFormatting?.SpacingAfterTwips.Value?.ToString(CultureInfo.InvariantCulture)
            };
            if (!FormattingFixSnapshot.ActualMatches(actual, current)) throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
            if (property == "lineSpacingValue") spacing.Line = wanted;
            else if (property == "spacingBeforeTwips") spacing.Before = wanted;
            else spacing.After = wanted;
        }
        return FixApplyOutcome.Changed;
    }
}

public sealed class BodyFirstLineIndentFixProvider : ParagraphPropertyFixProvider
{
    public const string Id = "body-first-line-indent-direct-paragraph";
    public override string CapabilityId => Id;
    protected override bool RequiresNormalBodyParagraph => true;
    protected override IReadOnlyDictionary<string, string[]> Contracts { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
        { ["body.first-line-indent-1cm"] = ["PPKI-LAY-018"] };
    protected override IReadOnlySet<string> Properties { get; } = new HashSet<string>(["firstLineIndent"], StringComparer.Ordinal);
    protected override (string, FixExpectedValueDescriptor, string)? Descriptor(string property, string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var twips) && twips >= 0
            ? ("paragraph.first-line-indent", new("twips", twips.ToString(CultureInfo.InvariantCulture)), "set-paragraph-first-line-indent") : null;
    protected override FixApplyOutcome Mutate(FixApplyContext context, ParsedParagraph parsed, string property, JsonElement actual, Paragraph paragraph)
    {
        var wanted = context.Operation.Expected.Value;
        var indentation = paragraph.ParagraphProperties?.Indentation;
        if (indentation?.Hanging is not null || parsed.EffectiveFormatting?.HangingIndentTwips.Value is not null)
            throw new FixExecutionException("fix-operation-indent-semantics-unsupported");
        if (indentation?.FirstLine?.Value == wanted) return FixApplyOutcome.NoChange;
        if (!FormattingFixSnapshot.ActualMatches(actual, parsed.EffectiveFormatting?.FirstLineIndentTwips.Value?.ToString(CultureInfo.InvariantCulture)))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        paragraph.ParagraphProperties ??= new ParagraphProperties();
        paragraph.ParagraphProperties.Indentation ??= new Indentation();
        paragraph.ParagraphProperties.Indentation.FirstLine = wanted;
        return FixApplyOutcome.Changed;
    }
}

public abstract class HeadingAlignmentFixProvider : ParagraphPropertyFixProvider
{
    private readonly string validationKey;
    private readonly string ruleCode;
    private readonly int level;
    private readonly bool chapter;
    private readonly string expectedValue;
    private readonly JustificationValues justification;

    protected HeadingAlignmentFixProvider(string validationKey, string ruleCode, int level, bool chapter,
        string expectedValue, JustificationValues justification)
    {
        this.validationKey = validationKey;
        this.ruleCode = ruleCode;
        this.level = level;
        this.chapter = chapter;
        this.expectedValue = expectedValue;
        this.justification = justification;
        Contracts = new Dictionary<string, string[]>(StringComparer.Ordinal) { [validationKey] = [ruleCode] };
    }

    protected override IReadOnlyDictionary<string, string[]> Contracts { get; }
    protected override IReadOnlySet<string> Properties { get; } = new HashSet<string>(["alignment"], StringComparer.Ordinal);
    protected override (string, FixExpectedValueDescriptor, string)? Descriptor(string property, string value) =>
        string.Equals(value, expectedValue, StringComparison.Ordinal)
            ? ("heading.alignment", new("enum-code", expectedValue.ToLowerInvariant()), "set-heading-alignment") : null;
    protected override FixApplyOutcome Mutate(FixApplyContext context, ParsedParagraph parsed, string property, JsonElement actual, Paragraph paragraph)
    {
        _ = FormattingFixSnapshot.Heading(context, level, chapter);
        if (paragraph.ParagraphProperties?.Justification?.Val?.Value == justification
            && parsed.EffectiveFormatting?.Alignment.Value?.ToString() == expectedValue)
            return FixApplyOutcome.NoChange;
        if (!FormattingFixSnapshot.ActualMatches(actual, parsed.EffectiveFormatting?.Alignment.Value?.ToString()))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        paragraph.ParagraphProperties ??= new ParagraphProperties();
        paragraph.ParagraphProperties.Justification = new Justification { Val = justification };
        return FixApplyOutcome.Changed;
    }
}

public sealed class ChapterCenteredFixProvider : HeadingAlignmentFixProvider
{
    public const string Id = "chapter-centered-direct-paragraph";
    public override string CapabilityId => Id;
    public ChapterCenteredFixProvider() : base("heading.chapter-centered", "PPKI-HDG-006", 1, true,
        "Center", JustificationValues.Center) { }
}

public sealed class SubheadingLeftFixProvider : HeadingAlignmentFixProvider
{
    public const string Id = "subheading-left-direct-paragraph";
    public override string CapabilityId => Id;
    public SubheadingLeftFixProvider() : base("heading.subheading-decimal-left", "PPKI-HDG-007", 2, false,
        "Left", JustificationValues.Left) { }
}

public sealed class SubSubheadingLeftFixProvider : HeadingAlignmentFixProvider
{
    public const string Id = "subsubheading-left-direct-paragraph";
    public override string CapabilityId => Id;
    public SubSubheadingLeftFixProvider() : base("heading.subsubheading-decimal-left", "PPKI-HDG-011", 3, false,
        "Left", JustificationValues.Left) { }
}

public abstract class HeadingRunFormattingFixProvider : IFixPreviewProvider, IFixApplyProvider
{
    private readonly string validationKey;
    private readonly string ruleCode;
    private readonly int level;
    private readonly bool chapter;
    private readonly IReadOnlyDictionary<string, bool?> properties;

    protected HeadingRunFormattingFixProvider(string validationKey, string ruleCode, int level, bool chapter,
        IReadOnlyDictionary<string, bool?> properties)
    {
        this.validationKey = validationKey;
        this.ruleCode = ruleCode;
        this.level = level;
        this.chapter = chapter;
        this.properties = properties;
        ValidationKeys = new HashSet<string>([validationKey], StringComparer.Ordinal);
    }

    public abstract string CapabilityId { get; }
    public string CapabilityVersion => "1.0";
    public IReadOnlySet<string> ValidationKeys { get; }

    public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
    {
        operation = null!;
        diagnosticCode = "fix-preview-provider-rejected-snapshot";
        if (!FormattingFixSnapshot.Common(finding, validationKey, ruleCode)
            || !FormattingFixSnapshot.TryRead(finding, out var actual, out var expected, out var location)) return false;
        using (actual) using (expected) using (location)
        {
            var property = FormattingFixSnapshot.Text(actual.RootElement, "property");
            if (!properties.TryGetValue(property, out var wantedBold)
                || !FormattingFixSnapshot.ParagraphLocation(location.RootElement, out var section, out var body, out var paragraph)
                || !FormattingFixSnapshot.SingleExpected(expected.RootElement, property, validationKey, out var value)) return false;
            if (property == "bold")
            {
                var wanted = wantedBold == true ? "true" : "false";
                if (value != wanted) return false;
                operation = new(new("main-document-paragraph", body, section, paragraph, null), "heading.runs-bold",
                    new("boolean", wanted), "source-finding-snapshot-must-match", "set-heading-runs-bold");
            }
            else if (property == "underline")
            {
                if (wantedBold is not null || value != "none") return false;
                operation = new(new("main-document-paragraph", body, section, paragraph, null), "heading.runs-underline",
                    new("enum-code", "none"), "source-finding-snapshot-must-match", "set-heading-runs-underline-none");
            }
            else return false;
            diagnosticCode = "fix-operation-planned";
            return true;
        }
    }

    public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCreate(context.Finding, out var approved, out _))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        FormattingFixSnapshot.ExactContract(context, this, approved, validationKey, ruleCode);
        var (_, _, visibleRuns) = FormattingFixSnapshot.Heading(context, level, chapter);
        using var actual = JsonDocument.Parse(context.Finding.ActualJson);
        var property = FormattingFixSnapshot.Text(actual.RootElement, "property");
        var current = property == "bold" ? BoldCategory(visibleRuns) : UnderlineCategory(visibleRuns);
        var wantedCategory = context.Operation.Expected.Value;
        var directSatisfied = property == "bold"
            ? bool.TryParse(wantedCategory, out var wantedBold) && visibleRuns.All(value => value.Bold == wantedBold)
            : wantedCategory == "none" && visibleRuns.All(value =>
                string.Equals(value.Underline, "none", StringComparison.OrdinalIgnoreCase));
        if (current == wantedCategory && directSatisfied) return Task.FromResult(FixApplyOutcome.NoChange);
        if (!FormattingFixSnapshot.ActualMatches(actual.RootElement, current))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        var visibleIndexes = visibleRuns.Select(value => value.Index).ToHashSet();
        var outcome = FormattingFixSnapshot.Mutate(context, body =>
        {
            var paragraph = FormattingFixSnapshot.XmlParagraph(context, body);
            var runs = paragraph.Descendants<Run>().Select((run, index) => (run, index))
                .Where(value => visibleIndexes.Contains(value.index)).Select(value => value.run).ToArray();
            if (runs.Length != visibleIndexes.Count)
                throw new FixExecutionException("fix-operation-target-precondition-failed");
            var changed = false;
            foreach (var run in runs)
            {
                run.RunProperties ??= new RunProperties();
                if (property == "bold")
                {
                    var wanted = context.Operation.Expected.Value == "true";
                    if (run.RunProperties.Bold is not null && run.RunProperties.Bold.Val?.Value == wanted) continue;
                    run.RunProperties.Bold = new Bold { Val = wanted };
                }
                else
                {
                    if (run.RunProperties.Underline?.Val?.Value == UnderlineValues.None) continue;
                    run.RunProperties.Underline = new Underline { Val = UnderlineValues.None };
                }
                changed = true;
            }
            return changed ? FixApplyOutcome.Changed : FixApplyOutcome.NoChange;
        });
        return Task.FromResult(outcome);
    }

    private static string BoldCategory(IReadOnlyList<ParsedRun> runs)
    {
        if (runs.Count == 0) return "empty";
        var values = runs.Select(value => value.EffectiveFormatting?.Bold).ToArray();
        if (values.Any(value => value?.State != FormattingResolutionState.Resolved || value.Value is null)) return "unresolved";
        var distinct = values.Select(value => value!.Value!.Value).Distinct().ToArray();
        return distinct.Length == 1 ? distinct[0].ToString().ToLowerInvariant() : "mixed";
    }

    private static string UnderlineCategory(IReadOnlyList<ParsedRun> runs)
    {
        var values = runs.Select(value => value.EffectiveFormatting?.Underline.Value).ToArray();
        if (values.All(value => string.IsNullOrEmpty(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))) return "none";
        return values.Select(value => string.IsNullOrEmpty(value) ? "none" : "present")
            .Distinct(StringComparer.Ordinal).Count() == 1 ? "present" : "mixed";
    }
}

public sealed class ChapterBoldFixProvider : HeadingRunFormattingFixProvider
{
    public const string Id = "chapter-bold-direct-heading-runs";
    public override string CapabilityId => Id;
    public ChapterBoldFixProvider() : base("heading.chapter-bold", "PPKI-HDG-004", 1, true,
        new Dictionary<string, bool?>(StringComparer.Ordinal) { ["bold"] = true }) { }
}

public sealed class ChapterDecorationFixProvider : HeadingRunFormattingFixProvider
{
    public const string Id = "chapter-decoration-direct-heading-runs";
    public override string CapabilityId => Id;
    public ChapterDecorationFixProvider() : base("heading.chapter-no-period-no-underline", "PPKI-HDG-005", 1, true,
        new Dictionary<string, bool?>(StringComparer.Ordinal) { ["underline"] = null }) { }
}

public sealed class SubheadingDecorationFixProvider : HeadingRunFormattingFixProvider
{
    public const string Id = "subheading-decoration-direct-heading-runs";
    public override string CapabilityId => Id;
    public SubheadingDecorationFixProvider() : base("heading.subheading-bold-no-period-no-underline", "PPKI-HDG-009", 2, false,
        new Dictionary<string, bool?>(StringComparer.Ordinal) { ["bold"] = true, ["underline"] = null }) { }
}

public sealed class SubSubheadingDecorationFixProvider : HeadingRunFormattingFixProvider
{
    public const string Id = "subsubheading-decoration-direct-heading-runs";
    public override string CapabilityId => Id;
    public SubSubheadingDecorationFixProvider() : base("heading.subsubheading-regular-no-period-no-underline", "PPKI-HDG-013", 3, false,
        new Dictionary<string, bool?>(StringComparer.Ordinal) { ["bold"] = false, ["underline"] = null }) { }
}
