using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.DocxEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class DocxParserContractTests
{
    [Fact]
    public async Task Golden_raw_units_alignment_and_locations_are_exact()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-compliant-layout");
        var parsed = await Parse(workspace);
        var section = Assert.Single(parsed.Sections);
        var paragraph = Assert.Single(parsed.Paragraphs);
        var run = Assert.Single(paragraph.RunList);

        Assert.Equal("3.0", parsed.ParserSchemaVersion);
        Assert.Equal("3.0", parsed.ProjectionSchemaVersion);
        Assert.Equal(11906, section.PageWidthTwips);
        Assert.Equal(16838, section.PageHeightTwips);
        Assert.Equal(1701, section.MarginTopTwips);
        Assert.Equal(1701, section.MarginRightTwips);
        Assert.Equal(1701, section.MarginBottomTwips);
        Assert.Equal(2268, section.MarginLeftTwips);
        Assert.Equal(ParsedAlignment.Justified, paragraph.DirectAlignment);
        Assert.Equal(567, paragraph.DirectFirstLineIndentTwips);
        Assert.Equal(240, paragraph.DirectLineSpacingValue);
        Assert.Equal(24, run.DirectFontSizeHalfPoints);
        Assert.Equal("maindocument/s:0/b:0/p:0/kind:paragraph", paragraph.Location!.ToCompactString());
        Assert.Equal("maindocument/s:0/b:0/p:0/r:0/kind:run", run.Location.ToCompactString());
    }

    [Fact]
    public async Task Heading_paragraph_and_run_order_is_stable_without_trimming_semantic_text()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-heading-layout");
        var parsed = await Parse(workspace);

        Assert.Equal([0, 1, 2], parsed.Paragraphs.Select(item => item.Index));
        Assert.Equal(["Heading1", "Heading2", null], parsed.Paragraphs.Select(item => item.StyleId));
        Assert.All(parsed.Paragraphs, paragraph => Assert.Equal([0], paragraph.RunList.Select(run => run.Index)));
        Assert.Equal([0, 1, 2], parsed.BodyElementOrder.Where(item => item.Kind == ParsedBodyElementKind.Paragraph).Select(item => item.Index));
    }

    [Fact]
    public async Task Table_field_numbering_break_and_drawing_inventory_is_structured()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-table-field-layout");
        var parsed = await Parse(workspace);
        var table = Assert.Single(parsed.TableInventory);
        var row = Assert.Single(table.Rows);

        Assert.Equal(2, row.Cells.Count);
        Assert.All(row.Cells, cell => Assert.Single(cell.ParagraphIndexes));
        Assert.Equal([2500L, 2500L], table.GridColumnWidthsTwips);
        var numbered = parsed.Paragraphs.Single(paragraph => paragraph.NumberingReference is not null);
        Assert.Equal(1, numbered.NumberingReference!.NumberingId);
        Assert.Equal(0, numbered.NumberingReference.Level);
        Assert.Contains(parsed.Numbering, item => item.NumberingId == 1 && item.AbstractNumberingId == 1);
        Assert.Contains(parsed.Styles, item => item.StyleId == "Normal");
        Assert.True(numbered.HasTabs);
        Assert.True(numbered.HasBreaks);
        Assert.Contains(numbered.RunList.SelectMany(run => run.Breaks), item => item == ParsedBreakKind.TextWrapping);
        Assert.Contains(numbered.RunList.SelectMany(run => run.Breaks), item => item == ParsedBreakKind.Page);
        var drawing = Assert.Single(parsed.DrawingInventory);
        Assert.Equal(ParsedDrawingKind.Inline, drawing.Kind);
        Assert.Equal("image/png", drawing.ContentType);
        Assert.Equal(9525, drawing.WidthEmu);
        Assert.Equal(9525, drawing.HeightEmu);
        Assert.False(drawing.HasExternalRelationship);
        var field = Assert.Single(parsed.FieldInventory);
        Assert.Equal(ParsedFieldKind.Page, field.Kind);
        Assert.Equal("PAGE", field.NormalizedInstruction);
        Assert.True(field.HasBegin && field.HasSeparate && field.HasEnd);
        Assert.Contains(parsed.Paragraphs.SelectMany(item => item.RunList), run => run.FieldIndexes.Contains(field.Index));
        Assert.Equal(1, parsed.Counts.FootnoteReferences);
        Assert.Equal(1, parsed.Counts.EndnoteReferences);
        Assert.Equal(1, parsed.Counts.CommentReferences);
    }

    [Fact]
    public async Task Paragraph_level_and_final_sections_map_header_footer_references()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-header-footer-layout");
        var parsed = await Parse(workspace);

        Assert.Equal(2, parsed.Sections.Count);
        Assert.False(parsed.Sections[0].IsBodyLevel);
        Assert.True(parsed.Sections[1].IsBodyLevel);
        Assert.Single(parsed.Sections[0].HeaderFooterReferenceList);
        Assert.Single(parsed.Sections[1].HeaderFooterReferenceList);
        Assert.Equal(2, parsed.HeaderFooterInventory.Count);
        Assert.Contains(parsed.HeaderFooterInventory, item => item.PartKind == DocumentPartKind.Header);
        Assert.Contains(parsed.HeaderFooterInventory, item => item.PartKind == DocumentPartKind.Footer);
        Assert.All(parsed.HeaderFooterInventory, item => Assert.Single(item.Paragraphs));
    }

    [Fact]
    public async Task Canonical_projection_is_text_free_and_repeatable()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-table-field-layout");
        var first = await Parse(workspace);
        var second = await Parse(workspace);
        var firstProjection = ParsedDocumentCanonicalProjection.Serialize(first);
        var secondProjection = ParsedDocumentCanonicalProjection.Serialize(second);

        Assert.Equal(firstProjection, secondProjection);
        Assert.Equal(ParsedDocumentCanonicalProjection.Sha256(first), ParsedDocumentCanonicalProjection.Sha256(second));
        Assert.DoesNotContain("Sel satu sintetis", firstProjection, StringComparison.Ordinal);
        Assert.DoesNotContain("Daftar sintetis", firstProjection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parallel_parse_is_deterministic()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-heading-layout");
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None));
        var parsed = await Task.WhenAll(tasks);
        var hashes = parsed.Select(ParsedDocumentCanonicalProjection.Sha256).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Single(hashes);
    }

    [Fact]
    public async Task Paragraph_and_run_limits_fail_with_safe_errors()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-heading-layout");
        var paragraphParser = new OpenXmlDocxParser(new DocxParserOptions { MaximumParagraphs = 1 });
        var paragraphError = await Assert.ThrowsAsync<DocxParserException>(() => paragraphParser.ParseAsync(workspace.WorkingPath, CancellationToken.None));
        Assert.Equal("resource-limit-exceeded", paragraphError.Code);
        Assert.DoesNotContain(workspace.WorkingPath, paragraphError.Message, StringComparison.OrdinalIgnoreCase);

        var runParser = new OpenXmlDocxParser(new DocxParserOptions { MaximumRuns = 1 });
        var runError = await Assert.ThrowsAsync<DocxParserException>(() => runParser.ParseAsync(workspace.WorkingPath, CancellationToken.None));
        Assert.Equal("resource-limit-exceeded", runError.Code);
        Assert.DoesNotContain(workspace.WorkingPath, runError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Input_table_relationship_and_diagnostic_limits_are_enforced()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-table-field-layout");
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        {
            MaximumInputBytes = 1
        }).ParseAsync(workspace.WorkingPath, CancellationToken.None));
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        {
            MaximumExpandedPackageBytes = 1
        }).ParseAsync(workspace.WorkingPath, CancellationToken.None));
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        {
            MaximumPackageEntries = 1
        }).ParseAsync(workspace.WorkingPath, CancellationToken.None));
        await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
        {
            MaximumRelationships = 1
        }).ParseAsync(workspace.WorkingPath, CancellationToken.None));

        var tablePath = TemporaryPath("table-limit.docx");
        var diagnosticPath = TemporaryPath("diagnostic-limit.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(tablePath, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Table(new TableRow(new TableCell(new Paragraph()))),
                    new Table(new TableRow(new TableCell(new Paragraph()))),
                    new SectionProperties()));
                main.Document.Save();
            }
            var tableError = await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser(new DocxParserOptions
            {
                MaximumTables = 1
            }).ParseAsync(tablePath, CancellationToken.None));
            Assert.Equal("resource-limit-exceeded", tableError.Code);

            using (var document = WordprocessingDocument.Create(diagnosticPath, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                var pageSize = new PageSize();
                pageSize.SetAttribute(new OpenXmlAttribute("w", "w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main", "bad"));
                pageSize.SetAttribute(new OpenXmlAttribute("w", "h", "http://schemas.openxmlformats.org/wordprocessingml/2006/main", "bad"));
                var margin = new PageMargin();
                margin.SetAttribute(new OpenXmlAttribute("w", "top", "http://schemas.openxmlformats.org/wordprocessingml/2006/main", "bad"));
                main.Document = new Document(new Body(new Paragraph(), new SectionProperties(pageSize, margin)));
                main.Document.Save();
            }
            var diagnostics = await new OpenXmlDocxParser(new DocxParserOptions { MaximumDiagnostics = 2 })
                .ParseAsync(diagnosticPath, CancellationToken.None);
            Assert.Equal(2, diagnostics.ParserDiagnostics.Count);
            Assert.Equal("diagnostics-truncated", diagnostics.ParserDiagnostics[^1].Code);
        }
        finally
        {
            File.Delete(tablePath);
            File.Delete(diagnosticPath);
        }
    }

    [Fact]
    public async Task Missing_main_part_and_corrupt_package_fail_safely()
    {
        var missingMain = TemporaryPath("missing-main.docx");
        var missingBody = TemporaryPath("missing-body.docx");
        var corrupt = TemporaryPath("corrupt.docx");
        try
        {
            using (WordprocessingDocument.Create(missingMain, WordprocessingDocumentType.Document)) { }
            using (var document = WordprocessingDocument.Create(missingBody, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document();
                main.Document.Save();
            }
            await File.WriteAllBytesAsync(corrupt, Encoding.UTF8.GetBytes("synthetic-sensitive-marker-not-a-package"));

            var missingError = await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser().ParseAsync(missingMain, CancellationToken.None));
            var missingBodyError = await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser().ParseAsync(missingBody, CancellationToken.None));
            var corruptError = await Assert.ThrowsAsync<DocxParserException>(() => new OpenXmlDocxParser().ParseAsync(corrupt, CancellationToken.None));
            Assert.Equal("main-part-missing", missingError.Code);
            Assert.Equal("body-missing", missingBodyError.Code);
            Assert.Equal("package-invalid", corruptError.Code);
            Assert.DoesNotContain("synthetic-sensitive-marker", corruptError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(corrupt, corruptError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(missingMain);
            File.Delete(missingBody);
            File.Delete(corrupt);
        }
    }

    [Fact]
    public async Task External_relationship_is_inventoried_but_never_resolved()
    {
        var path = TemporaryPath("external.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("Konten sintetis"))), new SectionProperties()));
                main.AddExternalRelationship("external", new Uri("https://127.0.0.1:1/must-not-be-contacted"), "rExternal");
                main.Document.Save();
            }

            var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
            Assert.Equal(1, parsed.Counts.ExternalRelationships);
            Assert.Contains(parsed.ParserDiagnostics, item => item.Code == "external-relationship-ignored");
            Assert.DoesNotContain("127.0.0.1", ParsedDocumentCanonicalProjection.Serialize(parsed), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Diagnostics_never_contain_document_text_or_file_path()
    {
        var path = TemporaryPath("diagnostic.docx");
        const string sensitiveMarker = "SENSITIVE-SYNTHETIC-PARAGRAPH-MARKER";
        try
        {
            using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                var pageSize = new PageSize();
                pageSize.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("w", "w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main", "invalid"));
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Text(sensitiveMarker))),
                    new SectionProperties(pageSize)));
                main.Document.Save();
            }
            var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
            var diagnostics = System.Text.Json.JsonSerializer.Serialize(parsed.ParserDiagnostics);
            Assert.Contains(parsed.ParserDiagnostics, item => item.Code == "numeric-value-invalid");
            Assert.DoesNotContain(sensitiveMarker, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(path, diagnostics, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Parser_does_not_modify_original_fixture_checksum()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-compliant-layout");
        var before = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        _ = await Parse(workspace);
        var after = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        Assert.Equal(before, after);
        Assert.Equal(workspace.OriginalChecksum, after);
    }

    private static Task<ParsedDocument> Parse(DocxFixtureWorkspace workspace) =>
        new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);

    private static string TemporaryPath(string filename)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ppki-docx-parser-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, filename);
    }
}
