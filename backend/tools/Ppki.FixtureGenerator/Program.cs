using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

var outputDirectory = GetOutputDirectory(args);
var fixtures = CreateFixtures();
var onlyIndex = Array.IndexOf(args, "--only");
if (onlyIndex >= 0 && onlyIndex + 1 < args.Length)
    fixtures = fixtures.Where(value => StringComparer.Ordinal.Equals(value.FileName, args[onlyIndex + 1])).ToArray();
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
    if (fixture.Kind == FixtureKind.StyleInheritance)
    {
        CreateStyleInheritanceDocument(mainPart);
        return;
    }
    if (fixture.Kind == FixtureKind.NumberedHeading)
    {
        CreateNumberedHeadingDocument(mainPart);
        return;
    }
    if (fixture.Kind == FixtureKind.DocumentSections)
    {
        CreateDocumentSectionsDocument(mainPart);
        return;
    }
    if (fixture.Kind == FixtureKind.AutoFormatProviders)
    {
        CreateAutoFormatProviderDocument(mainPart);
        return;
    }
    if (fixture.Kind == FixtureKind.DocumentPageMap)
    {
        CreateDocumentPageMapDocument(mainPart);
        return;
    }
    if (fixture.Kind == FixtureKind.ExactTextAnchor)
    {
        CreateExactTextAnchorDocument(mainPart);
        return;
    }
    if (fixture.Kind == FixtureKind.TextCorrectionBatch)
    {
        CreateTextCorrectionBatchDocument(mainPart);
        return;
    }
    if (fixture.Kind == FixtureKind.SectionPageLayoutFixers)
    {
        CreateSectionPageLayoutFixerDocument(mainPart);
        return;
    }
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

static void CreateNumberedHeadingDocument(MainDocumentPart mainPart)
{
    var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
    stylesPart.Styles = new Styles(
        CreateStyle("Normal", "Normal", true),
        HeadingStyle("Heading1", "Heading 1", 0),
        HeadingStyle("Heading2", "Heading 2", 1),
        HeadingStyle("Heading3", "Heading 3", 2),
        new Style(
            new StyleName { Val = "Synthetic Derived Heading" },
            new BasedOn { Val = "Heading2" })
        { Type = StyleValues.Paragraph, StyleId = "SyntheticDerivedHeading" });
    stylesPart.Styles.Save();

    var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
    numberingPart.Numbering = new Numbering(
        new AbstractNum(
            HeadingLevel(0, NumberFormatValues.UpperRoman, "%1.", "Heading1", 1, LevelSuffixValues.Space),
            HeadingLevel(1, NumberFormatValues.Decimal, "%1.%2", "Heading2", 1, LevelSuffixValues.Tab),
            HeadingLevel(2, NumberFormatValues.UpperLetter, "%1.%2.%3", "Heading3", 1, LevelSuffixValues.Nothing),
            new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = 10 },
        new NumberingInstance(
            new AbstractNumId { Val = 10 },
            new LevelOverride(new StartOverrideNumberingValue { Val = 3 }) { LevelIndex = 1 }) { NumberID = 10 },
        new AbstractNum(
            HeadingLevel(0, NumberFormatValues.LowerLetter, "%1)", null, 1, LevelSuffixValues.Space))
        { AbstractNumberId = 11 },
        new NumberingInstance(new AbstractNumId { Val = 11 }) { NumberID = 11 });
    numberingPart.Numbering.Save();

    var headerPart = mainPart.AddNewPart<HeaderPart>();
    headerPart.Header = new Header(StyledParagraph("Heading header sintetis", "Heading1"));
    headerPart.Header.Save();
    var headerId = mainPart.GetIdOfPart(headerPart);

    var body = new Body(
        NumberedParagraph("Judul tingkat satu sintetis", "Heading1", 10, 0),
        NumberedParagraph("Judul tingkat dua sintetis", "Heading2", 10, 1),
        NumberedParagraph("Daftar biasa sintetis", "Normal", 11, 0),
        NumberedParagraph("Judul tingkat dua kedua sintetis", "SyntheticDerivedHeading", 10, 1),
        NumberedParagraph("Judul tingkat tiga sintetis", "Heading3", 10, 2),
        NumberedParagraph("Judul taut numbering sintetis", "Normal", 10, 1),
        new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading3" }, new OutlineLevel { Val = 0 }),
            new Run(new Text("Outline langsung sintetis"))),
        new Paragraph(
            new ParagraphProperties(new OutlineLevel { Val = 2 }),
            new Run(new Text("Level terlewat sintetis"))),
        new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(new RunProperties(new Bold()), new Text("Format saja sintetis"))),
        new Paragraph(new Run(new Text("Paragraf normal sintetis"))),
        new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId },
            new PageSize { Width = 11906U, Height = 16838U },
            new PageMargin { Top = 1701, Right = 1701U, Bottom = 1701, Left = 2268U }));
    mainPart.Document = new Document(body);
    mainPart.Document.Save();
}

static void CreateDocumentSectionsDocument(MainDocumentPart mainPart)
{
    var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
    stylesPart.Styles = new Styles(
        CreateStyle("Normal", "Normal", true),
        HeadingStyle("Heading1", "Heading 1", 0),
        HeadingStyle("Heading2", "Heading 2", 1));
    stylesPart.Styles.Save();

    var headerPart = mainPart.AddNewPart<HeaderPart>();
    headerPart.Header = new Header(StyledParagraph("ABSTRACT", "Heading1"));
    headerPart.Header.Save();
    var footerPart = mainPart.AddNewPart<FooterPart>();
    footerPart.Footer = new Footer(StyledParagraph("SUMMARY", "Heading1"));
    footerPart.Footer.Save();

    var excludedTable = new Table(
        new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Dxa }),
        new TableRow(new TableCell(StyledParagraph("DAFTAR PUSTAKA", "Heading1"))));
    var body = new Body(
        new Paragraph(new Run(new Text("Halaman judul sintetis"))),
        StyledParagraph("  abstrak  ", "Heading1"),
        new Paragraph(new Run(new Text("Isi abstrak Indonesia sintetis"))),
        StyledParagraph("ABSTRACT", "Heading1"),
        new Paragraph(new Run(new Text("Synthetic English abstract body"))),
        StyledParagraph("ABSTRAK", "Heading1"),
        new Paragraph(new Run(new Text("Isi abstrak duplikat sintetis"))),
        StyledParagraph("BAB I PENDAHULUAN", "Heading1"),
        new Paragraph(new Run(new Text("Isi bab pertama sintetis"))),
        StyledParagraph("Metode", "Heading2"),
        new Paragraph(new Run(new Text("Isi metode sintetis"))),
        excludedTable,
        StyledParagraph("BAB II HASIL", "Heading1"),
        new Paragraph(new Run(new Text("Isi bab kedua sintetis"))),
        StyledParagraph("DAFTAR PUSTAKA", "Heading1"),
        new Paragraph(new Run(new Text("Referensi sintetis"))),
        StyledParagraph("LAMPIRAN", "Heading1"),
        new Paragraph(new Run(new Text("Isi lampiran sintetis"))),
        new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(headerPart) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(footerPart) },
            new PageSize { Width = 11906U, Height = 16838U },
            new PageMargin { Top = 1701, Right = 1701U, Bottom = 1701, Left = 2268U }));
    mainPart.Document = new Document(body);
    mainPart.Document.Save();
}

static void CreateAutoFormatProviderDocument(MainDocumentPart mainPart)
{
    AddStyles(mainPart);
    var hyperlink = mainPart.AddHyperlinkRelationship(new Uri("https://example.invalid/synthetic-auto-format"), true);
    var bodyParagraph = new Paragraph(
        new ParagraphProperties(
            new Justification { Val = JustificationValues.Left },
            new SpacingBetweenLines { Before = "120", After = "80", Line = "276", LineRule = LineSpacingRuleValues.Auto },
            new Indentation { Left = "720", Right = "400", Hanging = "360" }),
        new Run(new RunProperties(new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }, new FontSize { Val = "24" }), new Text("Judul ") { Space = SpaceProcessingModeValues.Preserve }),
        new Run(new RunProperties(new RunFonts { Ascii = "Arial", HighAnsi = "Arial" }, new FontSize { Val = "22" }, new Bold(), new Underline { Val = UnderlineValues.Single }), new Text("penting")),
        new Hyperlink(new Run(new RunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "20" }, new Italic()), new Text(" hari ini") { Space = SpaceProcessingModeValues.Preserve })) { Id = hyperlink.Id },
        new Run(new RunProperties(new RunFonts { Ascii = "Arial", HighAnsi = "Arial" }), new Text(string.Empty)),
        new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
        new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
        new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
    var abstractParagraph = new Paragraph(
        new ParagraphProperties(
            new SpacingBetweenLines { Before = "120", After = "80", Line = "276", LineRule = LineSpacingRuleValues.Auto }),
        new Run(new Text("Isi abstrak sintetis untuk pengujian format otomatis.")));
    var chapterHeading = StyledParagraph("BAB I PENDAHULUAN", "Heading1");
    chapterHeading.ParagraphProperties!.Append(new Justification { Val = JustificationValues.Left });
    mainPart.Document = new Document(new Body(
        bodyParagraph,
        StyledParagraph("ABSTRAK", "Heading1"),
        abstractParagraph,
        chapterHeading,
        StandardSection()));
    mainPart.Document.Save();
}

static void CreateExactTextAnchorDocument(MainDocumentPart mainPart)
{
    var hyperlink = mainPart.AddHyperlinkRelationship(new Uri("https://example.invalid/synthetic-anchor"), true);
    var duplicate = "Analisis dilakukan. Data di analisa menggunakan R. Hasil di analisa kembali.";
    var split = new Paragraph(
        new Run(new Text("Target ")),
        new Run(new Text("di ")),
        new Run(new RunProperties(new Bold()), new Text("anal")),
        new Run(new RunProperties(new Italic()), new Text("isa")),
        new Run(new Text(" selesai.")));
    var hyperlinkParagraph = new Paragraph(
        new Run(new Text("Tautan ")),
        new Hyperlink(new Run(new Text("di analisa"))) { Id = hyperlink.Id },
        new Run(new Text(" aman.")));
    var fieldAdjacent = new Paragraph(
        new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
        new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
        new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
        new Run(new Text("7")),
        new Run(new FieldChar { FieldCharType = FieldCharValues.End }),
        new Run(new Text(" di analisa")));
    var fieldResult = new Paragraph(
        new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
        new Run(new FieldCode(" REF synthetic ") { Space = SpaceProcessingModeValues.Preserve }),
        new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
        new Run(new Text("di analisa")),
        new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
    var coordinates = new Paragraph(
        new Run(new Text("A")), new Run(new TabChar()), new Run(new Text("B")),
        new Run(new Break()), new Run(new Text("C\u00a0D\u00adE \U0001F600 e\u0301 é")));
    var revision = new Paragraph(
        new Run(new Text("Awal ")),
        new InsertedRun(new Run(new Text("di analisa"))) { Id = "1", Author = "synthetic", Date = new DateTimeValue(DateTime.UnixEpoch) },
        new Run(new Text(" akhir")));
    var equivalentSplit = new Paragraph(
        new Run(new Text("Split aman di ")),
        new Run(new Text("analisa selesai.")));
    var tokenBoundaries = new Paragraph(new Run(new Text(
        "aktifitas aktifitasx xaktifitas resiko resikoo")));
    var body = new Body(
        new Paragraph(new Run(new Text(duplicate))),
        new Paragraph(new Run(new Text("Paragraf lain memuat di analisa sekali."))),
        new Paragraph(new Run(new Text("Paragraf identik di analisa."))),
        new Paragraph(new Run(new Text("Paragraf identik di analisa."))),
        split,
        hyperlinkParagraph,
        fieldAdjacent,
        fieldResult,
        coordinates,
        new Paragraph(new Run(new Text("Kalimat berulang. Kalimat berulang."))),
        revision,
        equivalentSplit,
        tokenBoundaries,
        StandardSection());
    mainPart.Document = new Document(body);
    mainPart.Document.Save();
}

static void CreateTextCorrectionBatchDocument(MainDocumentPart mainPart)
{
    CreateAutoFormatProviderDocument(mainPart);
    var hyperlink = mainPart.AddHyperlinkRelationship(new Uri("https://example.invalid/synthetic-correction"), true);
    var body = mainPart.Document!.Body!;
    var section = body.Elements<SectionProperties>().Single();
    body.InsertBefore(CorrectionParagraph(CorrectionRun(new Break { Type = BreakValues.Page })), section);
    body.InsertBefore(CorrectionParagraph(CorrectionRun(new Text(
        "Kandidat pertama di analisa dan duplikat tidak dipilih di analisa."))), section);
    body.InsertBefore(CorrectionParagraph(
        CorrectionRun(new Text("Kandidat split di ")),
        CorrectionRun(new Text("analisa untuk keputusan manual."))), section);
    body.InsertBefore(CorrectionParagraph(
        CorrectionRun(new Text("Kandidat tautan ")),
        new Hyperlink(CorrectionRun(new Text("di analisa"))) { Id = hyperlink.Id },
        CorrectionRun(new Text(" untuk diabaikan."))), section);
    body.InsertBefore(CorrectionParagraph(CorrectionRun(new Break { Type = BreakValues.Page })), section);
    body.InsertBefore(CorrectionParagraph(CorrectionRun(new Text("Halaman akhir sintetis tanpa koreksi."))), section);
    mainPart.Document.Save();
}

static Paragraph CorrectionParagraph(params OpenXmlElement[] content)
{
    var paragraph = new Paragraph(new ParagraphProperties(
        new Justification { Val = JustificationValues.Both },
        new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto },
        new Indentation { FirstLine = "567" }));
    paragraph.Append(content);
    return paragraph;
}

static Run CorrectionRun(OpenXmlElement content) => new(
    new RunProperties(new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" },
        new FontSize { Val = "24" }), content);

static void CreateDocumentPageMapDocument(MainDocumentPart mainPart)
{
    AddStyles(mainPart);
    var hyperlink = mainPart.AddHyperlinkRelationship(new Uri("https://example.invalid/page-map-fixture"), true);
    static ParagraphProperties CompliantParagraphProperties() => new(
        new Justification { Val = JustificationValues.Both },
        new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto },
        new Indentation { FirstLine = "567" });
    static Run CompliantRun(string text, bool bold = false)
    {
        var properties = new RunProperties(new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" },
            new FontSize { Val = "24" });
        if (bold) properties.Append(new Bold());
        return new Run(properties, new Text(text));
    }
    static Paragraph PageBreak() => new(CompliantParagraphProperties(), new Run(new RunProperties(
        new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }, new FontSize { Val = "24" }),
        new Break { Type = BreakValues.Page }));
    static Paragraph TextParagraph(string text) => new(CompliantParagraphProperties(), CompliantRun(text));
    var duplicate = "Penelitian ini dilakukan pada lokasi sintetis yang sama.";
    var abstractBody = new Paragraph(
        new ParagraphProperties(new SpacingBetweenLines { Before = "120", After = "80", Line = "276", LineRule = LineSpacingRuleValues.Auto }),
        new Run(new RunProperties(new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }, new FontSize { Val = "24" }), new Text("Ringkasan sintetis dengan ") { Space = SpaceProcessingModeValues.Preserve }),
        new Run(new RunProperties(new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }, new FontSize { Val = "24" }, new Bold()), new Text("format campuran")),
        new Hyperlink(new Run(new RunProperties(new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" }, new FontSize { Val = "24" }, new Italic()), new Text(" dan tautan"))) { Id = hyperlink.Id });
    var sectionBoundary = TextParagraph("Batas bagian sintetis.");
    sectionBoundary.ParagraphProperties!.Append(
        new SectionProperties(new SectionType { Val = SectionMarkValues.NextPage },
            new PageSize { Width = 11906U, Height = 16838U },
            new PageMargin { Top = 1701, Right = 1701U, Bottom = 1701, Left = 2268U }));
    var table = new Table(
        new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Dxa }),
        new TableRow(new TableCell(TextParagraph("Sel tabel sintetis untuk pemetaan struktural."))));
    var boundaryRuns = new Paragraph(CompliantParagraphProperties(),
        CompliantRun(string.Join(' ', Enumerable.Repeat("Paragraf panjang sintetis mendekati batas halaman.", 180))),
        CompliantRun(" RUN-BATAS-SINTETIS", bold: true));
    var body = new Body(
        StyledParagraph("ABSTRAK", "Heading1"), abstractBody,
        PageBreak(), TextParagraph(duplicate),
        PageBreak(), StyledParagraph("BAB I PENDAHULUAN", "Heading1"), TextParagraph("Isi bab sintetis."),
        sectionBoundary, TextParagraph("Isi setelah section break sintetis."),
        PageBreak(), table,
        PageBreak(), boundaryRuns,
        PageBreak(), TextParagraph(duplicate),
        StandardSection());
    mainPart.Document = new Document(body);
    mainPart.Document.Save();
}

static Style HeadingStyle(string id, string name, int outlineLevel) => new(
    new StyleName { Val = name },
    new StyleParagraphProperties(
        new OutlineLevel { Val = outlineLevel },
        new KeepNext { Val = true }))
{ Type = StyleValues.Paragraph, StyleId = id };

static Level HeadingLevel(
    int level,
    NumberFormatValues format,
    string text,
    string? paragraphStyleId,
    int start,
    LevelSuffixValues suffix)
{
    var definition = new Level(
        new StartNumberingValue { Val = start },
        new NumberingFormat { Val = format },
        new LevelText { Val = text },
        new LevelSuffix { Val = suffix },
        new LevelJustification { Val = LevelJustificationValues.Left },
        new PreviousParagraphProperties(new Indentation { Left = ((level + 1) * 720).ToString(), Hanging = "360" }))
    { LevelIndex = level };
    if (paragraphStyleId is not null) definition.Append(new ParagraphStyleIdInLevel { Val = paragraphStyleId });
    return definition;
}

static Paragraph NumberedParagraph(string text, string styleId, int numberId, int level) => new(
    new ParagraphProperties(
        new ParagraphStyleId { Val = styleId },
        new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = numberId })),
    new Run(new Text(text)));

static Paragraph StyledParagraph(string text, string styleId) => new(
    new ParagraphProperties(new ParagraphStyleId { Val = styleId }),
    new Run(new Text(text)));

static void CreateStyleInheritanceDocument(MainDocumentPart mainPart)
{
    var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
    stylesPart.Styles = new Styles(
        new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts
                {
                    AsciiTheme = ThemeFontValues.MinorHighAnsi,
                    HighAnsiTheme = ThemeFontValues.MinorHighAnsi,
                    EastAsiaTheme = ThemeFontValues.MinorEastAsia,
                    ComplexScriptTheme = ThemeFontValues.MinorBidi
                },
                new FontSize { Val = "22" },
                new Italic { Val = true })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { Before = "120", After = "120", Line = "240", LineRule = LineSpacingRuleValues.Auto },
                new KeepLines { Val = true }))),
        new Style(
            new StyleName { Val = "Normal" },
            new StyleParagraphProperties(new WidowControl { Val = false }),
            new StyleRunProperties(new RunFonts
            {
                AsciiTheme = ThemeFontValues.MajorHighAnsi,
                HighAnsiTheme = ThemeFontValues.MajorHighAnsi
            }))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
        new Style(
            new StyleName { Val = "Synthetic Base" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new Indentation { Left = "720" },
                new SpacingBetweenLines { Before = "360", After = "240" },
                new NumberingProperties(new NumberingLevelReference { Val = 1 }, new NumberingId { Val = 5 })),
            new StyleRunProperties(new Bold { Val = true }, new FontSize { Val = "24" }))
        { Type = StyleValues.Paragraph, StyleId = "SyntheticBase" },
        new Style(
            new StyleName { Val = "Synthetic Derived" },
            new BasedOn { Val = "SyntheticBase" },
            new StyleParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "0" }),
            new StyleRunProperties(new Bold { Val = true }, new Color { Val = "112233" }))
        { Type = StyleValues.Paragraph, StyleId = "SyntheticDerived" },
        new Style(
            new StyleName { Val = "Synthetic Character Base" },
            new StyleRunProperties(new Italic { Val = true }, new Color { Val = "445566" }))
        { Type = StyleValues.Character, StyleId = "SyntheticCharBase" },
        new Style(
            new StyleName { Val = "Synthetic Character Derived" },
            new BasedOn { Val = "SyntheticCharBase" },
            new StyleRunProperties(
                new RunFonts
                {
                    EastAsiaTheme = ThemeFontValues.MajorEastAsia,
                    ComplexScriptTheme = ThemeFontValues.MinorBidi
                },
                new FontSize { Val = "30" },
                new SmallCaps { Val = true }))
        { Type = StyleValues.Character, StyleId = "SyntheticCharDerived" });
    stylesPart.Styles.Save();

    AddStyleNumbering(mainPart);
    AddSyntheticTheme(mainPart);

    var paragraphProperties = new ParagraphProperties(
        new ParagraphStyleId { Val = "SyntheticDerived" },
        new Justification { Val = JustificationValues.Right },
        new Indentation { FirstLine = "0" },
        new KeepNext { Val = false });
    var styledRun = new Run(
        new RunProperties(
            new RunStyle { Val = "SyntheticCharDerived" },
            new RunFonts { Ascii = "DirectAscii" },
            new Italic { Val = false }),
        new Text("Teks pewarisan sintetis"));
    var defaultParagraph = new Paragraph(new Run(new Text("Teks default sintetis")));
    mainPart.Document = new Document(new Body(
        new Paragraph(paragraphProperties, styledRun),
        defaultParagraph,
        new SectionProperties()));
    mainPart.Document.Save();
}

static void AddStyleNumbering(MainDocumentPart mainPart)
{
    var part = mainPart.AddNewPart<NumberingDefinitionsPart>();
    part.Numbering = new Numbering(
        new AbstractNum(
            new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Decimal },
                new LevelText { Val = "%2." }) { LevelIndex = 1 }) { AbstractNumberId = 5 },
        new NumberingInstance(new AbstractNumId { Val = 5 }) { NumberID = 5 });
    part.Numbering.Save();
}

static void AddSyntheticTheme(MainDocumentPart mainPart)
{
    var part = mainPart.AddNewPart<ThemePart>();
    part.Theme = new A.Theme(
        new A.ThemeElements(
            new A.ColorScheme(
                new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                new A.Dark2Color(new A.RgbColorModelHex { Val = "1F497D" }),
                new A.Light2Color(new A.RgbColorModelHex { Val = "EEECE1" }),
                new A.Accent1Color(new A.RgbColorModelHex { Val = "4F81BD" }),
                new A.Accent2Color(new A.RgbColorModelHex { Val = "C0504D" }),
                new A.Accent3Color(new A.RgbColorModelHex { Val = "9BBB59" }),
                new A.Accent4Color(new A.RgbColorModelHex { Val = "8064A2" }),
                new A.Accent5Color(new A.RgbColorModelHex { Val = "4BACC6" }),
                new A.Accent6Color(new A.RgbColorModelHex { Val = "F79646" }),
                new A.Hyperlink(new A.RgbColorModelHex { Val = "0000FF" }),
                new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "800080" })) { Name = "Synthetic" },
            new A.FontScheme(
                new A.MajorFont(
                    new A.LatinFont { Typeface = "Major Latin Synthetic" },
                    new A.EastAsianFont { Typeface = "Major East Asia Synthetic" },
                    new A.ComplexScriptFont { Typeface = "Major Complex Synthetic" }),
                new A.MinorFont(
                    new A.LatinFont { Typeface = "Minor Latin Synthetic" },
                    new A.EastAsianFont { Typeface = "Minor East Asia Synthetic" },
                    new A.ComplexScriptFont { Typeface = "Minor Complex Synthetic" })) { Name = "Synthetic" },
            new A.FormatScheme(
                new A.FillStyleList(),
                new A.LineStyleList(),
                new A.EffectStyleList(),
                new A.BackgroundFillStyleList()) { Name = "Synthetic" })) { Name = "Synthetic" };
    part.Theme.Save();
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

static void CreateSectionPageLayoutFixerDocument(MainDocumentPart mainPart)
{
    AddStyles(mainPart);
    var headerPart = mainPart.AddNewPart<HeaderPart>();
    headerPart.Header = new Header(new Paragraph(new Run(new Text("Header fixer sintetis"))));
    headerPart.Header.Save();
    var footerPart = mainPart.AddNewPart<FooterPart>();
    footerPart.Footer = new Footer(new Paragraph(new Run(new Text("Footer fixer sintetis"))));
    footerPart.Footer.Save();

    var firstSection = new SectionProperties(
        new HeaderReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(headerPart) },
        new SectionType { Val = SectionMarkValues.NextPage },
        new PageSize { Width = 12240U, Height = 15840U },
        new PageMargin { Top = 1440, Right = 1441U, Bottom = 1442, Left = 1443U, Header = 701U, Footer = 702U, Gutter = 33U },
        new Columns { ColumnCount = 2, Space = "333" },
        new PageNumberType { Start = 3 });
    var firstParagraph = new Paragraph(
        new ParagraphProperties(firstSection),
        new Run(new Text("Bagian potret fixer sintetis")));

    var finalSection = new SectionProperties(
        new FooterReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(footerPart) },
        new SectionType { Val = SectionMarkValues.Continuous },
        new PageSize { Width = 15840U, Height = 12240U, Orient = PageOrientationValues.Landscape },
        new PageMargin { Top = 1450, Right = 1451U, Bottom = 1452, Left = 1453U, Header = 711U, Footer = 712U, Gutter = 44U },
        new Columns { ColumnCount = 1, Space = "444" },
        new PageNumberType { Start = 9 });
    mainPart.Document = new Document(new Body(
        firstParagraph,
        new Paragraph(new Run(new Text("Bagian lanskap fixer sintetis"))),
        finalSection));
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
        "minimal-invalid-layout-justified.docx",
        12240U,
        15840U,
        1440U,
        1440U,
        1440U,
        1440U,
        "Calibri",
        22U,
        [new ParagraphDefinition("Paragraf sintetis untuk pengujian parser.", null, JustificationValues.Both, 276U, null)], FixtureKind.Basic),
    new(
        "section-page-layout-fixers.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.SectionPageLayoutFixers),
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
        "Times New Roman", 24U, [], FixtureKind.HeaderFooter),
    new(
        "minimal-style-inheritance-layout.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.StyleInheritance),
    new(
        "minimal-numbered-heading-layout.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.NumberedHeading)
    ,new(
        "minimal-document-sections-layout.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.DocumentSections)
    ,new(
        "auto-format-provider-mixed.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.AutoFormatProviders)
    ,new(
        "document-page-map-multipage.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.DocumentPageMap)
    ,new(
        "exact-text-anchor.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.ExactTextAnchor)
    ,new(
        "text-correction-batch.docx",
        11906U, 16838U, 1701U, 1701U, 1701U, 2268U,
        "Times New Roman", 24U, [], FixtureKind.TextCorrectionBatch)
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

internal enum FixtureKind { Basic, TableField, HeaderFooter, StyleInheritance, NumberedHeading, DocumentSections, AutoFormatProviders, DocumentPageMap, ExactTextAnchor, TextCorrectionBatch, SectionPageLayoutFixers }
