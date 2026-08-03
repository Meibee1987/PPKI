using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.DocxEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class DocxParserNumberingOutlineTests
{
    [Fact]
    public async Task Numbered_heading_fixture_exposes_complete_catalog_and_deterministic_labels()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-numbered-heading-layout");
        var parsed = await Parse(workspace.WorkingPath);
        var catalog = parsed.FullNumberingCatalog;

        Assert.Equal("4.0", parsed.ParserSchemaVersion);
        Assert.Equal("4.0", parsed.ProjectionSchemaVersion);
        Assert.Equal(2, catalog.AbstractDefinitions.Count);
        Assert.Equal(2, catalog.Instances.Count);
        var headingAbstract = catalog.AbstractDefinitions.Single(value => value.AbstractNumberingId == 10);
        Assert.Equal("hybridMultilevel", headingAbstract.MultiLevelType);
        Assert.Equal([0, 1, 2], headingAbstract.Levels.Select(value => value.Level));
        Assert.Equal(ParsedNumberingFormat.UpperRoman, headingAbstract.Levels[0].Format);
        Assert.Equal(ParsedNumberingFormat.Decimal, headingAbstract.Levels[1].Format);
        Assert.Equal(ParsedNumberingFormat.UpperLetter, headingAbstract.Levels[2].Format);
        Assert.Equal("Heading2", headingAbstract.Levels[1].ParagraphStyleId);
        Assert.Equal(1440, headingAbstract.Levels[1].IndentLeftTwips);
        Assert.Equal(360, headingAbstract.Levels[1].HangingIndentTwips);
        var instance = catalog.Instances.Single(value => value.NumberingId == 10);
        Assert.Equal(10, instance.AbstractNumberingId);
        Assert.Equal(3, Assert.Single(instance.LevelOverrides).StartOverride);

        Assert.Equal("I.", parsed.Paragraphs[0].EffectiveNumbering!.Label!.Value);
        Assert.Equal("I.3", parsed.Paragraphs[1].EffectiveNumbering!.Label!.Value);
        Assert.Equal("a)", parsed.Paragraphs[2].EffectiveNumbering!.Label!.Value);
        Assert.Equal("I.4", parsed.Paragraphs[3].EffectiveNumbering!.Label!.Value);
        Assert.Equal("I.4.A", parsed.Paragraphs[4].EffectiveNumbering!.Label!.Value);
        Assert.Equal("I.5", parsed.Paragraphs[5].EffectiveNumbering!.Label!.Value);
        Assert.EndsWith(" ", parsed.Paragraphs[0].EffectiveNumbering!.Label!.ValueWithSuffix, StringComparison.Ordinal);
        Assert.EndsWith("\t", parsed.Paragraphs[1].EffectiveNumbering!.Label!.ValueWithSuffix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Heading_evidence_distinguishes_structural_headings_from_numbered_and_formatted_paragraphs()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-numbered-heading-layout");
        var parsed = await Parse(workspace.WorkingPath);

        Assert.Equal(7, parsed.Headings.Count);
        Assert.DoesNotContain(parsed.Headings, value => value.ParagraphIndex == 2);
        Assert.DoesNotContain(parsed.Headings, value => value.ParagraphIndex == 8);
        Assert.Contains(parsed.Headings.Single(value => value.ParagraphIndex == 0).Evidence,
            value => value.Kind == HeadingEvidenceKind.ParagraphStyleOutlineLevel);
        Assert.Contains(parsed.Headings.Single(value => value.ParagraphIndex == 3).Evidence,
            value => value.Kind == HeadingEvidenceKind.BasedOnHeadingStyle);
        Assert.Contains(parsed.Headings.Single(value => value.ParagraphIndex == 5).Evidence,
            value => value.Kind == HeadingEvidenceKind.NumberingLevelLinkedToHeadingStyle);
        var direct = parsed.Headings.Single(value => value.ParagraphIndex == 6);
        Assert.Equal(1, direct.Level);
        Assert.True(parsed.Headings[0].StartsNewSection);
        Assert.All(parsed.Headings.Skip(1), heading => Assert.False(heading.StartsNewSection));
        Assert.Contains(direct.Evidence, value => value.Kind == HeadingEvidenceKind.DirectOutlineLevel);
        Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "heading-evidence-conflict");
        Assert.DoesNotContain(parsed.Headings, value => value.Location.PartKind is DocumentPartKind.Header or DocumentPartKind.Footer);
    }

    [Fact]
    public async Task Outline_tree_preserves_body_order_parentage_siblings_locations_and_skipped_level_diagnostic()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-numbered-heading-layout");
        var parsed = await Parse(workspace.WorkingPath);
        var outline = parsed.DocumentOutline;

        Assert.Equal(7, outline.NodeCount);
        Assert.Equal(2, outline.RootNodes.Count);
        Assert.Equal(1, outline.RootNodes[0].Level);
        Assert.Equal(3, outline.RootNodes[0].Children.Count);
        Assert.Equal([1, 3, 5], outline.RootNodes[0].Children.Select(node => parsed.Headings[node.HeadingIndex].ParagraphIndex));
        Assert.Single(outline.RootNodes[0].Children[1].Children);
        Assert.Equal(4, parsed.Headings[outline.RootNodes[0].Children[1].Children[0].HeadingIndex].ParagraphIndex);
        Assert.Equal(6, parsed.Headings[outline.RootNodes[1].HeadingIndex].ParagraphIndex);
        Assert.Single(outline.RootNodes[1].Children);
        Assert.Equal(7, parsed.Headings[outline.RootNodes[1].Children[0].HeadingIndex].ParagraphIndex);
        Assert.All(Flatten(outline.RootNodes), node => Assert.Contains("kind:paragraph", node.Location.ToCompactString(), StringComparison.Ordinal));
        Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "heading-level-skipped");
        Assert.Equal(parsed.Headings.OrderBy(value => value.Order).Select(value => value.ParagraphIndex),
            parsed.Headings.Select(value => value.ParagraphIndex));
    }

    [Fact]
    public async Task Effective_numbering_uses_direct_then_style_and_preserves_zero_disabled_and_provenance()
    {
        var path = TemporaryPath("numbering-precedence.docx");
        try
        {
            var styles = new Styles(
                ParagraphStyle("Normal", true),
                new Style(
                    new StyleName { Val = "Style Numbered" },
                    new StyleParagraphProperties(new NumberingProperties(
                        new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })))
                { Type = StyleValues.Paragraph, StyleId = "StyleNumbered" });
            var numbering = new Numbering(
                Abstract(1, LevelDefinition(0, NumberFormatValues.Decimal, "%1.")),
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
                Abstract(2, LevelDefinition(0, NumberFormatValues.UpperLetter, "%1.")),
                new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 });
            CreatePackage(path, styles, numbering,
                Numbered("Direct", "StyleNumbered", 2, 0),
                Styled("Style", "StyleNumbered"),
                Numbered("Disabled", "StyleNumbered", 0, 0),
                Styled("Missing", "Normal"),
                new Paragraph(
                    new ParagraphProperties(
                        new ParagraphStyleId { Val = "Normal" },
                        new NumberingProperties(new NumberingId { Val = 1 })),
                    new Run(new Text("Missing level"))));

            var parsed = await Parse(path);
            var direct = parsed.Paragraphs[0].EffectiveNumbering!;
            var styled = parsed.Paragraphs[1].EffectiveNumbering!;
            Assert.Equal(2, direct.NumberingId);
            Assert.Equal(0, direct.Level);
            Assert.Equal("A.", direct.Label!.Value);
            Assert.Equal(FormattingSourceKind.DirectFormatting, direct.Provenance.SourceKind);
            Assert.Equal(1, styled.NumberingId);
            Assert.Equal(FormattingSourceKind.ParagraphStyle, styled.Provenance.SourceKind);
            Assert.Equal("StyleNumbered", styled.Provenance.SourceStyleId);
            Assert.Equal(NumberingResolutionState.Disabled, parsed.Paragraphs[2].EffectiveNumbering!.State);
            Assert.True(parsed.Paragraphs[2].EffectiveNumbering!.IsExplicitlyDisabled);
            Assert.Equal(NumberingResolutionState.Unspecified, parsed.Paragraphs[3].EffectiveNumbering!.State);
            Assert.Equal(NumberingResolutionState.Unresolved, parsed.Paragraphs[4].EffectiveNumbering!.State);
            Assert.Equal("numbering-level-missing", parsed.Paragraphs[4].EffectiveNumbering!.DiagnosticCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Label_engine_supports_lower_roman_lower_letter_bullet_none_and_resets_deeper_counters()
    {
        var path = TemporaryPath("numbering-formats.docx");
        try
        {
            var bullet = LevelDefinition(0, NumberFormatValues.Bullet, "•");
            var none = LevelDefinition(0, NumberFormatValues.None, string.Empty);
            var legalLevel = LevelDefinition(1, NumberFormatValues.Decimal, "%1.%2");
            legalLevel.Append(new IsLegalNumberingStyle { Val = true });
            var numbering = new Numbering(
                Abstract(1, LevelDefinition(0, NumberFormatValues.LowerRoman, "%1.")),
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
                Abstract(2, LevelDefinition(0, NumberFormatValues.LowerLetter, "%1)")),
                new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 },
                Abstract(3, bullet), new NumberingInstance(new AbstractNumId { Val = 3 }) { NumberID = 3 },
                Abstract(4, none), new NumberingInstance(new AbstractNumId { Val = 4 }) { NumberID = 4 },
                Abstract(5,
                    LevelDefinition(0, NumberFormatValues.Decimal, "%1."),
                    LevelDefinition(1, NumberFormatValues.Decimal, "%1.%2")),
                new NumberingInstance(new AbstractNumId { Val = 5 }) { NumberID = 5 },
                Abstract(6,
                    LevelDefinition(0, NumberFormatValues.Decimal, "%1."),
                    LevelDefinition(1, NumberFormatValues.Decimal, "%1.%2", restart: 0)),
                new NumberingInstance(new AbstractNumId { Val = 6 }) { NumberID = 6 },
                Abstract(7, LevelDefinition(0, NumberFormatValues.UpperRoman, "%1."), legalLevel),
                new NumberingInstance(new AbstractNumId { Val = 7 }) { NumberID = 7 });
            CreatePackage(path, BasicStyles(), numbering,
                Numbered("Roman", "Normal", 1, 0),
                Numbered("Letter", "Normal", 2, 0),
                Numbered("Bullet", "Normal", 3, 0),
                Numbered("None", "Normal", 4, 0),
                Numbered("Parent", "Normal", 5, 0),
                Numbered("Child", "Normal", 5, 1),
                Numbered("Child two", "Normal", 5, 1),
                Numbered("Parent two", "Normal", 5, 0),
                Numbered("Child reset", "Normal", 5, 1),
                Numbered("No restart parent", "Normal", 6, 0),
                Numbered("No restart child", "Normal", 6, 1),
                Numbered("No restart parent two", "Normal", 6, 0),
                Numbered("No restart child two", "Normal", 6, 1),
                Numbered("Legal parent", "Normal", 7, 0),
                Numbered("Legal child", "Normal", 7, 1));

            var labels = (await Parse(path)).Paragraphs.Select(value => value.EffectiveNumbering?.Label?.Value).ToArray();
            Assert.Equal("i.", labels[0]);
            Assert.Equal("a)", labels[1]);
            Assert.Equal("•", labels[2]);
            Assert.Equal(string.Empty, labels[3]);
            Assert.Equal("1.", labels[4]);
            Assert.Equal("1.1", labels[5]);
            Assert.Equal("1.2", labels[6]);
            Assert.Equal("2.", labels[7]);
            Assert.Equal("2.1", labels[8]);
            Assert.Equal("1.", labels[9]);
            Assert.Equal("1.1", labels[10]);
            Assert.Equal("2.", labels[11]);
            Assert.Equal("2.2", labels[12]);
            Assert.Equal("I.", labels[13]);
            Assert.Equal("1.1", labels[14]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Duplicate_missing_and_malformed_numbering_are_safe_deterministic_and_private()
    {
        const string marker = "SENSITIVE-SYNTHETIC-NUMBERING-MARKER";
        var path = TemporaryPath("malformed-numbering.docx");
        try
        {
            var unsupported = LevelDefinition(0, NumberFormatValues.Decimal, "%1.");
            unsupported.NumberingFormat!.SetAttribute(new OpenXmlAttribute("w", "val", WordNamespace, "chicago"));
            var numbering = new Numbering(
                Abstract(1, LevelDefinition(0, NumberFormatValues.Decimal, "%1.")),
                Abstract(1, LevelDefinition(0, NumberFormatValues.UpperRoman, "%1.")),
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
                new NumberingInstance(new AbstractNumId { Val = 99 }) { NumberID = 2 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 3 },
                Abstract(4, unsupported), new NumberingInstance(new AbstractNumId { Val = 4 }) { NumberID = 4 },
                Abstract(5, LevelDefinition(0, NumberFormatValues.Decimal, "%0")),
                new NumberingInstance(new AbstractNumId { Val = 5 }) { NumberID = 5 });
            CreatePackage(path, BasicStyles(), numbering,
                Numbered(marker, "Normal", 1, 0),
                Numbered(marker, "Normal", 2, 0),
                Numbered(marker, "Normal", 3, 1),
                Numbered(marker, "Normal", 4, 0),
                Numbered(marker, "Normal", 5, 0),
                Numbered(marker, "Normal", 99, 0));

            var parsed = await Parse(path);
            Assert.Equal(ParsedNumberingFormat.Decimal, parsed.FullNumberingCatalog.AbstractDefinitions[0].Levels[0].Format);
            Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "numbering-definition-duplicate");
            Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "abstract-numbering-missing");
            Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "numbering-level-missing");
            Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "numbering-format-unsupported");
            Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "numbering-level-text-invalid");
            Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "numbering-instance-missing");
            var diagnostics = System.Text.Json.JsonSerializer.Serialize(parsed.ParserDiagnostics);
            Assert.DoesNotContain(marker, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(path, diagnostics, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Empty_invalid_and_formatting_only_heading_cases_are_safe()
    {
        var path = TemporaryPath("heading-edge-cases.docx");
        try
        {
            var styles = new Styles(
                ParagraphStyle("Normal", true),
                new Style(new StyleName { Val = "Structured" },
                    new StyleParagraphProperties(new OutlineLevel { Val = 1 }))
                { Type = StyleValues.Paragraph, StyleId = "Structured" });
            var invalid = new Paragraph(
                new ParagraphProperties(new OutlineLevel { Val = 9 }),
                new Run(new Text("Invalid sintetis")));
            var formatted = new Paragraph(
                new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                new Run(new RunProperties(new Bold()), new Text("Format sintetis")));
            CreatePackage(path, styles, null,
                new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Structured" }), new Run(new Text(string.Empty))),
                invalid,
                formatted,
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Structured" }),
                    new Run(new RunProperties(new Vanish()), new Text("Hidden sintetis"))));

            var parsed = await Parse(path);
            Assert.Single(parsed.Headings);
            Assert.Equal(2, parsed.Headings[0].Level);
            Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "heading-empty");
            Assert.Contains(parsed.ParserDiagnostics, value => value.Code == "heading-level-invalid");
            Assert.DoesNotContain(parsed.Headings, value => value.ParagraphIndex == 2);
            Assert.DoesNotContain(parsed.Headings, value => value.ParagraphIndex == 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Numbering_and_outline_limits_are_positive_and_enforced()
    {
        await using var fixture = await DocxFixtureWorkspace.CreateAsync("minimal-numbered-heading-layout");
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        {
            MaximumAbstractNumberingDefinitions = 1
        }).ParseAsync(fixture.WorkingPath, CancellationToken.None));
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        {
            MaximumNumberingInstances = 1
        }).ParseAsync(fixture.WorkingPath, CancellationToken.None));
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        {
            MaximumNumberingLevels = 1
        }).ParseAsync(fixture.WorkingPath, CancellationToken.None));
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        {
            MaximumOutlineNodes = 1
        }).ParseAsync(fixture.WorkingPath, CancellationToken.None));

        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumAbstractNumberingDefinitions = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumNumberingInstances = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumNumberingLevels = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenXmlDocxParser(new DocxParserOptions { MaximumOutlineNodes = 0 }));
    }

    [Fact]
    public async Task Projection_is_text_free_repeatable_parallel_state_isolated_and_fixture_immutable()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-numbered-heading-layout");
        var before = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var parser = new OpenXmlDocxParser();
        var first = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var second = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var projection = ParsedDocumentCanonicalProjection.Serialize(first);
        Assert.Equal(projection, ParsedDocumentCanonicalProjection.Serialize(second));
        Assert.Equal(ParsedDocumentCanonicalProjection.Sha256(first), ParsedDocumentCanonicalProjection.Sha256(second));
        Assert.Contains("NumberingCatalog", projection, StringComparison.Ordinal);
        Assert.Contains("Headings", projection, StringComparison.Ordinal);
        Assert.Contains("Outline", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("Judul tingkat satu sintetis", projection, StringComparison.Ordinal);

        var parallel = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
            () => parser.ParseAsync(workspace.WorkingPath, CancellationToken.None))));
        Assert.Single(parallel.Select(ParsedDocumentCanonicalProjection.Sha256).Distinct(StringComparer.Ordinal));
        Assert.All(parallel, value => Assert.Equal("I.", value.Paragraphs[0].EffectiveNumbering!.Label!.Value));
        Assert.Equal(before, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
    }

    [Fact]
    public async Task All_eight_fixtures_remain_parseable_and_headers_do_not_enter_outline()
    {
        var manifest = await DocxFixtureManifest.LoadAsync(DocxFixtureWorkspace.FixtureRoot);
        Assert.Equal(8, manifest.Fixtures.Count);
        foreach (var definition in manifest.Fixtures)
        {
            await using var workspace = await DocxFixtureWorkspace.CreateAsync(definition.FixtureId);
            var parsed = await Parse(workspace.WorkingPath);
            Assert.DoesNotContain(parsed.Headings, heading => heading.Location.PartKind is DocumentPartKind.Header or DocumentPartKind.Footer);
            if (definition.FixtureId == "minimal-heading-layout")
            {
                Assert.Equal(2, parsed.Headings.Count);
                Assert.All(parsed.Headings, heading => Assert.Contains(heading.Evidence,
                    evidence => evidence.Kind == HeadingEvidenceKind.BuiltInHeadingStyle));
            }
        }
    }

    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static Task<ParsedDocument> Parse(string path) =>
        new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);

    private static IEnumerable<DocumentOutlineNode> Flatten(IEnumerable<DocumentOutlineNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private static Styles BasicStyles() => new(ParagraphStyle("Normal", true));

    private static Style ParagraphStyle(string id, bool isDefault) => new(new StyleName { Val = id })
    { Type = StyleValues.Paragraph, StyleId = id, Default = isDefault };

    private static AbstractNum Abstract(int id, params Level[] levels) => new(levels.Select(value => value.CloneNode(true)))
    { AbstractNumberId = id };

    private static Level LevelDefinition(int level, NumberFormatValues format, string text, int? restart = null)
    {
        var definition = new Level(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = format },
            new LevelText { Val = text },
            new LevelSuffix { Val = LevelSuffixValues.Nothing })
        { LevelIndex = level };
        if (restart is not null) definition.Append(new LevelRestart { Val = restart.Value });
        return definition;
    }

    private static Paragraph Styled(string text, string styleId) => new(
        new ParagraphProperties(new ParagraphStyleId { Val = styleId }),
        new Run(new Text(text)));

    private static Paragraph Numbered(string text, string styleId, int numberId, int level) => new(
        new ParagraphProperties(
            new ParagraphStyleId { Val = styleId },
            new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = numberId })),
        new Run(new Text(text)));

    private static void CreatePackage(string path, Styles styles, Numbering? numbering, params Paragraph[] paragraphs)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        document.PackageProperties.Creator = string.Empty;
        document.PackageProperties.LastModifiedBy = string.Empty;
        var main = document.AddMainDocumentPart();
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = (Styles)styles.CloneNode(true);
        stylesPart.Styles.Save();
        if (numbering is not null)
        {
            var numberingPart = main.AddNewPart<NumberingDefinitionsPart>();
            numberingPart.Numbering = (Numbering)numbering.CloneNode(true);
            numberingPart.Numbering.Save();
        }
        main.Document = new Document(new Body(
            paragraphs.Select(value => value.CloneNode(true)).Append(new SectionProperties())));
        main.Document.Save();
    }

    private static string TemporaryPath(string filename)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ppki-numbering-outline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, filename);
    }
}
