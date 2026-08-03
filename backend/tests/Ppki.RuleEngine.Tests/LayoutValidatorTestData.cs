using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.RuleEngine.Tests.Fixtures;

namespace Ppki.RuleEngine.Tests;

internal static class LayoutValidatorTestData
{
    public static IReadOnlyList<IDocumentRuleValidator> Validators() =>
    [
        new PageSizeA4Validator(),
        new MarginLeftValidator(),
        new MarginRightValidator(),
        new MarginTopValidator(),
        new MarginBottomValidator(),
        new BodyFontValidator(),
        new LineSpacingValidator(),
        new FirstLineIndentValidator(),
        new JustifiedValidator()
    ];

    public static DocumentLayoutValidationEngine Engine(int maximumFindings = LayoutValidatorOptions.DefaultMaximumFindings) =>
        new(new DocumentRuleValidatorRegistry(Validators()), new LayoutValidatorOptions { MaximumFindings = maximumFindings });

    public static AuditRuleSnapshot Snapshot(
        string validationKey,
        int ordinal = 1,
        string validationJson = "{}",
        string appliesTo = "Semua",
        string? ruleCode = null) => new()
    {
        AuditJobId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        RuleId = Guid.Parse($"{ordinal:D8}-2222-2222-2222-222222222222"),
        RuleCode = ruleCode ?? $"SYNTHETIC-LAYOUT-{ordinal:D3}",
        Domain = "LAY",
        Subdomain = "Synthetic",
        AppliesTo = appliesTo,
        Element = "Synthetic layout property",
        RequirementJson = "{\"officialRequirement\":\"Synthetic layout requirement\",\"expectedValuePattern\":\"controlled\"}",
        ValidationKey = validationKey,
        ValidationJson = validationJson,
        Severity = RuleSeverity.Error,
        FixMode = FixMode.Report,
        SourceReferenceJson = "{\"sourceSection\":\"Synthetic source\",\"pdfPage\":1,\"printedPage\":\"1\"}",
        Layer = "profile",
        Precedence = 0,
        Ordinal = ordinal,
        SnapshotSchemaVersion = 1
    };

    public static IReadOnlyList<AuditRuleSnapshot> DefaultSnapshots() =>
    [
        Snapshot("section.page-size-a4", 1, ruleCode: "PPKI-LAY-003"),
        Snapshot("body.font-times-new-roman-12", 2, ruleCode: "PPKI-LAY-005"),
        Snapshot("section.margin-left-4cm", 3, ruleCode: "PPKI-LAY-008"),
        Snapshot("section.margin-right-3cm", 4, ruleCode: "PPKI-LAY-009"),
        Snapshot("section.margin-top-3cm", 5, ruleCode: "PPKI-LAY-010"),
        Snapshot("section.margin-bottom-3cm", 6, ruleCode: "PPKI-LAY-011"),
        Snapshot("body.line-spacing-single", 7, ruleCode: "PPKI-LAY-017"),
        Snapshot("body.first-line-indent-1cm", 8, ruleCode: "PPKI-LAY-018"),
        Snapshot("body.justified", 9, ruleCode: "PPKI-LAY-019")
    ];

    public static async Task<ParsedDocument> ParseFixture(string fixtureId)
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync(fixtureId);
        return await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
    }

    public static ParsedSection Section(long? width, long? height, long? top, long? right, long? bottom, long? left, int index = 0)
    {
        var location = new DocumentElementLocation(DocumentPartKind.MainDocument, "/word/document.xml",
            SectionIndex: index, BodyElementIndex: index, ElementKind: DocumentElementKind.Section);
        return new ParsedSection(index, null, null, null, null, null, null, location,
            EffectiveFormatting: new(
                Value(width, "pageWidthTwips"),
                Value(height, "pageHeightTwips"),
                Value<ParsedPageOrientation?>(null, "orientation"),
                Value(top, "marginTopTwips"),
                Value(right, "marginRightTwips"),
                Value(bottom, "marginBottomTwips"),
                Value(left, "marginLeftTwips"),
                Value<long?>(null, "headerDistanceTwips"),
                Value<long?>(null, "footerDistanceTwips"),
                Value<long?>(null, "gutterTwips"),
                Value<int?>(null, "columnCount"),
                Value<long?>(null, "columnSpacingTwips"),
                Value<string?>(null, "sectionType"),
                Value<int?>(null, "startPageNumber")));
    }

    public static ParsedParagraph Paragraph(
        ParsedAlignment? alignment = ParsedAlignment.Justified,
        long? firstLine = 567,
        long? hanging = null,
        long? lineValue = 240,
        string? lineRule = "auto",
        bool heading = false,
        bool inTable = false,
        IReadOnlyList<ParsedRun>? runs = null,
        int index = 0,
        FormattingSourceKind formattingSource = FormattingSourceKind.ParagraphStyle,
        string text = "SYNTHETIC-DOCUMENT-TEXT-MARKER")
    {
        var location = new DocumentElementLocation(DocumentPartKind.MainDocument, "/word/document.xml",
            SectionIndex: 0, BodyElementIndex: index, ParagraphIndex: index,
            ElementKind: DocumentElementKind.Paragraph);
        runs ??= [Run(index: 0, paragraphIndex: index)];
        return new ParsedParagraph(index, text, heading ? "Heading1" : "Normal",
            heading, inTable, null, null, alignment?.ToString() ?? string.Empty, null, null,
            Location: location,
            Runs: runs,
            EffectiveFormatting: new(
                Value(alignment, "alignment", formattingSource),
                Value<long?>(null, "indentLeftTwips"),
                Value<long?>(null, "indentRightTwips"),
                Value(firstLine, "firstLineIndentTwips", formattingSource),
                Value(hanging, "hangingIndentTwips", formattingSource),
                Value<long?>(0, "spacingBeforeTwips"),
                Value<long?>(0, "spacingAfterTwips"),
                Value(lineValue, "lineSpacingValue", formattingSource),
                Value(lineRule, "lineSpacingRule", formattingSource),
                Value<bool?>(null, "keepWithNext"),
                Value<bool?>(null, "keepLinesTogether"),
                Value<bool?>(null, "pageBreakBefore"),
                Value<bool?>(null, "widowControl"),
                Value<bool?>(null, "contextualSpacing"),
                Value<int?>(null, "outlineLevel"),
                Value<int?>(null, "numberingId"),
                Value<int?>(null, "numberingLevel")));
    }

    public static ParsedRun Run(
        string? ascii = "Times New Roman",
        string? highAnsi = "Times New Roman",
        int? size = 24,
        bool deleted = false,
        bool hidden = false,
        bool empty = false,
        int index = 0,
        int paragraphIndex = 0,
        FormattingSourceKind source = FormattingSourceKind.ParagraphStyle,
        string text = "SYNTHETIC-DOCUMENT-TEXT-MARKER",
        bool? bold = null,
        string? underline = null)
    {
        var location = new DocumentElementLocation(DocumentPartKind.MainDocument, "/word/document.xml",
            SectionIndex: 0, BodyElementIndex: paragraphIndex, ParagraphIndex: paragraphIndex, RunIndex: index,
            ElementKind: DocumentElementKind.Run);
        return new ParsedRun(index, location, empty ? [] : [text],
            null, null, null, bold, null, underline, null, null, [], 0, [], [], deleted, false, hidden,
            EffectiveFormatting: new(
                Value(ascii, "fontAscii", source),
                Value(highAnsi, "fontHighAnsi", source),
                Value<string?>(null, "fontEastAsia"),
                Value<string?>(null, "fontComplexScript"),
                Value(size, "fontSizeHalfPoints", source),
                Value<int?>(null, "complexScriptFontSizeHalfPoints"),
                Value(bold, "bold", source),
                Value<bool?>(null, "italic"),
                Value(underline, "underline", source),
                Value<bool?>(null, "strike"),
                Value<bool?>(hidden, "hidden"),
                Value<bool?>(null, "caps"),
                Value<bool?>(null, "smallCaps"),
                Value<string?>(null, "color"),
                Value<string?>(null, "language"),
                Value<string?>(null, "languageEastAsia"),
                Value<string?>(null, "languageComplexScript"),
                Value<string?>(null, "verticalAlignment")));
    }

    public static ResolvedFormattingValue<T> Value<T>(T value, string property,
        FormattingSourceKind source = FormattingSourceKind.SectionProperties) => new(
        value,
        value is null ? FormattingResolutionState.Unspecified : FormattingResolutionState.Resolved,
        new(source, property, source is FormattingSourceKind.ParagraphStyle or FormattingSourceKind.CharacterStyle ? "SyntheticStyle" : null,
            source is FormattingSourceKind.ParagraphStyle or FormattingSourceKind.CharacterStyle));
}
