namespace Ppki.DocxEngine;

public enum ParsedStyleType { Paragraph, Character, Table, Numbering, Unknown }
public enum FormattingResolutionState { Resolved, Unspecified, Unresolved, Invalid }
public enum FormattingSourceKind
{
    DirectFormatting,
    CharacterStyle,
    ParagraphStyle,
    BasedOnStyle,
    DocumentDefault,
    Theme,
    SectionProperties,
    Unspecified,
    Invalid
}

public sealed record FormattingProvenance(
    FormattingSourceKind SourceKind,
    string SourceProperty,
    string? SourceStyleId = null,
    bool Inherited = false,
    string? DiagnosticCode = null);

public sealed record ResolvedFormattingValue<T>(
    T Value,
    FormattingResolutionState State,
    FormattingProvenance Provenance);

public sealed record ParagraphFormattingProperties(
    ParsedAlignment? Alignment = null,
    long? IndentLeftTwips = null,
    long? IndentRightTwips = null,
    long? FirstLineIndentTwips = null,
    long? HangingIndentTwips = null,
    long? SpacingBeforeTwips = null,
    long? SpacingAfterTwips = null,
    long? LineSpacingValue = null,
    string? LineSpacingRule = null,
    bool? KeepWithNext = null,
    bool? KeepLinesTogether = null,
    bool? PageBreakBefore = null,
    bool? WidowControl = null,
    bool? ContextualSpacing = null,
    int? OutlineLevel = null,
    int? NumberingId = null,
    int? NumberingLevel = null);

public sealed record RunFormattingProperties(
    string? CharacterStyleId = null,
    string? FontAscii = null,
    string? FontHighAnsi = null,
    string? FontEastAsia = null,
    string? FontComplexScript = null,
    string? FontAsciiTheme = null,
    string? FontHighAnsiTheme = null,
    string? FontEastAsiaTheme = null,
    string? FontComplexScriptTheme = null,
    int? FontSizeHalfPoints = null,
    int? ComplexScriptFontSizeHalfPoints = null,
    bool? Bold = null,
    bool? Italic = null,
    string? Underline = null,
    bool? Strike = null,
    bool? Hidden = null,
    bool? Caps = null,
    bool? SmallCaps = null,
    string? Color = null,
    string? Language = null,
    string? LanguageEastAsia = null,
    string? LanguageComplexScript = null,
    string? VerticalAlignment = null);

public sealed record ParsedDocumentDefaults(
    ParagraphFormattingProperties Paragraph,
    RunFormattingProperties Run);

public sealed record ParsedThemeFontCatalog(
    string? MajorLatin,
    string? MinorLatin,
    string? MajorEastAsia,
    string? MinorEastAsia,
    string? MajorComplexScript,
    string? MinorComplexScript);

public sealed record EffectiveParagraphFormatting(
    ResolvedFormattingValue<ParsedAlignment?> Alignment,
    ResolvedFormattingValue<long?> IndentLeftTwips,
    ResolvedFormattingValue<long?> IndentRightTwips,
    ResolvedFormattingValue<long?> FirstLineIndentTwips,
    ResolvedFormattingValue<long?> HangingIndentTwips,
    ResolvedFormattingValue<long?> SpacingBeforeTwips,
    ResolvedFormattingValue<long?> SpacingAfterTwips,
    ResolvedFormattingValue<long?> LineSpacingValue,
    ResolvedFormattingValue<string?> LineSpacingRule,
    ResolvedFormattingValue<bool?> KeepWithNext,
    ResolvedFormattingValue<bool?> KeepLinesTogether,
    ResolvedFormattingValue<bool?> PageBreakBefore,
    ResolvedFormattingValue<bool?> WidowControl,
    ResolvedFormattingValue<bool?> ContextualSpacing,
    ResolvedFormattingValue<int?> OutlineLevel,
    ResolvedFormattingValue<int?> NumberingId,
    ResolvedFormattingValue<int?> NumberingLevel);

public sealed record EffectiveRunFormatting(
    ResolvedFormattingValue<string?> FontAscii,
    ResolvedFormattingValue<string?> FontHighAnsi,
    ResolvedFormattingValue<string?> FontEastAsia,
    ResolvedFormattingValue<string?> FontComplexScript,
    ResolvedFormattingValue<int?> FontSizeHalfPoints,
    ResolvedFormattingValue<int?> ComplexScriptFontSizeHalfPoints,
    ResolvedFormattingValue<bool?> Bold,
    ResolvedFormattingValue<bool?> Italic,
    ResolvedFormattingValue<string?> Underline,
    ResolvedFormattingValue<bool?> Strike,
    ResolvedFormattingValue<bool?> Hidden,
    ResolvedFormattingValue<bool?> Caps,
    ResolvedFormattingValue<bool?> SmallCaps,
    ResolvedFormattingValue<string?> Color,
    ResolvedFormattingValue<string?> Language,
    ResolvedFormattingValue<string?> LanguageEastAsia,
    ResolvedFormattingValue<string?> LanguageComplexScript,
    ResolvedFormattingValue<string?> VerticalAlignment);

public sealed record EffectiveSectionFormatting(
    ResolvedFormattingValue<long?> PageWidthTwips,
    ResolvedFormattingValue<long?> PageHeightTwips,
    ResolvedFormattingValue<ParsedPageOrientation?> Orientation,
    ResolvedFormattingValue<long?> MarginTopTwips,
    ResolvedFormattingValue<long?> MarginRightTwips,
    ResolvedFormattingValue<long?> MarginBottomTwips,
    ResolvedFormattingValue<long?> MarginLeftTwips,
    ResolvedFormattingValue<long?> HeaderDistanceTwips,
    ResolvedFormattingValue<long?> FooterDistanceTwips,
    ResolvedFormattingValue<long?> GutterTwips,
    ResolvedFormattingValue<int?> ColumnCount,
    ResolvedFormattingValue<long?> ColumnSpacingTwips,
    ResolvedFormattingValue<string?> SectionType,
    ResolvedFormattingValue<int?> StartPageNumber);
