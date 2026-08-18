using System.Reflection;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class ExactTextAnchorTests
{
    private static readonly Guid VersionOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly ExactTextAnchorMaterializer _materializer = new();

    [Fact]
    public async Task Golden_fixture_proves_exact_duplicate_structural_and_split_run_targeting()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        Assert.Equal("4.0", parsed.ParserSchemaVersion);
        Assert.Equal(13, parsed.Paragraphs.Count);

        var first = await BuildForText(workspace, parsed, 0, "di analisa", 0);
        var second = await BuildForText(workspace, parsed, 0, "di analisa", 1);
        Assert.Equal(ExactTextTargetStatus.Exact, first.Status);
        Assert.Equal(ExactTextTargetStatus.Exact, second.Status);
        Assert.NotEqual(first.Anchor!.Start, second.Anchor!.Start);
        Assert.NotEqual(first.Anchor.TargetFingerprint + first.Anchor.Start, second.Anchor.TargetFingerprint + second.Anchor.Start);

        var separate = await BuildForText(workspace, parsed, 1, "di analisa", 0);
        Assert.NotEqual(first.Anchor.ParagraphLocation.ToCompactString(), separate.Anchor!.ParagraphLocation.ToCompactString());
        var identicalA = await BuildForText(workspace, parsed, 2, "di analisa", 0);
        var identicalB = await BuildForText(workspace, parsed, 3, "di analisa", 0);
        Assert.Equal(identicalA.Anchor!.ParagraphFingerprint, identicalB.Anchor!.ParagraphFingerprint);
        Assert.NotEqual(identicalA.Anchor.ParagraphLocation.ToCompactString(), identicalB.Anchor.ParagraphLocation.ToCompactString());

        var split = await BuildForText(workspace, parsed, 4, "di analisa", 0);
        Assert.Equal(3, split.Segments.Count);
        Assert.Collection(split.Segments,
            value => { Assert.False(value.IsBold); Assert.False(value.IsItalic); },
            value => Assert.True(value.IsBold),
            value => Assert.True(value.IsItalic));
        Assert.Equal(10, split.Segments.Sum(value => value.CanonicalLength));

        var repeatedA = await BuildForText(workspace, parsed, 9, "Kalimat berulang.", 0);
        var repeatedB = await BuildForText(workspace, parsed, 9, "Kalimat berulang.", 1);
        Assert.NotEqual(repeatedA.Anchor!.Start, repeatedB.Anchor!.Start);
    }

    [Fact]
    public async Task Hyperlinks_controls_and_unicode_follow_the_versioned_scalar_model()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);

        var hyperlink = await BuildForText(workspace, parsed, 5, "di analisa", 0);
        Assert.All(hyperlink.Segments, value => Assert.True(value.IsHyperlink));

        var controls = parsed.Paragraphs[8].Text;
        var tab = await BuildForText(workspace, parsed, 8, "\t", 0);
        var lineBreak = await BuildForText(workspace, parsed, 8, "\n", 0);
        var nbsp = await BuildForText(workspace, parsed, 8, "\u00a0", 0);
        var softHyphen = await BuildForText(workspace, parsed, 8, "\u00ad", 0);
        var nonBmp = await BuildForText(workspace, parsed, 8, "\U0001F600", 0);
        Assert.Equal("tab", Assert.Single(tab.Segments).NodeKind);
        Assert.Equal("line-break", Assert.Single(lineBreak.Segments).NodeKind);
        Assert.Equal(1, Assert.Single(nbsp.Segments).CanonicalLength);
        Assert.Equal("text", Assert.Single(softHyphen.Segments).NodeKind);
        Assert.Equal(1, nonBmp.Anchor!.Length);
        Assert.True(controls.Contains("C\u00a0D", StringComparison.Ordinal));

        var decomposed = await BuildForText(workspace, parsed, 8, "e\u0301", 0);
        var composed = await BuildForText(workspace, parsed, 8, "é", 0);
        Assert.Equal(2, decomposed.Anchor!.Length);
        Assert.Equal(1, composed.Anchor!.Length);
        Assert.NotEqual(decomposed.Anchor.TargetFingerprint, composed.Anchor.TargetFingerprint);
        Assert.Equal("wordprocessingml-visible-text/scalar-none/1.0", decomposed.Anchor.TextModelVersion);
    }

    [Fact]
    public async Task Field_adjacent_text_is_exact_while_field_results_and_revisions_fail_closed()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);

        var adjacent = await BuildForText(workspace, parsed, 6, "di analisa", 0);
        var fieldResult = await BuildForText(workspace, parsed, 7, "di analisa", 0);
        var revision = await BuildForText(workspace, parsed, 10, "di analisa", 0);
        Assert.Equal(ExactTextTargetStatus.Exact, adjacent.Status);
        Assert.Equal(ExactTextTargetStatus.Unsupported, fieldResult.Status);
        Assert.Equal("unsupported-content-overlap", fieldResult.SafeReason);
        Assert.Equal(ExactTextTargetStatus.Unsupported, revision.Status);
    }

    [Fact]
    public async Task Materialization_is_read_only_deterministic_and_serializes_without_raw_text()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var before = await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath);
        var first = await BuildForText(workspace, parsed, 4, "di analisa", 0);
        var second = await BuildForText(workspace, parsed, 4, "di analisa", 0);
        var resolved = await _materializer.ResolveAsync(workspace.WorkingPath, VersionOne, first.Anchor!);
        var after = await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath);

        Assert.Equal(ExactTextTargetStatus.Exact, resolved.Status);
        Assert.True(first.Anchor!.Spans.SequenceEqual(second.Anchor!.Spans));
        Assert.Equal(first.Anchor!.SerializeCanonical(), second.Anchor!.SerializeCanonical());
        Assert.Equal(first.Anchor.AnchorHash, second.Anchor.AnchorHash);
        Assert.DoesNotContain("di analisa", first.Anchor.SerializeCanonical(), StringComparison.Ordinal);
        Assert.Equal(before, after);
        Assert.Equal(first.Segments, resolved.Segments);
    }

    [Fact]
    public async Task Version_sha_paragraph_target_and_context_preconditions_never_relocate()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var built = await BuildForText(workspace, parsed, 0, "di analisa", 1);
        var anchor = built.Anchor!;

        var wrongVersion = await _materializer.ResolveAsync(workspace.WorkingPath, Guid.Parse("22222222-2222-2222-2222-222222222222"), anchor);
        Assert.Equal("document-version-mismatch", wrongVersion.SafeReason);

        MutateFirstParagraphCharacter(workspace.WorkingPath);
        var shaMismatch = await _materializer.ResolveAsync(workspace.WorkingPath, VersionOne, anchor);
        Assert.Equal("source-sha-mismatch", shaMismatch.SafeReason);
        var currentSha = await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath);
        var forgedSourcePrecondition = anchor with { SourceSha256 = currentSha.ToLowerInvariant() };
        var changedParagraph = await _materializer.ResolveAsync(workspace.WorkingPath, VersionOne, forgedSourcePrecondition);
        Assert.Equal("paragraph-fingerprint-mismatch", changedParagraph.SafeReason);

        await using var clean = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var targetChanged = await _materializer.ResolveAsync(clean.WorkingPath, VersionOne,
            anchor with { TargetFingerprint = new string('0', 64) });
        var contextChanged = await _materializer.ResolveAsync(clean.WorkingPath, VersionOne,
            anchor with { PrefixFingerprint = new string('0', 64) });
        Assert.Equal("target-fingerprint-mismatch", targetChanged.SafeReason);
        Assert.Equal("context-fingerprint-mismatch", contextChanged.SafeReason);
    }

    [Fact]
    public async Task Page_map_location_is_compatible_but_page_number_is_not_anchor_identity()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var built = await BuildForText(workspace, parsed, 4, "di analisa", 0);
        var page = new PageMapRenderEntry(built.Anchor!.ParagraphLocation.ToCompactString(),
            built.Anchor.ParagraphLocation.SectionIndex, built.Anchor.ParagraphLocation.BodyElementIndex,
            built.Anchor.ParagraphLocation.ParagraphIndex, null, null, null, null, PageMapConfidence.Exact, 23, null);
        Assert.Equal(page.StructuralLocation, built.Anchor.ParagraphLocation.ToCompactString());
        Assert.DoesNotContain(typeof(ExactTextAnchor).GetProperties(), value =>
            value.Name.Contains("Page", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Architecture_guard_forbids_relocation_replacement_client_authority_and_persisted_raw_text()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "ExactTextAnchors.cs"));
        foreach (var forbidden in new[] { ".Replace(", "Regex.Replace", ".IndexOf(", "Levenshtein", "Similarity", "HttpClient", "EntityFrameworkCore" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        Assert.DoesNotContain("client", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(ExactTextAnchor).GetProperties(), value =>
            value.Name is "Text" or "ParagraphText" or "SourceText" or "ReplacementText");
        Assert.DoesNotContain(typeof(ExactTextSourceSpan).GetProperties(), value => value.PropertyType == typeof(string)
            && value.Name.Contains("Text", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ExactTextTargetResult> BuildForText(
        DocxFixtureWorkspace workspace,
        ParsedDocument parsed,
        int paragraphIndex,
        string target,
        int occurrence)
    {
        var text = parsed.Paragraphs[paragraphIndex].Text;
        var utf16Index = -1;
        var searchFrom = 0;
        for (var current = 0; current <= occurrence; current++)
        {
            utf16Index = text.IndexOf(target, searchFrom, StringComparison.Ordinal);
            Assert.True(utf16Index >= 0);
            searchFrom = utf16Index + target.Length;
        }
        var scalarStart = text[..utf16Index].EnumerateRunes().Count();
        var scalarLength = target.EnumerateRunes().Count();
        return await _materializer.BuildAsync(workspace.WorkingPath, VersionOne, workspace.OriginalChecksum,
            paragraphIndex, scalarStart, scalarLength);
    }

    private static void MutateFirstParagraphCharacter(string path)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var mainDocument = document.MainDocumentPart!.Document!;
        var text = mainDocument.Body!.Elements<Paragraph>().First().Descendants<Text>().First();
        text.Text = "X" + text.Text[1..];
        mainDocument.Save();
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
