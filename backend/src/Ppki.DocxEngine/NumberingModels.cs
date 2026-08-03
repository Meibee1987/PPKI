namespace Ppki.DocxEngine;

public enum ParsedNumberingFormat { Decimal, UpperRoman, LowerRoman, UpperLetter, LowerLetter, Bullet, None, Unsupported }
public enum ParsedNumberingSuffix { Tab, Space, Nothing, Unspecified }
public enum NumberingResolutionState { Resolved, Unspecified, Unresolved, Disabled }
public enum HeadingClassification { Confirmed, Candidate }
public enum HeadingEvidenceKind
{
    DirectOutlineLevel,
    ParagraphStyleOutlineLevel,
    BasedOnHeadingStyle,
    BuiltInHeadingStyle,
    NumberingLevelLinkedToHeadingStyle,
    ExplicitHeadingStyleReference,
    FormattingOnlyCandidate
}

public sealed record ParsedNumberingCatalog(
    IReadOnlyList<ParsedAbstractNumbering> AbstractDefinitions,
    IReadOnlyList<ParsedNumberingInstance> Instances);

public sealed record ParsedAbstractNumbering(
    int AbstractNumberingId,
    string? MultiLevelType,
    string? StyleLink,
    string? NumberingStyleLink,
    IReadOnlyList<ParsedNumberingLevel> Levels,
    int DeclarationOrder);

public sealed record ParsedNumberingInstance(
    int NumberingId,
    int? AbstractNumberingId,
    IReadOnlyList<ParsedNumberingLevelOverride> LevelOverrides,
    int DeclarationOrder);

public sealed record ParsedNumberingLevel(
    int Level,
    int? StartValue,
    ParsedNumberingFormat Format,
    string? RawFormat,
    string? LevelText,
    ParsedNumberingSuffix Suffix,
    string? Justification,
    int? RestartAfterLevel,
    bool? IsLegalNumbering,
    string? ParagraphStyleId,
    long? IndentLeftTwips,
    long? HangingIndentTwips,
    RunFormattingProperties RunProperties,
    int DeclarationOrder);

public sealed record ParsedNumberingLevelOverride(
    int Level,
    int? StartOverride,
    ParsedNumberingLevel? LevelDefinition,
    int DeclarationOrder);

public sealed record NumberingProvenance(
    FormattingSourceKind SourceKind,
    string SourceProperty,
    string? SourceStyleId,
    bool Inherited,
    string? DiagnosticCode = null);

public sealed record ResolvedNumberingLabel(
    NumberingResolutionState State,
    string? Value,
    string? ValueWithSuffix,
    ParsedNumberingFormat Format,
    ParsedNumberingSuffix Suffix,
    string? DiagnosticCode = null);

public sealed record EffectiveParagraphNumbering(
    NumberingResolutionState State,
    int? NumberingId,
    int? Level,
    int? AbstractNumberingId,
    NumberingProvenance Provenance,
    ResolvedNumberingLabel? Label = null,
    bool IsExplicitlyDisabled = false,
    string? DiagnosticCode = null);

public sealed record HeadingEvidence(
    HeadingEvidenceKind Kind,
    FormattingSourceKind SourceKind,
    string SourceProperty,
    string? SourceStyleId = null,
    int? OutlineLevel = null,
    int? NumberingLevel = null);

public sealed record ParsedHeading(
    int Index,
    int ParagraphIndex,
    DocumentElementLocation Location,
    int Level,
    HeadingClassification Classification,
    IReadOnlyList<HeadingEvidence> Evidence,
    string? EffectiveParagraphStyleId,
    int? OutlineLevel,
    EffectiveParagraphNumbering? Numbering,
    bool StartsNewSection,
    int Order);

public sealed record DocumentOutlineNode(
    int Index,
    int HeadingIndex,
    DocumentElementLocation Location,
    int Level,
    int? ParentNodeIndex,
    IReadOnlyList<DocumentOutlineNode> Children);

public sealed record DocumentOutline(
    IReadOnlyList<DocumentOutlineNode> RootNodes,
    int NodeCount);
