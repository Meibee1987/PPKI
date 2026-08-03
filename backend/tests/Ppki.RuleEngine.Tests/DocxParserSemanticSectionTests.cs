using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.DocxEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class DocxParserSemanticSectionTests
{
    [Fact]
    public async Task Section_fixture_exposes_catalog_abstracts_zones_ranges_and_observed_systematics()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-document-sections-layout");
        var parsed = await Parse(workspace.WorkingPath);
        var structure = parsed.DocumentStructure;

        Assert.Equal("4.0", parsed.ParserSchemaVersion);
        Assert.Equal("4.0", parsed.ProjectionSchemaVersion);
        Assert.Equal("1.0", structure.CatalogVersion);
        Assert.Equal(8, structure.Sections.Count);
        Assert.Equal(3, structure.AbstractSections.Count);
        Assert.Single(structure.ExcludedCandidates);
        Assert.Equal(SemanticSectionEvidenceKind.ExcludedTableHeading, structure.ExcludedCandidates[0].Reason);

        Assert.Equal([
            SemanticSectionKind.AbstractIndonesian,
            SemanticSectionKind.AbstractEnglish,
            SemanticSectionKind.AbstractIndonesian,
            SemanticSectionKind.Chapter,
            SemanticSectionKind.Methods,
            SemanticSectionKind.Chapter,
            SemanticSectionKind.References,
            SemanticSectionKind.Appendices
        ], structure.Sections.Select(value => value.Kind).Where(value => value != SemanticSectionKind.OtherMainMatter));
        Assert.All(structure.Sections.Take(3), value => Assert.Equal(SemanticSectionZone.FrontMatter, value.Zone));
        Assert.All(structure.Sections.Skip(3).Take(3), value => Assert.Equal(SemanticSectionZone.MainMatter, value.Zone));
        Assert.All(structure.Sections.Skip(6), value => Assert.Equal(SemanticSectionZone.BackMatter, value.Zone));
        Assert.Equal(2, parsed.Systematics.DetectedChapterCount);
        Assert.Equal(structure.Sections.Select(value => value.Index), parsed.Systematics.OrderedSections.Select(value => value.SectionIndex));
        Assert.NotNull(parsed.Systematics.FrontMatterStart);
        Assert.NotNull(parsed.Systematics.MainMatterStart);
        Assert.NotNull(parsed.Systematics.BackMatterStart);
    }

    [Fact]
    public async Task Abstract_language_range_count_duplicate_and_chapter_parentage_are_deterministic()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-document-sections-layout");
        var parsed = await Parse(workspace.WorkingPath);
        var abstracts = parsed.DocumentStructure.AbstractSections;

        Assert.Equal(SemanticSectionLanguage.Indonesian, abstracts[0].Language);
        Assert.Equal(SemanticSectionLanguage.English, abstracts[1].Language);
        Assert.Equal(SemanticSectionLanguage.Indonesian, abstracts[2].Language);
        Assert.All(abstracts, value => Assert.Equal(1, value.ParagraphCount));
        Assert.All(abstracts, value => Assert.True(value.EndLocation.BodyElementIndex <
            parsed.DocumentStructure.Sections[value.SectionIndex + 1].HeadingLocation.BodyElementIndex));
        Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "abstract-section-duplicate");
        Assert.Equal(parsed.DocumentStructure.Sections[0].DuplicateGroup, parsed.DocumentStructure.Sections[2].DuplicateGroup);

        var method = parsed.DocumentStructure.Sections.Single(value => value.Kind == SemanticSectionKind.Methods);
        var parent = parsed.DocumentStructure.Sections[method.ParentSectionIndex!.Value];
        Assert.Equal(SemanticSectionKind.Chapter, parent.Kind);
        Assert.True(method.Range.EndBodyElementIndex < parsed.DocumentStructure.Sections[5].Range.StartBodyElementIndex);
        Assert.True(parent.Range.EndBodyElementIndex >= method.Range.EndBodyElementIndex);
        Assert.Contains(parsed.BodyElementOrder, value => value.Kind == ParsedBodyElementKind.Table
            && value.Index >= method.Range.StartBodyElementIndex && value.Index <= method.Range.EndBodyElementIndex);
    }

    [Fact]
    public async Task Exact_aliases_case_whitespace_and_structured_numbering_prefix_are_supported_without_body_substrings()
    {
        var path = TemporaryPath("semantic-aliases.docx");
        try
        {
            CreatePackage(path,
                Heading("  abstrak  "), Body("Isi biasa"),
                Heading("ABSTRACT"), Body("Isi biasa"),
                Heading("RINGKASAN"), Body("Isi biasa"),
                NumberedHeading("1. SUMMARY"), Body("Isi biasa"),
                Body("Kata ABSTRAK dan SUMMARY hanya muncul pada isi"));
            var parsed = await Parse(path);
            Assert.Equal([
                SemanticSectionKind.AbstractIndonesian,
                SemanticSectionKind.AbstractEnglish,
                SemanticSectionKind.SummaryIndonesian,
                SemanticSectionKind.SummaryEnglish
            ], parsed.DocumentStructure.Sections.Select(value => value.Kind));
            Assert.Equal(4, parsed.DocumentStructure.Sections.Count);
            Assert.All(parsed.DocumentStructure.Sections, value =>
                Assert.Equal(SemanticClassificationBasis.ExactAlias, value.ClassificationBasis));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Chapter_detection_requires_structural_heading_and_preserves_evidence_and_body_order()
    {
        var path = TemporaryPath("chapter-evidence.docx");
        try
        {
            CreatePackage(path,
                Heading("BAB I PENDAHULUAN"),
                Body("BAB II bukan heading"),
                Formatted("BAB III hanya format"),
                NumberedBody("Daftar bernomor biasa"),
                DirectOutline("CHAPTER 2 METHODS"));
            var parsed = await Parse(path);
            var chapters = parsed.DocumentStructure.Sections.Where(value => value.Kind == SemanticSectionKind.Chapter).ToArray();
            Assert.Equal(2, chapters.Length);
            Assert.Equal([0, 4], chapters.Select(value => value.BodyOrderIndex));
            Assert.Contains(chapters[1].Evidence, value => value.Kind == SemanticSectionEvidenceKind.DirectOutline);
            Assert.DoesNotContain(parsed.Headings, value => value.ParagraphIndex is 1 or 2 or 3);

            await using var unknownWorkspace = await DocxFixtureWorkspace.CreateAsync("minimal-numbered-heading-layout");
            var unknown = await Parse(unknownWorkspace.WorkingPath);
            Assert.Contains(unknown.DocumentStructure.Sections, value => value.HeadingLevel == 1
                && value.ClassificationState == SemanticClassificationState.Candidate
                && value.Kind == SemanticSectionKind.OtherMainMatter);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Long_empty_duplicate_and_zone_regression_cases_emit_only_safe_diagnostics()
    {
        const string marker = "SENSITIVE-SEMANTIC-HEADING-MARKER";
        var path = TemporaryPath("semantic-edge-cases.docx");
        try
        {
            CreatePackage(path,
                Heading("BAB I PENDAHULUAN"),
                Heading("ABSTRAK"),
                Heading("ABSTRAK"), Body("Isi"),
                Heading(marker));
            var parsed = await new OpenXmlDocxParser(new DocxParserOptions
            {
                MaximumSemanticHeadingLength = 32
            }).ParseAsync(path, CancellationToken.None);
            var codes = parsed.ParserDiagnostics.Select(value => value.Code).ToArray();
            Assert.Contains("semantic-zone-regression", codes);
            Assert.Contains("semantic-section-ambiguous", codes);
            Assert.Contains("semantic-section-empty", codes);
            Assert.Contains("abstract-section-empty", codes);
            Assert.Contains("semantic-section-duplicate", codes);
            Assert.Contains("abstract-section-duplicate", codes);
            Assert.Contains("semantic-heading-too-long", codes);
            var diagnostics = System.Text.Json.JsonSerializer.Serialize(parsed.ParserDiagnostics);
            Assert.DoesNotContain(marker, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, ParsedDocumentCanonicalProjection.Serialize(parsed), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Malformed_duplicate_heading_boundary_is_diagnosed_without_crossing_range()
    {
        var path = TemporaryPath("semantic-overlap.docx");
        try
        {
            CreatePackage(path, Heading("ABSTRAK"), Body("Isi"));
            var parsed = await Parse(path);
            var heading = Assert.Single(parsed.Headings);
            var result = new SemanticDocumentStructureDetector(10, 1000, 512, 10).Detect(
                parsed.Paragraphs, parsed.BodyElementOrder, [heading, heading]);
            Assert.Contains(result.Diagnostics, value => value.Code == "semantic-section-overlap");
            Assert.Contains(result.Diagnostics, value => value.Code == "semantic-section-boundary-unresolved");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Semantic_limits_are_positive_enforced_and_do_not_leak_between_parses()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-document-sections-layout");
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        { MaximumSemanticSections = 1 }).ParseAsync(workspace.WorkingPath, CancellationToken.None));
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        { MaximumSectionAliases = 1 }).ParseAsync(workspace.WorkingPath, CancellationToken.None));
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        { MaximumSystematicsEntries = 1 }).ParseAsync(workspace.WorkingPath, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumSemanticSections = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumSectionAliases = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumSemanticHeadingLength = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumSystematicsEntries = 0 }));
    }

    [Fact]
    public async Task Projection_is_text_safe_repeatable_parallel_and_fixture_is_immutable()
    {
        const string abstractBody = "Isi abstrak Indonesia sintetis";
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-document-sections-layout");
        var checksum = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var parser = new OpenXmlDocxParser();
        var first = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var second = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var projection = ParsedDocumentCanonicalProjection.Serialize(first);
        Assert.Equal(projection, ParsedDocumentCanonicalProjection.Serialize(second));
        Assert.Equal(ParsedDocumentCanonicalProjection.Sha256(first), ParsedDocumentCanonicalProjection.Sha256(second));
        Assert.Contains("SemanticStructure", projection, StringComparison.Ordinal);
        Assert.Contains("Systematics", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("ABSTRAK", projection, StringComparison.Ordinal);
        Assert.DoesNotContain(abstractBody, projection, StringComparison.Ordinal);

        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(
            () => parser.ParseAsync(workspace.WorkingPath, CancellationToken.None))));
        Assert.Single(parallel.Select(ParsedDocumentCanonicalProjection.Sha256).Distinct(StringComparer.Ordinal));
        Assert.All(parallel, value => Assert.Equal(8, value.DocumentStructure.Sections.Count));
        Assert.Equal(checksum, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
    }

    [Fact]
    public async Task Existing_fixtures_worker_contract_and_header_footer_exclusions_remain_compatible()
    {
        var manifest = await DocxFixtureManifest.LoadAsync(DocxFixtureWorkspace.FixtureRoot);
        Assert.Equal(8, manifest.Fixtures.Count);
        foreach (var fixture in manifest.Fixtures)
        {
            await using var workspace = await DocxFixtureWorkspace.CreateAsync(fixture.FixtureId);
            var parsed = await Parse(workspace.WorkingPath);
            Assert.DoesNotContain(parsed.DocumentStructure.Sections,
                value => value.HeadingLocation.PartKind is DocumentPartKind.Header or DocumentPartKind.Footer);
        }
    }

    [Fact]
    public async Task Footnote_endnote_and_comment_headings_are_not_semantic_sources_and_systematics_has_no_verdict()
    {
        const string marker = "SYNTHETIC-EXCLUDED-NOTE-HEADING";
        var path = TemporaryPath("semantic-note-exclusions.docx");
        try
        {
            CreatePackage(path, Body("Main body without headings"));
            using (var document = WordprocessingDocument.Open(path, true))
            {
                var main = document.MainDocumentPart!;
                var footnotes = main.AddNewPart<FootnotesPart>();
                footnotes.Footnotes = new Footnotes(new Footnote(Heading(marker)) { Id = 1 });
                footnotes.Footnotes.Save();
                var endnotes = main.AddNewPart<EndnotesPart>();
                endnotes.Endnotes = new Endnotes(new Endnote(Heading(marker)) { Id = 1 });
                endnotes.Endnotes.Save();
                var comments = main.AddNewPart<WordprocessingCommentsPart>();
                comments.Comments = new Comments(new Comment(Heading(marker)) { Id = "0", Author = string.Empty });
                comments.Comments.Save();
            }
            var parsed = await Parse(path);
            Assert.Empty(parsed.DocumentStructure.Sections);
            Assert.Empty(parsed.Systematics.OrderedSections);
            Assert.DoesNotContain(marker, ParsedDocumentCanonicalProjection.Serialize(parsed), StringComparison.Ordinal);
            Assert.DoesNotContain(parsed.ParserDiagnostics, value => value.Code.Contains("missing-section", StringComparison.Ordinal));
            var forbiddenVerdictNames = new[] { "Pass", "Fail", "Violation", "Compliance", "Score", "Severity" };
            Assert.DoesNotContain(typeof(DocumentSystematics).GetProperties(), property =>
                forbiddenVerdictNames.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
            Assert.DoesNotContain(typeof(DocumentSystematicsEntry).GetProperties(), property =>
                forbiddenVerdictNames.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
        }
        finally { File.Delete(path); }
    }

    private static Task<ParsedDocument> Parse(string path) =>
        new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);

    private static Paragraph Heading(string text) => new(
        new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text(text)));

    private static Paragraph DirectOutline(string text) => new(
        new ParagraphProperties(new OutlineLevel { Val = 0 }), new Run(new Text(text)));

    private static Paragraph Body(string text) => new(new Run(new Text(text)));

    private static Paragraph Formatted(string text) => new(
        new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
        new Run(new RunProperties(new Bold()), new Text(text)));

    private static Paragraph NumberedHeading(string text) => new(
        new ParagraphProperties(
            new ParagraphStyleId { Val = "Heading1" },
            new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
        new Run(new Text(text)));

    private static Paragraph NumberedBody(string text) => new(
        new ParagraphProperties(
            new ParagraphStyleId { Val = "Normal" },
            new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
        new Run(new Text(text)));

    private static string TemporaryPath(string filename) => Path.Combine(
        Path.GetTempPath(), $"ppki-semantic-{Guid.NewGuid():N}-{filename}");

    private static void CreatePackage(string path, params OpenXmlElement[] elements)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new Style(new StyleName { Val = "Normal" })
            { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(new StyleName { Val = "Heading 1" },
                new StyleParagraphProperties(new OutlineLevel { Val = 0 }))
            { Type = StyleValues.Paragraph, StyleId = "Heading1" });
        styles.Styles.Save();
        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new Numbering(
            new AbstractNum(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Decimal },
                new LevelText { Val = "%1." }) { LevelIndex = 0 }) { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
        numbering.Numbering.Save();
        main.Document = new Document(new Body(elements.Concat<OpenXmlElement>([new SectionProperties()])));
        main.Document.Save();
    }
}
