using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.RuleEngine.Tests;

internal static class Wave1ValidatorTestData
{
    public static IReadOnlyList<IDocumentRuleValidator> StructuralValidators() =>
    [
        new ChapterNumberingValidator(),
        new HeadingDepthValidator(),
        new ChapterUppercaseValidator(),
        new ChapterBoldValidator(),
        new ChapterDecorationValidator(),
        new ChapterAlignmentValidator(),
        new SubheadingNumberingAlignmentValidator(),
        new SubheadingDecorationValidator(),
        new SubSubheadingNumberingAlignmentValidator(),
        new SubSubheadingDecorationValidator(),
        new SkripsiAbstractLanguagePairValidator(),
        new SkripsiAbstractParagraphCountValidator(),
        new SkripsiAbstractWordCountValidator(),
        new SkripsiAbstractSpacingValidator(),
        new ThesisSummaryLanguagePairValidator(),
        new AbstractSummarySpacingValidator()
    ];

    public static DocumentLayoutValidationEngine Engine(int maximumFindings = 10_000)
    {
        var validators = LayoutValidatorTestData.Validators().Concat(StructuralValidators()).ToArray();
        return new(new DocumentRuleValidatorRegistry(validators),
            new LayoutValidatorOptions { MaximumFindings = maximumFindings });
    }

    public static AuditRuleSnapshot Snapshot(
        string validationKey,
        int ordinal = 1,
        string appliesTo = "Semua",
        string validationJson = "{}",
        string? ruleCode = null,
        string domain = "HDG")
    {
        var snapshot = LayoutValidatorTestData.Snapshot(validationKey, ordinal, validationJson, appliesTo, ruleCode);
        snapshot.Domain = domain;
        snapshot.RequirementJson = "{\"officialRequirement\":\"Synthetic structural requirement\",\"expectedValuePattern\":\"controlled\"}";
        return snapshot;
    }

    public static ParsedDocument HeadingDocument(params HeadingSpec[] specs)
    {
        var paragraphs = new List<ParsedParagraph>();
        var headings = new List<ParsedHeading>();
        var sections = new List<SemanticDocumentSection>();
        var bodyElements = new List<ParsedBodyElement>();
        for (var index = 0; index < specs.Length; index++)
        {
            var spec = specs[index];
            var run = LayoutValidatorTestData.Run(index: 0, paragraphIndex: index, source: spec.Source,
                text: spec.Text, bold: spec.Bold, underline: spec.Underline);
            if (spec.Empty) run = LayoutValidatorTestData.Run(index: 0, paragraphIndex: index, empty: true);
            IReadOnlyList<ParsedRun> runs = spec.AddMixedBoldRun
                ? [run, LayoutValidatorTestData.Run(index: 1, paragraphIndex: index, source: spec.Source,
                    text: spec.Text, bold: spec.Bold != true, underline: spec.Underline)]
                : [run];
            var paragraph = LayoutValidatorTestData.Paragraph(alignment: spec.Alignment, heading: true,
                inTable: spec.InTable, runs: runs, index: index, formattingSource: spec.Source, text: spec.Text);
            var numbering = spec.NumberingState == NumberingResolutionState.Unspecified
                ? null
                : new EffectiveParagraphNumbering(
                    spec.NumberingState, 10, spec.Level - 1, 10,
                    new(spec.Source, "numbering", "SyntheticHeading", spec.Source != FormattingSourceKind.DirectFormatting),
                    new(spec.NumberingState, spec.Label, spec.Label, spec.NumberingFormat,
                        ParsedNumberingSuffix.Nothing));
            paragraph = paragraph with { EffectiveNumbering = numbering };
            paragraphs.Add(paragraph);
            var heading = new ParsedHeading(index, index, paragraph.Location!, spec.Level, spec.Classification,
                [new(HeadingEvidenceKind.DirectOutlineLevel, FormattingSourceKind.DirectFormatting, "outlineLevel",
                    OutlineLevel: spec.Level - 1)], paragraph.StyleId, spec.Level - 1, numbering, false, index);
            headings.Add(heading);
            bodyElements.Add(new(index, ParsedBodyElementKind.Paragraph, paragraph.Location!, ParagraphIndex: index));
            if (spec.SemanticKind is not null)
            {
                var range = new SemanticSectionRange(paragraph.Location!, null, paragraph.Location!, index, index, 0);
                sections.Add(new(sections.Count, spec.SemanticKind.Value, spec.Zone, spec.SemanticState,
                    SemanticClassificationBasis.ExactAlias, index, paragraph.Location!, spec.Level,
                    SemanticNumberingCategory.ResolvedNumbering,
                    [new(SemanticSectionEvidenceKind.StructuralHeading, spec.Level)], index, null, range, null, []));
            }
        }
        return new([], paragraphs, BodyElements: bodyElements, HeadingInventory: headings,
            SemanticStructure: new("1.0", sections, [], []),
            ObservedSystematics: new(sections.Select((section, index) => new DocumentSystematicsEntry(
                index, section.Index, section.Kind, section.Zone, section.HeadingLocation, section.Range.EndLocation,
                section.HeadingLevel, null, section.ClassificationState, [SemanticSectionEvidenceKind.StructuralHeading], null)).ToArray(),
                null, null, null, sections.Count(value => value.Kind == SemanticSectionKind.Chapter), [], [], [], [], []));
    }

    public static ParsedDocument AbstractDocument(
        SemanticSectionKind kind,
        string text,
        bool hidden = false,
        bool deleted = false,
        bool keyword = false,
        FormattingSourceKind source = FormattingSourceKind.ParagraphStyle)
    {
        var headingLocation = Location(0, 0);
        var bodyLocation = Location(1, 1);
        var run = LayoutValidatorTestData.Run(paragraphIndex: 1, source: source, text: text,
            hidden: hidden, deleted: deleted);
        var heading = LayoutValidatorTestData.Paragraph(heading: true, index: 0, text: kind.ToString());
        var body = LayoutValidatorTestData.Paragraph(runs: [run], index: 1, formattingSource: source, text: text);
        var range = new SemanticSectionRange(headingLocation, bodyLocation, bodyLocation, 0, 1, 1);
        var section = new SemanticDocumentSection(0, kind, SemanticSectionZone.FrontMatter,
            SemanticClassificationState.Confirmed, SemanticClassificationBasis.ExactAlias, 0,
            headingLocation, 1, SemanticNumberingCategory.None,
            [new(SemanticSectionEvidenceKind.ExactHeadingAlias)], 0, null, range, null, []);
        var language = kind is SemanticSectionKind.AbstractEnglish or SemanticSectionKind.SummaryEnglish
            ? SemanticSectionLanguage.English : SemanticSectionLanguage.Indonesian;
        var descriptor = new AbstractSectionDescriptor(0, kind, language, headingLocation, bodyLocation,
            bodyLocation, 1, keyword ? bodyLocation : null,
            [new(SemanticSectionEvidenceKind.ExactHeadingAlias)], []);
        return new([], [heading, body], BodyElements:
        [
            new(0, ParsedBodyElementKind.Paragraph, headingLocation, ParagraphIndex: 0),
            new(1, ParsedBodyElementKind.Paragraph, bodyLocation, ParagraphIndex: 1)
        ], SemanticStructure: new("1.0", [section], [descriptor], []),
            ObservedSystematics: new([], headingLocation, null, null, 0, [0], [], [], [], []));
    }

    private static DocumentElementLocation Location(int body, int paragraph) => new(
        DocumentPartKind.MainDocument, "/word/document.xml", SectionIndex: 0,
        BodyElementIndex: body, ParagraphIndex: paragraph, ElementKind: DocumentElementKind.Paragraph);
}

internal sealed record HeadingSpec(
    int Level,
    string Text,
    SemanticSectionKind? SemanticKind = null,
    ParsedNumberingFormat NumberingFormat = ParsedNumberingFormat.UpperRoman,
    string? Label = "I",
    NumberingResolutionState NumberingState = NumberingResolutionState.Resolved,
    ParsedAlignment Alignment = ParsedAlignment.Center,
    bool? Bold = true,
    string? Underline = null,
    bool Empty = false,
    bool AddMixedBoldRun = false,
    bool InTable = false,
    HeadingClassification Classification = HeadingClassification.Confirmed,
    FormattingSourceKind Source = FormattingSourceKind.ParagraphStyle,
    SemanticClassificationState SemanticState = SemanticClassificationState.Confirmed,
    SemanticSectionZone Zone = SemanticSectionZone.MainMatter);
