using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.DocxEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class StructuralFindingExcerptTests
{
    [Fact]
    public async Task Heading_and_paragraph_materialize_from_exact_bounded_structural_locations()
    {
        var path = CreateDocument();
        try
        {
            var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
            var heading = await Materialize(path, parsed.Paragraphs[0].Location!);
            var paragraph = await Materialize(path, parsed.Paragraphs[2].Location!);

            Assert.Equal("Exact", heading.Status);
            Assert.Equal("Heading", heading.TargetType);
            Assert.Equal("BAB 2.", heading.TargetText);
            Assert.Equal("Exact", paragraph.Status);
            Assert.Equal("Paragraph", paragraph.TargetType);
            Assert.EndsWith("…", paragraph.Excerpt, StringComparison.Ordinal);
            Assert.Null(paragraph.TargetText);
            Assert.True(paragraph.Excerpt!.EnumerateRunes().Count()
                <= StructuralFindingExcerptMaterializer.MaximumExcerptScalars);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Missing_section_and_incomplete_or_stale_coordinates_fail_closed()
    {
        var path = CreateDocument();
        try
        {
            var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
            var location = parsed.Paragraphs[0].Location!;
            var missingSection = await StructuralFindingExcerptMaterializer.MaterializeAsync(path,
                JsonSerializer.Serialize(location with { ParagraphIndex = null }), CancellationToken.None);
            var wrongBody = await Materialize(path, location with { BodyElementIndex = 99 });
            var wrongPart = await Materialize(path, location with { PartUri = "/word/header1.xml" });

            Assert.Equal("Unavailable", missingSection.Status);
            Assert.Null(missingSection.Excerpt);
            Assert.Equal("Unavailable", wrongBody.Status);
            Assert.Equal("Unavailable", wrongPart.Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Paragraph_bound_section_location_materializes_as_section_but_body_level_section_does_not_guess()
    {
        var path = CreateDocument();
        try
        {
            var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
            var paragraph = parsed.Paragraphs[1].Location!;
            var sectionLocation = $"maindocument/s:{paragraph.SectionIndex}/b:{paragraph.BodyElementIndex}/p:{paragraph.ParagraphIndex}/kind:section";
            var exact = await StructuralFindingExcerptMaterializer.MaterializeAsync(path, JsonSerializer.Serialize(new
            {
                CompactLocation = sectionLocation, paragraph.SectionIndex, paragraph.BodyElementIndex,
                paragraph.ParagraphIndex, RunIndex = (int?)null
            }), CancellationToken.None);
            var bodyLevel = await StructuralFindingExcerptMaterializer.MaterializeAsync(path, JsonSerializer.Serialize(new
            {
                CompactLocation = "maindocument/s:0/b:4/kind:section", SectionIndex = 0,
                BodyElementIndex = 4, ParagraphIndex = (int?)null, RunIndex = (int?)null
            }), CancellationToken.None);

            Assert.Equal("Exact", exact.Status);
            Assert.Equal("Section", exact.TargetType);
            Assert.Equal("Unavailable", bodyLevel.Status);
            Assert.Null(bodyLevel.Excerpt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Duplicate_text_is_resolved_by_coordinates_without_global_text_search()
    {
        var path = CreateDocument();
        try
        {
            var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
            Assert.Equal(parsed.Paragraphs[1].Text, parsed.Paragraphs[3].Text);
            var first = await Materialize(path, parsed.Paragraphs[1].Location!);
            var structurallyDistinct = await Materialize(path, parsed.Paragraphs[3].Location!);

            Assert.Equal("Paragraph", first.TargetType);
            Assert.Equal("Heading", structurallyDistinct.TargetType);
            Assert.Equal(first.TargetText, structurallyDistinct.TargetText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Persisted_compact_locator_must_agree_with_every_structural_coordinate()
    {
        var path = CreateDocument();
        try
        {
            var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
            var location = parsed.Paragraphs[0].Location!;
            var persisted = JsonSerializer.Serialize(new
            {
                CompactLocation = location.ToCompactString(), location.SectionIndex,
                location.BodyElementIndex, location.ParagraphIndex, RunIndex = (int?)null
            });
            var exact = await StructuralFindingExcerptMaterializer.MaterializeAsync(path, persisted, CancellationToken.None);
            var tampered = await StructuralFindingExcerptMaterializer.MaterializeAsync(path,
                persisted.Replace("\"BodyElementIndex\":0", "\"BodyElementIndex\":1", StringComparison.Ordinal),
                CancellationToken.None);

            Assert.Equal("Exact", exact.Status);
            Assert.Equal("Unavailable", tampered.Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Materialization_is_read_only_and_response_diagnostics_are_redacted()
    {
        var path = CreateDocument();
        try
        {
            var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
            var before = await Hash(path);
            var result = await Materialize(path, parsed.Paragraphs[0].Location!);
            var after = await Hash(path);
            var dto = new StructuralFindingExcerptDto(Guid.NewGuid(), Guid.NewGuid(), result.Status,
                result.TargetType, result.Excerpt, result.TargetText, new(null, "Unavailable", null));

            Assert.Equal(before, after);
            Assert.DoesNotContain("BAB 2.", dto.ToString(), StringComparison.Ordinal);
            Assert.Contains("Content=[REDACTED]", dto.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Architecture_guards_preserve_version_binding_privacy_authorization_and_on_demand_reading()
    {
        var root = RepositoryRoot();
        var materializer = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Application", "StructuralFindingExcerptContracts.cs"));
        var service = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Infrastructure", "StructuralFindingExcerptService.cs"));
        var api = File.ReadAllText(Path.Combine(root, "backend", "services", "Ppki.Api", "Program.cs"));
        foreach (var forbidden in new[] { ".IndexOf(", "Regex", "Levenshtein", "Similarity", "Console.", "ILogger" })
            Assert.DoesNotContain(forbidden, materializer + service, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "SaveChanges", "Add(", "Update(", "source_excerpt", "localStorage", "sessionStorage" })
            Assert.DoesNotContain(forbidden, service, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RequirePpkiAdminAsync", service, StringComparison.Ordinal);
        Assert.Contains("value.AuditJob!.DocumentVersionId", service, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentVersionNo", service, StringComparison.Ordinal);
        Assert.Contains("/audits/{auditId:guid}/findings/{findingId:guid}/excerpt", api, StringComparison.Ordinal);
    }

    private static Task<(string Status, string TargetType, string? Excerpt, string? TargetText)> Materialize(
        string path, DocumentElementLocation location) => StructuralFindingExcerptMaterializer.MaterializeAsync(
            path, JsonSerializer.Serialize(location), CancellationToken.None);

    private static string CreateDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ppki-structural-excerpt-{Guid.NewGuid():N}.docx");
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        main.Document = new(new Body(
            Paragraph("BAB 2.", "Heading1"),
            Paragraph("Paragraf duplikat."),
            Paragraph(string.Concat(Enumerable.Repeat("Cuplikan deterministik panjang. ", 20))),
            Paragraph("Paragraf duplikat.", "Heading2"),
            new SectionProperties()));
        main.Document.Save();
        return path;
    }

    private static Paragraph Paragraph(string text, string? style = null) => style is null
        ? new(new Run(new Text(text)))
        : new(new ParagraphProperties(new ParagraphStyleId { Val = style }), new Run(new Text(text)));

    private static async Task<string> Hash(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "package.json"))) return current.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
