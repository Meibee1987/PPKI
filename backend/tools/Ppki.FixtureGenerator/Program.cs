using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

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
        [new ParagraphDefinition("Paragraf sintetis untuk pengujian parser.", null, JustificationValues.Both, 240U, 567U)]),
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
        [new ParagraphDefinition("Paragraf sintetis untuk pengujian parser.", null, JustificationValues.Left, 276U, null)]),
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
        ])
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
    IReadOnlyList<ParagraphDefinition> Paragraphs);

internal sealed record ParagraphDefinition(
    string Text,
    string? StyleId,
    JustificationValues Alignment,
    uint LineSpacingTwips,
    uint? FirstLineIndentTwips);
