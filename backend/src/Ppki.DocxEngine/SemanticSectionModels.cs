namespace Ppki.DocxEngine;

public enum SemanticSectionKind
{
    TitlePage, ApprovalPage, StatementPage,
    AbstractIndonesian, AbstractEnglish, SummaryIndonesian, SummaryEnglish,
    Preface, TableOfContents, ListOfTables, ListOfFigures, ListOfAppendices, OtherFrontMatter,
    Chapter, Introduction, LiteratureReview, Methods, Results, Discussion,
    ResultsAndDiscussion, Conclusion, Recommendations, OtherMainMatter,
    References, Appendices, Biography, OtherBackMatter
}

public enum SemanticSectionZone { FrontMatter, MainMatter, BackMatter, Unknown }
public enum SemanticClassificationState { Confirmed, Candidate, Ambiguous, Unresolved }
public enum SemanticClassificationBasis { ExactAlias, ChapterMarker, StructuralHeading, ConflictingEvidence }
public enum SemanticSectionLanguage { Indonesian, English }
public enum SemanticNumberingCategory { None, ChapterMarker, ResolvedNumbering, UnresolvedNumbering }

public enum SemanticSectionEvidenceKind
{
    StructuralHeading,
    DirectOutline,
    StyleOutline,
    BasedOnHeadingStyle,
    NumberingLinkedHeadingStyle,
    ExactHeadingAlias,
    ChapterMarker,
    HeadingLevel,
    ResolvedNumbering,
    StartsNewOpenXmlSection,
    BodyOrder,
    ZoneBoundary,
    ExcludedTableHeading
}

public sealed record SemanticSectionEvidence(
    SemanticSectionEvidenceKind Kind,
    int? HeadingLevel = null,
    int? NumberingLevel = null);

public sealed record SemanticSectionRange(
    DocumentElementLocation StartLocation,
    DocumentElementLocation? ContentStartLocation,
    DocumentElementLocation EndLocation,
    int StartBodyElementIndex,
    int EndBodyElementIndex,
    int ParagraphCount);

public sealed record SemanticDocumentSection(
    int Index,
    SemanticSectionKind Kind,
    SemanticSectionZone Zone,
    SemanticClassificationState ClassificationState,
    SemanticClassificationBasis ClassificationBasis,
    int HeadingIndex,
    DocumentElementLocation HeadingLocation,
    int HeadingLevel,
    SemanticNumberingCategory NumberingCategory,
    IReadOnlyList<SemanticSectionEvidence> Evidence,
    int BodyOrderIndex,
    int? ParentSectionIndex,
    SemanticSectionRange Range,
    int? DuplicateGroup,
    IReadOnlyList<string> DiagnosticCodes);

public sealed record AbstractSectionDescriptor(
    int SectionIndex,
    SemanticSectionKind Kind,
    SemanticSectionLanguage Language,
    DocumentElementLocation HeadingLocation,
    DocumentElementLocation? ContentStartLocation,
    DocumentElementLocation EndLocation,
    int ParagraphCount,
    DocumentElementLocation? KeywordParagraphLocation,
    IReadOnlyList<SemanticSectionEvidence> Evidence,
    IReadOnlyList<string> DiagnosticCodes);

public sealed record ExcludedSemanticHeadingCandidate(
    int HeadingIndex,
    DocumentElementLocation Location,
    SemanticSectionEvidenceKind Reason);

public sealed record SemanticDocumentStructure(
    string CatalogVersion,
    IReadOnlyList<SemanticDocumentSection> Sections,
    IReadOnlyList<AbstractSectionDescriptor> AbstractSections,
    IReadOnlyList<ExcludedSemanticHeadingCandidate> ExcludedCandidates);

public sealed record DocumentSystematicsEntry(
    int ObservedOrdinal,
    int SectionIndex,
    SemanticSectionKind Kind,
    SemanticSectionZone Zone,
    DocumentElementLocation StartLocation,
    DocumentElementLocation EndLocation,
    int HeadingLevel,
    int? ParentEntryOrdinal,
    SemanticClassificationState ClassificationState,
    IReadOnlyList<SemanticSectionEvidenceKind> EvidenceSummary,
    int? DuplicateGroup);

public sealed record DocumentSystematics(
    IReadOnlyList<DocumentSystematicsEntry> OrderedSections,
    DocumentElementLocation? FrontMatterStart,
    DocumentElementLocation? MainMatterStart,
    DocumentElementLocation? BackMatterStart,
    int DetectedChapterCount,
    IReadOnlyList<int> AbstractSectionIndexes,
    IReadOnlyList<int> DuplicateSectionIndexes,
    IReadOnlyList<int> AmbiguousSectionIndexes,
    IReadOnlyList<int> UnknownStructuralHeadingIndexes,
    IReadOnlyList<string> StructureDiagnosticCodes);

public sealed record SemanticSectionDetectionResult(
    SemanticDocumentStructure Structure,
    DocumentSystematics Systematics,
    IReadOnlyList<ParserDiagnostic> Diagnostics);
