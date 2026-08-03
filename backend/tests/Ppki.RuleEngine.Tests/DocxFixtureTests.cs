using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Ppki.DocxEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class DocxFixtureTests
{
    [Fact]
    public async Task Minimal_compliant_fixture_opens_and_parses_offline_without_mutating_original()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-compliant-layout");

        using (var document = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var mainPart = Assert.IsType<MainDocumentPart>(document.MainDocumentPart);
            var mainDocument = Assert.IsType<DocumentFormat.OpenXml.Wordprocessing.Document>(mainPart.Document);
            Assert.NotNull(mainDocument.Body);
        }

        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var section = Assert.Single(parsed.Sections);
        Assert.Equal(21m, section.WidthCm);
        Assert.Equal(29.7m, section.HeightCm);
        Assert.Equal(3m, section.MarginTopCm);
        Assert.Equal(3m, section.MarginRightCm);
        Assert.Equal(3m, section.MarginBottomCm);
        Assert.Equal(4m, section.MarginLeftCm);
        Assert.Contains(parsed.Paragraphs, paragraph => paragraph.Text.Contains("sintetis", StringComparison.OrdinalIgnoreCase));
        var paragraph = Assert.Single(parsed.Paragraphs);
        Assert.Equal("Times New Roman", paragraph.FontName);
        Assert.Equal(12m, paragraph.FontSizePt);
        Assert.Equal(1m, paragraph.LineSpacingMultiple);
        Assert.Equal(1m, paragraph.FirstLineIndentCm);
        Assert.Equal("both", paragraph.Alignment, ignoreCase: true);
    }

    [Fact]
    public async Task Heading_fixture_contains_heading_subheading_and_normal_paragraph()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-heading-layout");

        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);

        Assert.Contains(parsed.Paragraphs, paragraph => paragraph.StyleId == "Heading1" && paragraph.IsHeading);
        Assert.Contains(parsed.Paragraphs, paragraph => paragraph.StyleId == "Heading2" && paragraph.IsHeading);
        Assert.Contains(parsed.Paragraphs, paragraph => !paragraph.IsHeading && paragraph.Alignment.Equals("both", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Invalid_layout_fixture_exposes_its_deliberately_non_compliant_properties()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");

        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var section = Assert.Single(parsed.Sections);
        var paragraph = Assert.Single(parsed.Paragraphs);

        Assert.Equal(21.59m, section.WidthCm);
        Assert.Equal(27.94m, section.HeightCm);
        Assert.Equal(2.54m, section.MarginLeftCm);
        Assert.Equal("Calibri", paragraph.FontName);
        Assert.Equal(11m, paragraph.FontSizePt);
        Assert.Equal(1.15m, paragraph.LineSpacingMultiple);
        Assert.Null(paragraph.FirstLineIndentCm);
        Assert.Equal("left", paragraph.Alignment, ignoreCase: true);
    }

    [Fact]
    public async Task Unknown_fixture_id_has_a_clear_error()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => DocxFixtureWorkspace.CreateAsync("unknown-fixture"));

        Assert.Contains("Unknown DOCX fixture id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_fixture_file_has_a_clear_error()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ppki-docx-fixture-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "generated"));
        File.Copy(Path.Combine(DocxFixtureWorkspace.FixtureRoot, "manifest.json"), Path.Combine(temporaryRoot, "manifest.json"));

        try
        {
            var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => DocxFixtureWorkspace.CreateAsync("minimal-compliant-layout", temporaryRoot));
            Assert.Contains("DOCX fixture file is missing", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Mutating_a_working_copy_does_not_change_the_original_fixture()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-compliant-layout");
        Assert.NotEqual(workspace.OriginalPath, workspace.WorkingPath);

        await using (var stream = new FileStream(workspace.WorkingPath, FileMode.Append, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(new byte[] { 0 });
        }

        var originalChecksum = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var copyChecksum = await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath);
        Assert.Equal(workspace.OriginalChecksum, originalChecksum);
        Assert.NotEqual(originalChecksum, copyChecksum);
    }

    [Fact]
    public async Task Manifest_declares_only_synthetic_non_personal_fixtures()
    {
        var manifest = await DocxFixtureManifest.LoadAsync(DocxFixtureWorkspace.FixtureRoot);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(6, manifest.Fixtures.Count);
        Assert.All(manifest.Fixtures, fixture =>
        {
            Assert.True(fixture.Synthetic);
            Assert.False(fixture.ContainsPersonalData);
            Assert.False(string.IsNullOrWhiteSpace(fixture.GeneratorVersion));
        });
    }

    [Fact]
    public async Task Fixtures_have_no_personal_metadata_or_secret_patterns_and_stay_small()
    {
        var manifest = await DocxFixtureManifest.LoadAsync(DocxFixtureWorkspace.FixtureRoot);
        foreach (var fixture in manifest.Fixtures)
        {
            await using var workspace = await DocxFixtureWorkspace.CreateAsync(fixture.FixtureId);
            Assert.InRange(new FileInfo(workspace.OriginalPath).Length, 1, 64 * 1024);

            using var document = WordprocessingDocument.Open(workspace.WorkingPath, false);
            Assert.True(string.IsNullOrWhiteSpace(document.PackageProperties.Creator));
            Assert.True(string.IsNullOrWhiteSpace(document.PackageProperties.LastModifiedBy));
            Assert.Null(document.ExtendedFilePropertiesPart);
            Assert.Null(document.CustomFilePropertiesPart);

            using var archive = ZipFile.OpenRead(workspace.WorkingPath);
            foreach (var entry in archive.Entries)
            {
                await using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                var content = await reader.ReadToEndAsync();
                Assert.False(content.Contains("sb_secret_", StringComparison.OrdinalIgnoreCase));
                Assert.False(content.Contains("Bearer eyJ", StringComparison.OrdinalIgnoreCase));
                Assert.False(content.Contains("Password=", StringComparison.OrdinalIgnoreCase));
                Assert.False(content.Contains("-----BEGIN ", StringComparison.Ordinal));
            }
        }
    }
}
