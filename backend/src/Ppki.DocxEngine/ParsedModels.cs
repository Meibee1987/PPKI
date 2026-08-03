namespace Ppki.DocxEngine;

public enum ParsedPackageType { Document, Template, MacroEnabledDocument, MacroEnabledTemplate }
public enum DocumentPartKind { MainDocument, Header, Footer, Footnotes, Endnotes, Comments, Unknown }
public enum DocumentElementKind { Section, Paragraph, Run, Table, TableRow, TableCell, Drawing, Field, HeaderFooter, Unknown }
public enum ParsedBodyElementKind { Paragraph, Table, SectionProperties, Unsupported }
public enum ParsedAlignment { Left, Center, Right, Justified, Distributed, Start, End }
public enum ParsedPageOrientation { Portrait, Landscape }
public enum ParsedHeaderFooterType { Default, First, Even, Unknown }
public enum ParsedDrawingKind { Inline, Anchor, Unknown }
public enum ParsedBreakKind { Line, Page, Column, TextWrapping, Unknown }
public enum ParsedFieldKind { Page, NumPages, Toc, Ref, Hyperlink, Date, Time, Unknown }
public enum ParserDiagnosticSeverity { Information, Warning, Error }

public sealed record DocumentElementLocation(
    DocumentPartKind PartKind,
    string PartUri,
    int? SectionIndex = null,
    int? BodyElementIndex = null,
    int? ParagraphIndex = null,
    int? RunIndex = null,
    int? TableIndex = null,
    int? RowIndex = null,
    int? CellIndex = null,
    ParsedHeaderFooterType? HeaderFooterType = null,
    DocumentElementKind ElementKind = DocumentElementKind.Unknown)
{
    public string ToCompactString()
    {
        var segments = new List<string> { PartKind.ToString().ToLowerInvariant() };
        Add(segments, "s", SectionIndex);
        Add(segments, "b", BodyElementIndex);
        Add(segments, "p", ParagraphIndex);
        Add(segments, "r", RunIndex);
        Add(segments, "t", TableIndex);
        Add(segments, "row", RowIndex);
        Add(segments, "cell", CellIndex);
        if (HeaderFooterType is not null) segments.Add($"hf:{HeaderFooterType.Value.ToString().ToLowerInvariant()}");
        if (ElementKind != DocumentElementKind.Unknown) segments.Add($"kind:{ElementKind.ToString().ToLowerInvariant()}");
        return string.Join("/", segments);
    }

    private static void Add(ICollection<string> segments, string prefix, int? value)
    {
        if (value is not null) segments.Add($"{prefix}:{value.Value}");
    }
}

public sealed record ParserDiagnosticMetadata(string Name, string Value);

public sealed record ParserDiagnostic(
    string Code,
    ParserDiagnosticSeverity Severity,
    string MessageKey,
    DocumentElementLocation? Location = null,
    IReadOnlyList<ParserDiagnosticMetadata>? Metadata = null);

public sealed record ParsedAggregateCounts(
    int Sections,
    int BodyElements,
    int Paragraphs,
    int Runs,
    int Tables,
    int Drawings,
    int Fields,
    int HeaderFooters,
    int Relationships,
    int ExternalRelationships,
    int FootnoteReferences,
    int EndnoteReferences,
    int CommentReferences);

public sealed record ParsedBodyElement(
    int Index,
    ParsedBodyElementKind Kind,
    DocumentElementLocation Location,
    int? ParagraphIndex = null,
    int? TableIndex = null,
    int? SectionIndex = null);

public sealed record ParsedDocument(
    IReadOnlyList<ParsedSection> Sections,
    IReadOnlyList<ParsedParagraph> Paragraphs,
    string ParserSchemaVersion = "1.0",
    ParsedPackageType PackageType = ParsedPackageType.Document,
    IReadOnlyList<ParsedBodyElement>? BodyElements = null,
    IReadOnlyList<ParsedTable>? Tables = null,
    IReadOnlyList<ParsedDrawing>? Drawings = null,
    IReadOnlyList<ParsedField>? Fields = null,
    IReadOnlyList<ParsedHeaderFooter>? HeaderFooters = null,
    IReadOnlyList<ParsedStyleReference>? StyleCatalog = null,
    IReadOnlyList<ParsedNumberingReference>? NumberingCatalog = null,
    IReadOnlyList<ParserDiagnostic>? Diagnostics = null,
    ParsedAggregateCounts? AggregateCounts = null)
{
    public IReadOnlyList<ParsedBodyElement> BodyElementOrder { get; } = BodyElements ?? [];
    public IReadOnlyList<ParsedTable> TableInventory { get; } = Tables ?? [];
    public IReadOnlyList<ParsedDrawing> DrawingInventory { get; } = Drawings ?? [];
    public IReadOnlyList<ParsedField> FieldInventory { get; } = Fields ?? [];
    public IReadOnlyList<ParsedHeaderFooter> HeaderFooterInventory { get; } = HeaderFooters ?? [];
    public IReadOnlyList<ParsedStyleReference> Styles { get; } = StyleCatalog ?? [];
    public IReadOnlyList<ParsedNumberingReference> Numbering { get; } = NumberingCatalog ?? [];
    public IReadOnlyList<ParserDiagnostic> ParserDiagnostics { get; } = Diagnostics ?? [];
    public ParsedAggregateCounts Counts { get; } = AggregateCounts ?? new(Sections.Count, BodyElements?.Count ?? 0, Paragraphs.Count, 0, Tables?.Count ?? 0, Drawings?.Count ?? 0, Fields?.Count ?? 0, HeaderFooters?.Count ?? 0, 0, 0, 0, 0, 0);
}

public sealed record ParsedSection(
    int Index,
    decimal? WidthCm,
    decimal? HeightCm,
    decimal? MarginTopCm,
    decimal? MarginRightCm,
    decimal? MarginBottomCm,
    decimal? MarginLeftCm,
    DocumentElementLocation? Location = null,
    long? PageWidthTwips = null,
    long? PageHeightTwips = null,
    ParsedPageOrientation? Orientation = null,
    long? MarginTopTwips = null,
    long? MarginRightTwips = null,
    long? MarginBottomTwips = null,
    long? MarginLeftTwips = null,
    long? HeaderDistanceTwips = null,
    long? FooterDistanceTwips = null,
    long? GutterTwips = null,
    string? SectionType = null,
    int? ColumnCount = null,
    long? ColumnSpacingTwips = null,
    int? StartPageNumber = null,
    IReadOnlyList<ParsedHeaderFooterReference>? HeaderFooterReferences = null,
    bool IsBodyLevel = false)
{
    public IReadOnlyList<ParsedHeaderFooterReference> HeaderFooterReferenceList { get; } = HeaderFooterReferences ?? [];
}

public sealed record ParsedParagraph(
    int Index,
    string Text,
    string? StyleId,
    bool IsHeading,
    bool IsInTable,
    string? FontName,
    decimal? FontSizePt,
    string Alignment,
    decimal? LineSpacingMultiple,
    decimal? FirstLineIndentCm,
    DocumentElementLocation? Location = null,
    ParsedStyleReference? StyleReference = null,
    ParsedNumberingReference? NumberingReference = null,
    ParsedAlignment? DirectAlignment = null,
    long? DirectIndentLeftTwips = null,
    long? DirectIndentRightTwips = null,
    long? DirectFirstLineIndentTwips = null,
    long? DirectHangingIndentTwips = null,
    long? DirectSpacingBeforeTwips = null,
    long? DirectSpacingAfterTwips = null,
    long? DirectLineSpacingValue = null,
    string? DirectLineSpacingRule = null,
    bool? KeepWithNext = null,
    bool? KeepLinesTogether = null,
    bool? PageBreakBefore = null,
    int? OutlineLevel = null,
    IReadOnlyList<ParsedRun>? Runs = null,
    bool HasTabs = false,
    bool HasBreaks = false,
    bool HasFields = false,
    bool HasDrawings = false,
    bool HasBookmarks = false,
    bool HasHyperlinks = false,
    bool HasTrackedChanges = false)
{
    public IReadOnlyList<ParsedRun> RunList { get; } = Runs ?? [];
}

public sealed record ParsedRun(
    int Index,
    DocumentElementLocation Location,
    IReadOnlyList<string> TextSegments,
    string? DirectFontAscii,
    string? DirectFontHighAnsi,
    int? DirectFontSizeHalfPoints,
    bool? Bold,
    bool? Italic,
    string? Underline,
    string? Language,
    string? VerticalAlignment,
    IReadOnlyList<ParsedBreakKind> Breaks,
    int TabCount,
    IReadOnlyList<int> FieldIndexes,
    IReadOnlyList<int> DrawingIndexes,
    bool IsDeleted,
    bool IsInserted,
    bool IsHidden);

public sealed record ParsedTable(
    int Index,
    DocumentElementLocation Location,
    string? StyleId,
    long? WidthTwips,
    string? WidthType,
    IReadOnlyList<long?> GridColumnWidthsTwips,
    IReadOnlyList<ParsedTableRow> Rows);

public sealed record ParsedTableRow(
    int Index,
    DocumentElementLocation Location,
    IReadOnlyList<ParsedTableCell> Cells);

public sealed record ParsedTableCell(
    int Index,
    DocumentElementLocation Location,
    long? WidthTwips,
    string? WidthType,
    IReadOnlyList<int> ParagraphIndexes);

public sealed record ParsedDrawing(
    int Index,
    DocumentElementLocation Location,
    ParsedDrawingKind Kind,
    string? RelationshipId,
    string? ContentType,
    long? WidthEmu,
    long? HeightEmu,
    bool HasExternalRelationship);

public sealed record ParsedField(
    int Index,
    DocumentElementLocation Location,
    ParsedFieldKind Kind,
    string NormalizedInstruction,
    bool HasBegin,
    bool HasSeparate,
    bool HasEnd);

public sealed record ParsedHeaderFooterReference(
    ParsedHeaderFooterType Type,
    string RelationshipId,
    string? NormalizedPartUri);

public sealed record ParsedHeaderFooter(
    int Index,
    ParsedHeaderFooterType Type,
    DocumentPartKind PartKind,
    string PartUri,
    IReadOnlyList<ParsedParagraph> Paragraphs,
    IReadOnlyList<ParserDiagnostic> Diagnostics);

public sealed record ParsedStyleReference(
    string StyleId,
    string? Name,
    string? Type,
    string? BasedOnStyleId,
    bool IsDefault,
    bool IsCustom);

public sealed record ParsedNumberingReference(
    int NumberingId,
    int? Level,
    int? AbstractNumberingId = null,
    string? NumberFormat = null,
    string? LevelText = null);

public interface IDocxParser
{
    Task<ParsedDocument> ParseAsync(string filePath, CancellationToken cancellationToken);
}
