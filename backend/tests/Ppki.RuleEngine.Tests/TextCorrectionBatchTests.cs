using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.FixEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class TextCorrectionBatchTests
{
    private static readonly Guid VersionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly OpenXmlDocxParser _parser = new();
    private readonly ExactTextAnchorMaterializer _anchors = new();

    [Fact]
    public async Task Detector_is_versioned_bounded_exact_and_skips_unsupported_occurrences()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var detector = new DeterministicTextCorrectionDetector(_parser, _anchors);
        var first = await detector.DetectAsync(workspace.WorkingPath, VersionId, workspace.OriginalChecksum);
        var second = await detector.DetectAsync(workspace.WorkingPath, VersionId, workspace.OriginalChecksum);

        Assert.Equal("ppki-text-correction-detector", DeterministicTextCorrectionDetector.DetectorId);
        Assert.Equal("1.0", DeterministicTextCorrectionDetector.DetectorVersion);
        Assert.Equal("ppki-text-correction-catalog/1.0", DeterministicTextCorrectionDetector.CatalogVersion);
        Assert.Equal(11, first.Count);
        Assert.Equal(9, first.Count(value => value.RuleId == "lex.di-analisa"));
        Assert.Single(first, value => value.RuleId == "lex.aktifitas");
        Assert.Single(first, value => value.RuleId == "lex.resiko");
        Assert.Equal(2, first.Count(value => value.Anchor.ParagraphLocation.ParagraphIndex == 12));
        Assert.True(first.Select(value => value.Anchor.AnchorHash)
            .SequenceEqual(second.Select(value => value.Anchor.AnchorHash)));
        Assert.Equal(first.Count, first.Select(value => value.Anchor.AnchorHash).Distinct().Count());
        Assert.DoesNotContain(first, value => value.Anchor.ParagraphLocation.ParagraphIndex is 7 or 10);
        Assert.Contains(first, value => value.Anchor.Spans.Count > 1);
        Assert.Contains(first, value => value.Anchor.Spans.All(span => span.IsHyperlink));
    }

    [Fact]
    public async Task Provider_replaces_only_selected_duplicate_and_proves_canonical_post_text()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var before = await _parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var anchor = await BuildAsync(workspace, before, 0, "di analisa", 1);
        var replacement = Replacement("dianalisis");
        var provider = new ExactTextReplacementProvider();

        var result = await provider.ApplyAsync(workspace.WorkingPath, VersionId,
            [new(Guid.NewGuid(), anchor, replacement)], _anchors, CancellationToken.None);
        var after = await _parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);

        Assert.Equal(1, result.ChangedCount);
        Assert.Equal("Analisis dilakukan. Data di analisa menggunakan R. Hasil dianalisis kembali.",
            after.Paragraphs[0].Text);
        Assert.Equal(before.Paragraphs.Skip(1).Select(value => value.Text),
            after.Paragraphs.Skip(1).Select(value => value.Text));
        Assert.Equal(workspace.OriginalChecksum,
            await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
    }

    [Fact]
    public async Task Equivalent_split_runs_and_hyperlink_are_preserved_without_paragraph_flattening()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var before = await _parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var safeSplit = await BuildAsync(workspace, before, 11, "di analisa", 0);
        var hyperlink = await BuildAsync(workspace, before, 5, "di analisa", 0);
        Assert.Equal(2, safeSplit.Spans.Count);
        string relationshipId;
        int runCount;
        using (var source = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var link = source.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ElementAt(5)
                .Elements<Hyperlink>().Single();
            relationshipId = link.Id!.Value!;
            runCount = source.MainDocumentPart.Document.Body.Descendants<Run>().Count();
        }

        await new ExactTextReplacementProvider().ApplyAsync(workspace.WorkingPath, VersionId,
            [new(Guid.NewGuid(), safeSplit, Replacement("dianalisis")),
             new(Guid.NewGuid(), hyperlink, Replacement("dianalisis"))],
            _anchors, CancellationToken.None);

        using var mutated = WordprocessingDocument.Open(workspace.WorkingPath, false);
        var body = mutated.MainDocumentPart!.Document!.Body!;
        var linkAfter = body.Elements<Paragraph>().ElementAt(5).Elements<Hyperlink>().Single();
        Assert.Equal(relationshipId, linkAfter.Id!.Value);
        Assert.Equal("dianalisis", linkAfter.InnerText);
        Assert.Equal(runCount, body.Descendants<Run>().Count());
        Assert.Equal("Split aman dianalisis selesai.", body.Elements<Paragraph>().ElementAt(11).InnerText);
    }

    [Fact]
    public async Task Incompatible_multirun_and_overlap_fail_before_partial_mutation()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("exact-text-anchor");
        var parsed = await _parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var incompatible = await BuildAsync(workspace, parsed, 4, "di analisa", 0);
        var beforeSha = await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath);
        var provider = new ExactTextReplacementProvider();
        var incompatibleFailure = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(
            workspace.WorkingPath, VersionId,
            [new(Guid.NewGuid(), incompatible, Replacement("dianalisis"))], _anchors, CancellationToken.None));
        Assert.Equal("correction-multirun-semantics-incompatible", incompatibleFailure.DiagnosticCode);
        Assert.Equal(beforeSha, await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath));

        var first = await BuildAsync(workspace, parsed, 0, "di analisa", 0);
        var overlapFailure = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(
            workspace.WorkingPath, VersionId,
            [new(Guid.NewGuid(), first, Replacement("dianalisis")),
             new(Guid.NewGuid(), first, Replacement("analisis"))], _anchors, CancellationToken.None));
        Assert.Equal("correction-target-overlap", overlapFailure.DiagnosticCode);
        Assert.Equal(beforeSha, await DocxFixtureWorkspace.ComputeSha256Async(workspace.WorkingPath));
    }

    [Fact]
    public void Reference_plan_and_schema_never_embed_source_context_or_manual_replacement()
    {
        const string secretReplacement = "penggantian-sangat-spesifik";
        var replacement = Replacement(secretReplacement);
        var plan = new ApprovedTextCorrectionExecutionPlan(
            ApprovedTextCorrectionExecutionPlanSerializer.SchemaVersion, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new string('a', 64),
            [new(1, Guid.NewGuid(), new string('b', 64), replacement.Fingerprint)]);
        var json = ApprovedTextCorrectionExecutionPlanSerializer.Serialize(plan);
        Assert.DoesNotContain(secretReplacement, json, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceText", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("context", json, StringComparison.OrdinalIgnoreCase);

        var root = RepositoryRoot();
        var migration = File.ReadAllText(Path.Combine(root, "supabase", "migrations",
            "202608090001_text_correction_pipeline.sql"));
        Assert.Contains("manual_replacement", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("source_sentence", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_paragraph", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PPKIAdmin", migration, StringComparison.Ordinal);
        Assert.Contains("decisions are append-only", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decision_count between 1 and 100", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Architecture_guards_keep_search_in_detector_and_language_out_of_auto_apply()
    {
        var root = RepositoryRoot();
        var apply = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.FixEngine",
            "ExactTextReplacementProvider.cs"));
        foreach (var forbidden in new[] { ".IndexOf(", "Regex.Replace", "string.Replace", "Levenshtein", "Similarity", "HttpClient" })
            Assert.DoesNotContain(forbidden, apply, StringComparison.Ordinal);
        var automatic = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Application",
            "AutomaticRemediationContracts.cs"));
        Assert.DoesNotContain("text-exact-replacement", automatic, StringComparison.Ordinal);
        Assert.DoesNotContain("ppki-text-correction", automatic, StringComparison.Ordinal);
    }

    private async Task<ExactTextAnchor> BuildAsync(DocxFixtureWorkspace workspace, ParsedDocument parsed,
        int paragraphIndex, string target, int occurrence)
    {
        var text = parsed.Paragraphs[paragraphIndex].Text;
        var utf16 = -1;
        var from = 0;
        for (var index = 0; index <= occurrence; index++)
        {
            utf16 = text.IndexOf(target, from, StringComparison.Ordinal);
            Assert.True(utf16 >= 0);
            from = utf16 + target.Length;
        }
        var built = await _anchors.BuildAsync(workspace.WorkingPath, VersionId,
            workspace.OriginalChecksum, paragraphIndex, text[..utf16].EnumerateRunes().Count(),
            target.EnumerateRunes().Count());
        Assert.Equal(ExactTextTargetStatus.Exact, built.Status);
        return built.Anchor!;
    }

    private static ValidatedCorrectionReplacement Replacement(string value)
    {
        Assert.True(TextCorrectionPrivacyContract.TryValidateReplacement(value, out var result, out _));
        return result!;
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null;
             candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
