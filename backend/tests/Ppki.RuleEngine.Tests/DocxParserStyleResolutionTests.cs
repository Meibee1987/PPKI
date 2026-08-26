using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.DocxEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class DocxParserStyleResolutionTests
{
    [Fact]
    public async Task Style_fixture_exposes_defaults_catalog_theme_and_separate_direct_values()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-style-inheritance-layout");
        var parsed = await Parse(workspace.WorkingPath);

        Assert.Equal("4.0", parsed.ParserSchemaVersion);
        Assert.Equal("4.0", parsed.ProjectionSchemaVersion);
        Assert.Equal(5, parsed.Styles.Count);
        Assert.Equal([0, 1, 2, 3, 4], parsed.Styles.Select(style => style.DeclarationOrder));
        Assert.Equal(120, parsed.FormattingDefaults.Paragraph.SpacingBeforeTwips);
        Assert.True(parsed.FormattingDefaults.Paragraph.KeepLinesTogether);
        Assert.Equal(22, parsed.FormattingDefaults.Run.FontSizeHalfPoints);
        Assert.True(parsed.FormattingDefaults.Run.Italic);
        Assert.Equal("Major Latin Synthetic", parsed.ThemeFontCatalog.MajorLatin);
        Assert.Equal("Major East Asia Synthetic", parsed.ThemeFontCatalog.MajorEastAsia);
        Assert.Equal("Minor Complex Synthetic", parsed.ThemeFontCatalog.MinorComplexScript);

        var paragraph = parsed.Paragraphs[0];
        Assert.Equal(ParsedAlignment.Right, paragraph.DirectAlignment);
        Assert.Equal(ParsedAlignment.Right, paragraph.DirectFormatting!.Alignment);
        Assert.Equal(FormattingSourceKind.DirectFormatting, paragraph.EffectiveFormatting!.Alignment.Provenance.SourceKind);
        Assert.Equal(ParsedAlignment.Right, paragraph.EffectiveFormatting.Alignment.Value);
        Assert.Equal(ParsedAlignment.Center, parsed.Styles.Single(style => style.StyleId == "SyntheticDerived").ParagraphProperties!.Alignment);
    }

    [Fact]
    public async Task Paragraph_cascade_preserves_zero_false_based_on_defaults_and_numbering_provenance()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-style-inheritance-layout");
        var parsed = await Parse(workspace.WorkingPath);
        var first = parsed.Paragraphs[0].EffectiveFormatting!;
        var defaults = parsed.Paragraphs[1].EffectiveFormatting!;

        Assert.Equal(0, first.FirstLineIndentTwips.Value);
        Assert.Equal(FormattingSourceKind.DirectFormatting, first.FirstLineIndentTwips.Provenance.SourceKind);
        Assert.False(first.KeepWithNext.Value);
        Assert.Null(parsed.Paragraphs[0].DirectFormatting!.IndentLeftTwips);
        Assert.Equal(720, first.IndentLeftTwips.Value);
        Assert.Equal(FormattingSourceKind.BasedOnStyle, first.IndentLeftTwips.Provenance.SourceKind);
        Assert.Equal("SyntheticBase", first.IndentLeftTwips.Provenance.SourceStyleId);
        Assert.Equal(0, first.SpacingBeforeTwips.Value);
        Assert.Equal(FormattingSourceKind.ParagraphStyle, first.SpacingBeforeTwips.Provenance.SourceKind);
        Assert.Equal(360, parsed.Styles.Single(style => style.StyleId == "SyntheticBase").ParagraphProperties!.SpacingBeforeTwips);
        Assert.Equal(5, first.NumberingId.Value);
        Assert.Equal(1, first.NumberingLevel.Value);
        Assert.Equal(FormattingSourceKind.BasedOnStyle, first.NumberingId.Provenance.SourceKind);
        Assert.True(first.KeepLinesTogether.Value);
        Assert.Equal(FormattingSourceKind.DocumentDefault, first.KeepLinesTogether.Provenance.SourceKind);
        Assert.Equal(120, defaults.SpacingBeforeTwips.Value);
        Assert.Equal(FormattingSourceKind.DocumentDefault, defaults.SpacingBeforeTwips.Provenance.SourceKind);
    }

    [Fact]
    public async Task Run_cascade_keeps_font_slots_separate_and_applies_toggle_semantics()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-style-inheritance-layout");
        var parsed = await Parse(workspace.WorkingPath);
        var run = parsed.Paragraphs[0].RunList[0];
        var effective = run.EffectiveFormatting!;

        Assert.Equal("SyntheticCharDerived", run.DirectFormatting!.CharacterStyleId);
        Assert.Equal("DirectAscii", run.DirectFormatting.FontAscii);
        Assert.Equal("DirectAscii", effective.FontAscii.Value);
        Assert.Equal(FormattingSourceKind.DirectFormatting, effective.FontAscii.Provenance.SourceKind);
        Assert.Equal("Major Latin Synthetic", effective.FontHighAnsi.Value);
        Assert.Equal("Major East Asia Synthetic", effective.FontEastAsia.Value);
        Assert.Equal("Minor Complex Synthetic", effective.FontComplexScript.Value);
        Assert.Equal(FormattingSourceKind.Theme, effective.FontEastAsia.Provenance.SourceKind);
        Assert.Equal(30, effective.FontSizeHalfPoints.Value);
        Assert.Equal(FormattingSourceKind.CharacterStyle, effective.FontSizeHalfPoints.Provenance.SourceKind);
        Assert.False(effective.Italic.Value);
        Assert.Equal(FormattingSourceKind.DirectFormatting, effective.Italic.Provenance.SourceKind);
        Assert.False(effective.Bold.Value);
        Assert.Equal(FormattingSourceKind.ParagraphStyle, effective.Bold.Provenance.SourceKind);
        Assert.Equal("445566", effective.Color.Value);
        Assert.Equal(FormattingSourceKind.BasedOnStyle, effective.Color.Provenance.SourceKind);
        Assert.True(effective.SmallCaps.Value);

        var defaultRun = parsed.Paragraphs[1].RunList[0].EffectiveFormatting!;
        Assert.Equal(22, defaultRun.FontSizeHalfPoints.Value);
        Assert.Equal(FormattingSourceKind.DocumentDefault, defaultRun.FontSizeHalfPoints.Provenance.SourceKind);
        Assert.True(defaultRun.Italic.Value);
    }

    [Fact]
    public async Task Empty_section_never_infers_page_or_margin_defaults()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-style-inheritance-layout");
        var section = Assert.Single((await Parse(workspace.WorkingPath)).Sections);
        var effective = section.EffectiveFormatting!;

        Assert.Equal(FormattingResolutionState.Unspecified, effective.PageWidthTwips.State);
        Assert.Null(effective.PageWidthTwips.Value);
        Assert.Equal(FormattingSourceKind.Unspecified, effective.PageWidthTwips.Provenance.SourceKind);
        Assert.Equal(FormattingResolutionState.Unspecified, effective.MarginLeftTwips.State);
        Assert.Null(effective.MarginLeftTwips.Value);
    }

    [Fact]
    public async Task Missing_based_on_and_type_mismatch_are_safe_and_partial_resolution_is_marked()
    {
        var missingPath = TemporaryPath("missing-style.docx");
        var mismatchPath = TemporaryPath("mismatch-style.docx");
        try
        {
            CreatePackage(missingPath,
                [ParagraphStyle("MissingChild", "DoesNotExist", JustificationValues.Center)], "MissingChild");
            CreatePackage(mismatchPath,
                [CharacterStyle("CharacterBase"), ParagraphStyle("WrongType", "CharacterBase", JustificationValues.Right)], "WrongType");

            var missing = await Parse(missingPath);
            var mismatch = await Parse(mismatchPath);
            Assert.Contains(missing.ParserDiagnostics, diagnostic => diagnostic.Code == "style-based-on-missing");
            Assert.Equal(ParsedAlignment.Center, missing.Paragraphs[0].EffectiveFormatting!.Alignment.Value);
            Assert.Equal("style-based-on-missing", missing.Paragraphs[0].EffectiveFormatting!.Alignment.Provenance.DiagnosticCode);
            Assert.Contains(mismatch.ParserDiagnostics, diagnostic => diagnostic.Code == "style-type-mismatch");
            Assert.Equal(ParsedAlignment.Right, mismatch.Paragraphs[0].EffectiveFormatting!.Alignment.Value);
            Assert.Equal("style-type-mismatch", mismatch.Paragraphs[0].EffectiveFormatting!.Alignment.Provenance.DiagnosticCode);
        }
        finally
        {
            File.Delete(missingPath);
            File.Delete(mismatchPath);
        }
    }

    [Fact]
    public async Task Direct_and_indirect_cycles_stop_deterministically()
    {
        var directPath = TemporaryPath("direct-cycle.docx");
        var indirectPath = TemporaryPath("indirect-cycle.docx");
        try
        {
            CreatePackage(directPath, [ParagraphStyle("A", "A", JustificationValues.Center)], "A");
            CreatePackage(indirectPath,
                [ParagraphStyle("A", "B", null), ParagraphStyle("B", "A", JustificationValues.Left)], "A");
            var direct = await Parse(directPath);
            var indirect = await Parse(indirectPath);

            Assert.Contains(direct.ParserDiagnostics, diagnostic => diagnostic.Code == "style-inheritance-cycle");
            Assert.Equal(ParsedAlignment.Center, direct.Paragraphs[0].EffectiveFormatting!.Alignment.Value);
            Assert.Contains(indirect.ParserDiagnostics, diagnostic => diagnostic.Code == "style-inheritance-cycle");
            Assert.Equal(ParsedAlignment.Left, indirect.Paragraphs[0].EffectiveFormatting!.Alignment.Value);
        }
        finally
        {
            File.Delete(directPath);
            File.Delete(indirectPath);
        }
    }

    [Fact]
    public async Task Depth_limit_and_style_count_limit_are_enforced()
    {
        var path = TemporaryPath("style-limits.docx");
        try
        {
            CreatePackage(path,
                [ParagraphStyle("A", "B", null), ParagraphStyle("B", null, JustificationValues.Left)], "A");
            var depth = await new OpenXmlDocxParser(new DocxParserOptions { MaximumStyleInheritanceDepth = 1 })
                .ParseAsync(path, CancellationToken.None);
            Assert.Contains(depth.ParserDiagnostics, diagnostic => diagnostic.Code == "style-inheritance-depth-exceeded");
            Assert.Equal(FormattingResolutionState.Unresolved, depth.Paragraphs[0].EffectiveFormatting!.Alignment.State);

            var exception = await Assert.ThrowsAsync<DocxParserException>(() =>
                new OpenXmlDocxParser(new DocxParserOptions { MaximumStyleCount = 1 }).ParseAsync(path, CancellationToken.None));
            Assert.Equal("resource-limit-exceeded", exception.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Duplicate_style_uses_first_declaration_and_reports_safe_diagnostic()
    {
        var path = TemporaryPath("duplicate-style.docx");
        try
        {
            CreatePackage(path,
                [ParagraphStyle("Duplicate", null, JustificationValues.Left), ParagraphStyle("Duplicate", null, JustificationValues.Right)],
                "Duplicate");
            var parsed = await Parse(path);
            Assert.Single(parsed.Styles);
            Assert.Equal(ParsedAlignment.Left, parsed.Paragraphs[0].EffectiveFormatting!.Alignment.Value);
            var diagnostic = Assert.Single(parsed.ParserDiagnostics, value => value.Code == "style-id-duplicate");
            Assert.Equal("Duplicate", Assert.Single(diagnostic.Metadata!).Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_theme_is_unresolved_without_font_lookup_or_sensitive_text_in_diagnostics()
    {
        const string marker = "SENSITIVE-SYNTHETIC-STYLE-TEXT";
        var path = TemporaryPath("missing-theme.docx");
        try
        {
            var style = CharacterStyle("ThemeCharacter");
            style.StyleRunProperties = new StyleRunProperties(new RunFonts { EastAsiaTheme = ThemeFontValues.MajorEastAsia });
            CreatePackage(path, [style], null, "ThemeCharacter", marker);
            var parsed = await Parse(path);
            var value = parsed.Paragraphs[0].RunList[0].EffectiveFormatting!.FontEastAsia;
            Assert.Equal(FormattingResolutionState.Unresolved, value.State);
            Assert.Equal("theme-font-unresolved", value.Provenance.DiagnosticCode);
            Assert.Contains(parsed.ParserDiagnostics, diagnostic => diagnostic.Code == "theme-font-unresolved");
            var serialized = System.Text.Json.JsonSerializer.Serialize(parsed.ParserDiagnostics);
            Assert.DoesNotContain(marker, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(path, serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Effective_projection_is_text_free_repeatable_parallel_and_fixture_is_immutable()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-style-inheritance-layout");
        var before = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var first = await Parse(workspace.WorkingPath);
        var second = await Parse(workspace.WorkingPath);
        var projection = ParsedDocumentCanonicalProjection.Serialize(first);

        Assert.Equal(projection, ParsedDocumentCanonicalProjection.Serialize(second));
        Assert.Equal(ParsedDocumentCanonicalProjection.Sha256(first), ParsedDocumentCanonicalProjection.Sha256(second));
        Assert.Contains("EffectiveFormatting", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("Teks pewarisan sintetis", projection, StringComparison.Ordinal);
        var sharedParser = new OpenXmlDocxParser();
        var parallel = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
            () => sharedParser.ParseAsync(workspace.WorkingPath, CancellationToken.None))));
        Assert.Single(parallel.Select(ParsedDocumentCanonicalProjection.Sha256).Distinct(StringComparer.Ordinal));
        Assert.Equal(before, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
    }

    [Fact]
    public async Task All_synthetic_fixtures_remain_parseable()
    {
        var manifest = await DocxFixtureManifest.LoadAsync(DocxFixtureWorkspace.FixtureRoot);
        Assert.Equal(14, manifest.Fixtures.Count);
        foreach (var fixture in manifest.Fixtures)
        {
            await using var workspace = await DocxFixtureWorkspace.CreateAsync(fixture.FixtureId);
            _ = await Parse(workspace.WorkingPath);
        }
    }

    [Fact]
    public void Style_options_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumStyleCount = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumStyleInheritanceDepth = 0 }));
    }

    private static Task<ParsedDocument> Parse(string path) =>
        new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);

    private static Style ParagraphStyle(string id, string? basedOn, JustificationValues? alignment)
    {
        var style = new Style(new StyleName { Val = id }) { Type = StyleValues.Paragraph, StyleId = id };
        if (basedOn is not null) style.Append(new BasedOn { Val = basedOn });
        if (alignment is not null) style.Append(new StyleParagraphProperties(new Justification { Val = alignment }));
        return style;
    }

    private static Style CharacterStyle(string id)
        => new(new StyleName { Val = id }) { Type = StyleValues.Character, StyleId = id };

    private static void CreatePackage(
        string path,
        IReadOnlyList<Style> styles,
        string? paragraphStyleId,
        string? characterStyleId = null,
        string text = "Konten style sintetis")
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var stylePart = main.AddNewPart<StyleDefinitionsPart>();
        stylePart.Styles = new Styles(styles.Select(style => style.CloneNode(true)));
        stylePart.Styles.Save();
        var paragraphProperties = new ParagraphProperties();
        if (paragraphStyleId is not null) paragraphProperties.Append(new ParagraphStyleId { Val = paragraphStyleId });
        var runProperties = new RunProperties();
        if (characterStyleId is not null) runProperties.Append(new RunStyle { Val = characterStyleId });
        main.Document = new Document(new Body(
            new Paragraph(paragraphProperties, new Run(runProperties, new Text(text))),
            new SectionProperties()));
        main.Document.Save();
    }

    private static string TemporaryPath(string filename)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ppki-style-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, filename);
    }
}
