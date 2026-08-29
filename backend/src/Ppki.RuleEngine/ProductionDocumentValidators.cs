namespace Ppki.RuleEngine;

public static class ProductionDocumentValidators
{
    public static IReadOnlyList<IDocumentRuleValidator> Create() =>
    [
        new PageSizeA4Validator(),
        new MarginLeftValidator(),
        new MarginRightValidator(),
        new MarginTopValidator(),
        new MarginBottomValidator(),
        new BodyFontValidator(),
        new LineSpacingValidator(),
        new FirstLineIndentValidator(),
        new JustifiedValidator(),
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
}
