using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

var outputDirectory = GetOutputDirectory(args);
var fixtures = CreateFixtures();
Directory.CreateDirectory(outputDirectory);

foreach (var fixture in fixtures)
{
    CreateDocument(Path.Combine(outputDirectory, fixture.FileName), fixture);
}

Console.WriteLine($"Generated {fixtures.Count} synthetic DOCX fixtures.");

return;

static string GetOutputDirectory(string[] arguments)
{
    var outputIndex = Array.IndexOf(arguments, "--output");
    if (outputIndex >= 0 && outputIndex + 1 < arguments.Length)
    {
        return Path.GetFullPath(arguments[outputIndex + 1]);
    }

    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", "docx", "generated"));
}

static void CreateDocument(string filePath, FixtureDefinition fixture)
{
    using var document = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
    document.PackageProperties.Creator = string.Empty;
    document.PackageProperties.LastModifiedBy = string.Empty;
    document.PackageProperties.Title = "Dokumen Sintetis untuk Pengujian";
    document.PackageProperties.Subject = string.Empty;
    document.PackageProperties.Keywords = string.Empty;

    var mainPart = document.AddMainDocumentPart();
    AddStyles(mainPart);

    if (fixture.Kind == FixtureKind.TableField)
    {
        CreateTableFieldDocument(mainPart);
        return;
    }
    if (fixture.Kind == FixtureKind.HeaderFooter)
    {
        CreateHeaderFooterDocument(mainPart, fixture);
        return;
    }

    var body = new Body();
    foreach (var paragraph in fixture.Paragraphs)
    {
        body.Append(CreateParagraph(paragraph, fixture));
    }

    body.Append(new SectionProperties(
        new PageSize { Width = fixture.PageWidthTwips, Height = fixture.PageHeightTwips },
        new PageMargin
        {
            Top = checked((int)fixture.MarginTopTwips),
            Right = fixture.MarginRightTwips,
            Bottom = checked((int)fixture.MarginBottomTwips),
            Left = fixture.MarginLeftTwips,
            Header = 720U,
            Footer = 720U,
            Gutter = 0U
        }));

    mainPart.Document = new Document(body);
    mainPart.Document.Save();
}

static void CreateTableFieldDocument(MainDocumentPart mainPart)
{
    AddNumbering(mainPart);
    var imagePart = mainPart.AddImagePart(ImagePartType.Png);
    using (var image = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")))
    {
        imagePart.FeedData(image);
    }
    var imageRelationshipId = mainPart.GetIdOfPart(imagePart);
    var numbered = CreateParagraph(new ParagraphDefinition("Daftar sintetis", null, JustificationValues.Left, 240U, null), BaselineFixture());
    numbered.ParagraphProperties!.Append(new NumberingProperties(
        new NumberingLevelReference { Val = 0 },
        new NumberingId { Val = 1 }));
    numbered.Append(new Run(
        new Text("A"),
        new TabChar(),
        new Text("B"),
        new Break { Type = BreakValues.TextWrapping },
        new Break { Type = BreakValues.Page },
        new FootnoteReference { Id = 1 },
        new EndnoteReference { Id = 1 },
        new CommentReference { Id = "1" },
        CreateDrawing(imageRelationshipId)));
    var fieldParagraph = new Paragraph(
        new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
        new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
        new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
        new Run(new Text("1")),
        new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
    var table = new Table(
        new TableProperties(
            new TableStyle { Val = "SyntheticTable" },
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Dxa }),
        new TableGrid(new GridColumn { Width = "2500" }, new GridColumn { Width = "2500" }),
        new TableRow(
            new TableCell(
                new TableCellProperties(new TableCellWidth { Width = "2500", Type = TableWidthUnitValues.Dxa }),
                new Paragraph(new Run(new Text("Sel satu sintetis")))),
            new TableCell(
                new TableCellProperties(new TableCellWidth { Width = "2500", Type = TableWidthUnitValues.Dxa }),
                new Paragraph(new Run(new Text("Sel dua sintetis"))))));
    var body = new Body(numbered, fieldParagraph, table, StandardSection());
    mainPart.Document = new Document(body);
    mainPart.Document.Save();
}

static void CreateHeaderFooterDocument(MainDocumentPart mainPart, FixtureDefinition fixture)
{
    var headerPart = mainPart.AddNewPart<HeaderPart>();
    headerPart.Header = new Header(new Paragraph(new Run(new Text("Header sintetis"))));
    headerPart.Header.Save();
    var footerPart = mainPart.AddNewPart<FooterPart>();
    footerPart.Footer = new Footer(new Paragraph(new Run(new Text("Footer sintetis"))));
    footerPart.Footer.Save();
    var headerId = mainPart.GetIdOfPart(headerPart);
    var footerId = mainPart.GetIdOfPart(footerPart);
    var firstSection = StandardSection(
        new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId });
    var firstParagraph = CreateParagraph(new ParagraphDefinition("Bagian pertama sintetis", null, JustificationValues.Left, 240U, null), fixture);
    firstParagraph.ParagraphProperties!.Append(firstSection);
    var finalSection = StandardSection(
        new FooterReference { Type = HeaderFooterValues.Default, Id = footerId });
    var body = new Body(
        firstParagraph,
        CreateParagraph(new ParagraphDefinition("Bagian kedua sintetis", null, JustificationValues.Left, 240U, null), fixture),
        finalSection);
    mainPart.Document = new Document(body);
    mainPart.Document.Save();
}

static SectionProperties StandardSection(params OpenXmlElement[] references) => new(
    references.Concat<OpenXmlElement>([
        new PageSize { Width = 11906U, Height = 16838U },
        new PageMargin { Top = 1701, Right = 1701U, Bottom = 1701, Left = 2268U, Header = 720U, Footer = 720U, Gutter = 0U },
        new Columns { ColumnCount = 1, Space = "720" },
        new PageNumberType { Start = 1 }
    ]));

static Drawing CreateDrawing(string relationshipId) => new(
    new DW.Inline(
        new DW.Extent { Cx = 9525L, Cy = 9525L },
        new DW.DocProperties { Id = 1U, Name = "Synthetic image" },
        new A.Graphic(
            new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 1U, Name = "synthetic.png" },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relationshipId }),
                    new PIC.ShapeProperties()))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));

static void AddNumbering(MainDocumentPart mainPart)
{
    var part = mainPart.AddNewPart<NumberingDefinitionsPart>();
    part.Numbering = new Numbering(
        new AbstractNum(
            new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Decimal },
                new LevelText { Val = "%1." }) { LevelIndex = 0 }) { AbstractNumberId = 1 },
        new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
    part.Numbering.Save();
}

static FixtureDefinition BaselineFixture() => new(
    "", 11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
    "Times New Roman", 24U, [], FixtureKind.Basic);

static Paragraph CreateParagraph(ParagraphDefinition definition, FixtureDefinition fixture)
{
    var properties = new ParagraphProperties();
    if (definition.StyleId is not null)
    {
        properties.Append(new ParagraphStyleId { Val = definition.StyleId });
    }

    properties.Append(new Justification { Val = definition.Alignment });
    properties.Append(new SpacingBetweenLines { Line = definition.LineSpacingTwips.ToString(), LineRule = LineSpacingRuleValues.Auto });
    if (definition.FirstLineIndentTwips is not null)
    {
        properties.Append(new Indentation { FirstLine = definition.FirstLineIndentTwips.Value.ToString() });
    }

    return new Paragraph(
        properties,
        new Run(
            new RunProperties(
                new RunFonts { Ascii = fixture.FontName, HighAnsi = fixture.FontName },
                new FontSize { Val = fixture.FontSizeHalfPoints.ToString() }),
            new Text(definition.Text)));
}

static void AddStyles(MainDocumentPart mainPart)
{
    var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
    stylesPart.Styles = new Styles(
        CreateStyle("Normal", "Normal", true),
        CreateStyle("Heading1", "Heading 1", false),
        CreateStyle("Heading2", "Heading 2", false));
    stylesPart.Styles.Save();
}

static Style CreateStyle(string styleId, string name, bool isDefault) => new()
{
    Type = StyleValues.Paragraph,
    StyleId = styleId,
    Default = isDefault,
    StyleName = new StyleName { Val = name }
};

static IReadOnlyList<FixtureDefinition> CreateFixtures() =>
[
    new(
        "minimal-compliant-layout.docx",
        11906U,
        16838U,
        1701U,
        1701U,
        1701U,
        2268U,
        "Times New Roman",
        24U,
        [new ParagraphDefinition("Paragraf sintetis untuk pengujian parser.", null, JustificationValues.Both, 240U, 567U)], FixtureKind.Basic),
    new(
        "minimal-invalid-layout.docx",
        12240U,
        15840U,
        1440U,
        1440U,
        1440U,
        1440U,
        "Calibri",
        22U,
        [new ParagraphDefinition("Paragraf sintetis untuk pengujian parser.", null, JustificationValues.Left, 276U, null)], FixtureKind.Basic),
    new(
        "minimal-heading-layout.docx",
        11906U,
        16838U,
        1701U,
        1701U,
        1701U,
        2268U,
        "Times New Roman",
        24U,
        [
            new ParagraphDefinition("BAB I DOKUMEN SINTETIS", "Heading1", JustificationValues.Center, 240U, null),
            new ParagraphDefinition("1.1 Subbab Sintetis", "Heading2", JustificationValues.Left, 240U, null),
            new ParagraphDefinition("Paragraf sintetis untuk pengujian parser.", null, JustificationValues.Both, 240U, 567U)
        ], FixtureKind.Basic),
    new(
        "minimal-table-field-layout.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.TableField),
    new(
        "minimal-header-footer-layout.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.HeaderFooter)
];

internal sealed record FixtureDefinition(
    string FileName,
    uint PageWidthTwips,
    uint PageHeightTwips,
    uint MarginTopTwips,
    uint MarginRightTwips,
    uint MarginBottomTwips,
    uint MarginLeftTwips,
    string FontName,
    uint FontSizeHalfPoints,
    IReadOnlyList<ParagraphDefinition> Paragraphs,
    FixtureKind Kind);

internal sealed record ParagraphDefinition(
    string Text,
    string? StyleId,
    JustificationValues Alignment,
    uint LineSpacingTwips,
    uint? FirstLineIndentTwips);

internal enum FixtureKind { Basic, TableField, HeaderFooter }
