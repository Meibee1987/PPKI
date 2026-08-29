using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class Sprint8GoldenFixerMatrixTests
{
    [Fact]
    public async Task Golden_fixture_combines_required_structures_and_source_bytes_remain_identical()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("sprint8-golden-regression-matrix");
        var originalBytes = await File.ReadAllBytesAsync(workspace.OriginalPath);
        var originalSha = Sha(originalBytes);

        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var main = Assert.IsType<MainDocumentPart>(package.MainDocumentPart);
            var body = Assert.IsType<Body>(main.Document?.Body);
            Assert.Equal(2, body.Descendants<SectionProperties>().Count());
            Assert.Single(main.HeaderParts);
            Assert.Single(main.FooterParts);
            Assert.Single(main.HyperlinkRelationships);
            Assert.Single(body.Elements<Table>());
            Assert.NotEmpty(body.Descendants<NumberingProperties>());
            Assert.NotEmpty(body.Descendants<FieldCode>());
            Assert.NotEmpty(body.Descendants<BookmarkStart>());
            var semanticRun = body.Elements<Paragraph>().First().Descendants<Run>().First();
            Assert.NotNull(semanticRun.RunProperties?.Bold);
            Assert.NotNull(semanticRun.RunProperties?.Italic);
            Assert.NotNull(semanticRun.RunProperties?.Underline);
            Assert.NotNull(semanticRun.RunProperties?.VerticalTextAlignment);
            Assert.False(string.IsNullOrWhiteSpace(semanticRun.RunProperties?.RunFonts?.EastAsia?.Value));
            Assert.False(string.IsNullOrWhiteSpace(semanticRun.RunProperties?.RunFonts?.ComplexScript?.Value));
        }

        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        Assert.Equal(2, parsed.Sections.Count);
        Assert.Contains(parsed.Paragraphs, value => value.EffectiveNumbering is not null
            && value.DirectHangingIndentTwips == 360);
        Assert.Single(parsed.FieldInventory);
        Assert.Equal("CITATION", parsed.FieldInventory.Single().NormalizedInstruction);
        Assert.Equal(originalSha, (await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath)).ToLowerInvariant());
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(workspace.OriginalPath));
    }

    [Fact]
    public async Task Cross_family_plan_changes_only_allowlisted_semantics_reopens_reparses_and_is_deterministic()
    {
        await using var first = await DocxFixtureWorkspace.CreateAsync("sprint8-golden-regression-matrix");
        await using var second = await DocxFixtureWorkspace.CreateAsync("sprint8-golden-regression-matrix");
        var firstResult = await ApplyGoldenPlan(first);
        var secondResult = await ApplyGoldenPlan(second);

        Assert.Equal(firstResult.After, secondResult.After);
        Assert.Equal(firstResult.ChangedKeys, secondResult.ChangedKeys);
        Assert.Equal(first.OriginalChecksum, await DocxFixtureWorkspace.ComputeSha256Async(first.OriginalPath));
        Assert.Equal(second.OriginalChecksum, await DocxFixtureWorkspace.ComputeSha256Async(second.OriginalPath));
    }

    [Fact]
    public async Task Cancelled_apply_and_validation_leave_working_and_original_bytes_unchanged()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("sprint8-golden-regression-matrix");
        var parser = new OpenXmlDocxParser();
        var parsed = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var resolved = Resolve(parsed).First(value => value.Finding.Location.ParagraphIndex == 0);
        var plan = Plan([resolved]);
        var operation = Assert.Single(plan.Preview.Operations);
        Assert.True(ProductionFixCapabilities.CreateApplyRegistry().TryGet(operation, out var provider));
        var workingBefore = await File.ReadAllBytesAsync(workspace.WorkingPath);
        var originalBefore = await File.ReadAllBytesAsync(workspace.OriginalPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ApplyAsync(new(
            workspace.WorkingPath, parsed, plan.Source.Findings.Single(), operation), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FinalDocxOutputValidator(parser).ValidatePublishedAsync(
                workspace.WorkingPath, Sha(workingBefore), workingBefore.LongLength, cancellation.Token));

        Assert.Equal(workingBefore, await File.ReadAllBytesAsync(workspace.WorkingPath));
        Assert.Equal(originalBefore, await File.ReadAllBytesAsync(workspace.OriginalPath));
    }

    private static async Task<GoldenResult> ApplyGoldenPlan(DocxFixtureWorkspace workspace)
    {
        var parser = new OpenXmlDocxParser();
        var originalBytes = await File.ReadAllBytesAsync(workspace.OriginalPath);
        var originalSha = Sha(originalBytes);
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var baseline = DocxPackageIntegrity.Capture(workspace.WorkingPath);
        var normalizedBefore = Normalize(workspace.WorkingPath);
        var selected = Resolve(before).Where(value =>
            value.Finding.Location.ParagraphIndex == 2
            || value.Finding.Location.SectionIndex == 0
                && value.Finding.Location.ParagraphIndex == 0).ToArray();
        Assert.Contains(selected, value => value.Finding.Actual.Property == "pageSize");
        Assert.Contains(selected, value => value.Finding.Actual.Property == "marginLeft");
        Assert.Contains(selected, value => value.Finding.Actual.Property == "font.ascii");
        Assert.Contains(selected, value => value.Finding.Actual.Property == "fontSize");
        Assert.Contains(selected, value => value.Finding.Actual.Property == "lineSpacingValue");
        Assert.Contains(selected, value => value.Finding.Actual.Property == "firstLineIndent");
        Assert.Contains(selected, value => value.Finding.Actual.Property == "alignment");
        Assert.Contains(selected, value => value.Snapshot.ValidationKey == "heading.chapter-bold");
        Assert.Contains(selected, value => value.Snapshot.ValidationKey == "heading.chapter-no-period-no-underline");
        Assert.Contains(selected, value => value.Snapshot.ValidationKey == "heading.chapter-centered");
        var plan = Plan(selected);
        Assert.Equal(FixPlanState.Ready, plan.Preview.State);

        var registry = ProductionFixCapabilities.CreateApplyRegistry();
        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(registry.TryGet(operation, out var provider));
            var finding = plan.Source.Findings.Single(value => value.FindingId == operation.SourceFindingIds.Single());
            Assert.Equal(FixApplyOutcome.Changed, await provider.ApplyAsync(
                new(workspace.WorkingPath, before, finding, operation), CancellationToken.None));
        }

        var validated = await new FinalDocxOutputValidator(parser).ValidateMutationAsync(
            baseline, workspace.WorkingPath, CancellationToken.None);
        using (var reopened = WordprocessingDocument.Open(workspace.WorkingPath, false,
                   new OpenSettings { AutoSave = false }))
        {
            Assert.NotNull(reopened.MainDocumentPart?.Document?.Body);
            Assert.Single(reopened.MainDocumentPart!.HeaderParts);
            Assert.Single(reopened.MainDocumentPart.FooterParts);
        }
        Assert.Equal(OpenXmlDocxParser.SchemaVersion, validated.ParsedDocument.ParserSchemaVersion);
        var normalizedAfter = Normalize(workspace.WorkingPath);
        var changed = normalizedBefore.Keys.Where(key => normalizedBefore[key] != normalizedAfter[key])
            .Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(changed);
        Assert.All(changed, key => Assert.True(IsAllowed(key, plan.Preview.Operations),
            $"Unexpected normalized semantic delta: {key}"));
        Assert.Contains(changed, key => key.StartsWith("section.0.page", StringComparison.Ordinal));
        Assert.Contains(changed, key => key.StartsWith("paragraph.0.run.", StringComparison.Ordinal));
        Assert.Contains(changed, key => key.StartsWith("paragraph.0.", StringComparison.Ordinal)
            && !key.Contains(".run.", StringComparison.Ordinal));
        Assert.Contains(changed, key => key.StartsWith("paragraph.2.", StringComparison.Ordinal));
        Assert.Equal(normalizedBefore["document.text-fingerprint"], normalizedAfter["document.text-fingerprint"]);
        Assert.Equal(normalizedBefore["structure"], normalizedAfter["structure"]);
        Assert.Equal(originalSha, (await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath)).ToLowerInvariant());
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(workspace.OriginalPath));

        var bytesBeforeReplay = await File.ReadAllBytesAsync(workspace.WorkingPath);
        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(registry.TryGet(operation, out var provider));
            var finding = plan.Source.Findings.Single(value => value.FindingId == operation.SourceFindingIds.Single());
            Assert.Equal(FixApplyOutcome.NoChange, await provider.ApplyAsync(
                new(workspace.WorkingPath, validated.ParsedDocument, finding, operation), CancellationToken.None));
        }
        Assert.Equal(bytesBeforeReplay, await File.ReadAllBytesAsync(workspace.WorkingPath));
        return new(normalizedAfter, changed);
    }

    private static bool IsAllowed(string key, IReadOnlyList<FixPlanOperation> operations) =>
        operations.Any(operation => operation.PropertyIdentifier switch
        {
            "section.page-size" => key is "section.0.page-width" or "section.0.page-height",
            "section.margin-left" => key == "section.0.margin-left",
            "run.font-family-ascii" => RunKey(key, operation, "font-ascii"),
            "run.font-family-high-ansi" => RunKey(key, operation, "font-high-ansi"),
            "run.font-size" => RunKey(key, operation, "font-size"),
            "paragraph.line-spacing-value" => ParagraphKey(key, operation, "line-spacing"),
            "paragraph.line-spacing-rule" => ParagraphKey(key, operation, "line-rule"),
            "paragraph.first-line-indent" => ParagraphKey(key, operation, "first-line"),
            "paragraph.alignment" => ParagraphKey(key, operation, "alignment"),
            "heading.runs-bold" => HeadingRunKey(key, operation, "bold"),
            "heading.runs-underline" => HeadingRunKey(key, operation, "underline"),
            "heading.alignment" => ParagraphKey(key, operation, "alignment"),
            _ => false
        });

    private static bool RunKey(string key, FixPlanOperation operation, string property) =>
        key == $"paragraph.{operation.Target.ParagraphIndex}.run.{operation.Target.RunIndex}.{property}";

    private static bool ParagraphKey(string key, FixPlanOperation operation, string property) =>
        key == $"paragraph.{operation.Target.ParagraphIndex}.{property}";

    private static bool HeadingRunKey(string key, FixPlanOperation operation, string property) =>
        key.StartsWith($"paragraph.{operation.Target.ParagraphIndex}.run.", StringComparison.Ordinal)
        && key.EndsWith($".{property}", StringComparison.Ordinal);

    private static SortedDictionary<string, string> Normalize(string path)
    {
        using var package = WordprocessingDocument.Open(path, false, new OpenSettings { AutoSave = false });
        var main = package.MainDocumentPart!;
        var body = main.Document!.Body!;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var sections = body.Descendants<SectionProperties>().ToArray();
        for (var index = 0; index < sections.Length; index++)
        {
            var page = sections[index].GetFirstChild<PageSize>();
            var margin = sections[index].GetFirstChild<PageMargin>();
            result[$"section.{index}.page-width"] = page?.Width?.Value.ToString() ?? "";
            result[$"section.{index}.page-height"] = page?.Height?.Value.ToString() ?? "";
            result[$"section.{index}.orientation"] = page?.Orient?.Value.ToString() ?? "";
            result[$"section.{index}.margin-left"] = margin?.Left?.Value.ToString() ?? "";
            result[$"section.{index}.margin-right"] = margin?.Right?.Value.ToString() ?? "";
            result[$"section.{index}.margin-top"] = margin?.Top?.Value.ToString() ?? "";
            result[$"section.{index}.margin-bottom"] = margin?.Bottom?.Value.ToString() ?? "";
        }
        var paragraphs = body.Elements<Paragraph>().ToArray();
        for (var paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
        {
            var paragraph = paragraphs[paragraphIndex];
            var properties = paragraph.ParagraphProperties;
            var spacing = properties?.SpacingBetweenLines;
            var indentation = properties?.Indentation;
            result[$"paragraph.{paragraphIndex}.style"] = properties?.ParagraphStyleId?.Val?.Value ?? "";
            result[$"paragraph.{paragraphIndex}.alignment"] = properties?.Justification?.Val?.Value.ToString() ?? "";
            result[$"paragraph.{paragraphIndex}.line-spacing"] = spacing?.Line?.Value ?? "";
            result[$"paragraph.{paragraphIndex}.line-rule"] = spacing?.LineRule?.Value.ToString() ?? "";
            result[$"paragraph.{paragraphIndex}.first-line"] = indentation?.FirstLine?.Value ?? "";
            result[$"paragraph.{paragraphIndex}.left"] = indentation?.Left?.Value ?? "";
            result[$"paragraph.{paragraphIndex}.right"] = indentation?.Right?.Value ?? "";
            result[$"paragraph.{paragraphIndex}.hanging"] = indentation?.Hanging?.Value ?? "";
            result[$"paragraph.{paragraphIndex}.numbering"] = properties?.NumberingProperties?.OuterXml ?? "";
            var runs = paragraph.Descendants<Run>().ToArray();
            for (var runIndex = 0; runIndex < runs.Length; runIndex++)
            {
                var run = runs[runIndex];
                var runProperties = run.RunProperties;
                var prefix = $"paragraph.{paragraphIndex}.run.{runIndex}";
                result[$"{prefix}.text"] = Sha(Encoding.UTF8.GetBytes(string.Concat(run.Descendants<Text>().Select(value => value.Text))));
                result[$"{prefix}.font-ascii"] = runProperties?.RunFonts?.Ascii?.Value ?? "";
                result[$"{prefix}.font-high-ansi"] = runProperties?.RunFonts?.HighAnsi?.Value ?? "";
                result[$"{prefix}.font-east-asia"] = runProperties?.RunFonts?.EastAsia?.Value ?? "";
                result[$"{prefix}.font-complex"] = runProperties?.RunFonts?.ComplexScript?.Value ?? "";
                result[$"{prefix}.font-size"] = runProperties?.FontSize?.Val?.Value ?? "";
                result[$"{prefix}.bold"] = Toggle(runProperties?.Bold);
                result[$"{prefix}.italic"] = Toggle(runProperties?.Italic);
                result[$"{prefix}.underline"] = runProperties?.Underline?.Val?.Value.ToString() ?? "";
                result[$"{prefix}.vertical"] = runProperties?.VerticalTextAlignment?.Val?.Value.ToString() ?? "";
            }
        }
        result["document.text-fingerprint"] = Sha(Encoding.UTF8.GetBytes(string.Join("\0",
            main.Document.Descendants<Text>().Select(value => value.Text))));
        result["structure"] = string.Join("|", sections.Length, paragraphs.Length,
            body.Elements<Table>().Count(), main.HeaderParts.Count(), main.FooterParts.Count(),
            main.HyperlinkRelationships.Count(), body.Descendants<FieldCode>().Count(),
            body.Descendants<BookmarkStart>().Count());
        return result;
    }

    private static string Toggle(OnOffType? value) => value is null ? "" : value.Val?.Value == false ? "false" : "true";

    private static IReadOnlyList<ResolvedRuleFinding> Resolve(ParsedDocument document)
    {
        var validators = new IDocumentRuleValidator[]
        {
            new PageSizeA4Validator(), new MarginLeftValidator(), new BodyFontValidator(),
            new LineSpacingValidator(), new FirstLineIndentValidator(), new JustifiedValidator(),
            new ChapterBoldValidator(), new ChapterDecorationValidator(), new ChapterAlignmentValidator()
        };
        return new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry(validators))
            .Validate(document, Snapshots(), CancellationToken.None).Findings;
    }

    private static AuditRuleSnapshot[] Snapshots() =>
    [
        Snapshot("PPKI-LAY-003", "section.page-size-a4", 3),
        Snapshot("PPKI-LAY-008", "section.margin-left-4cm", 8),
        Snapshot("PPKI-LAY-005", "body.font-times-new-roman-12", 5),
        Snapshot("PPKI-LAY-017", "body.line-spacing-single", 17,
            "{\"value\":240,\"unit\":\"twip\",\"rule\":\"auto\"}"),
        Snapshot("PPKI-LAY-018", "body.first-line-indent-1cm", 18,
            "{\"value\":1,\"unit\":\"cm\"}"),
        Snapshot("PPKI-LAY-019", "body.justified", 19,
            "{\"accepted\":[\"Justified\"]}"),
        Snapshot("PPKI-HDG-004", "heading.chapter-bold", 104),
        Snapshot("PPKI-HDG-005", "heading.chapter-no-period-no-underline", 105),
        Snapshot("PPKI-HDG-006", "heading.chapter-centered", 106)
    ];

    private static AuditRuleSnapshot Snapshot(string rule, string key, int ordinal, string validation = "{}") => new()
    {
        AuditJobId = Guid.NewGuid(), RuleId = Guid.NewGuid(), RuleCode = rule,
        Domain = rule.Contains("HDG", StringComparison.Ordinal) ? "HDG" : "LAY",
        Subdomain = "Synthetic", AppliesTo = "Semua", Element = "Golden matrix",
        RequirementJson = "{}", ValidationKey = key, ValidationJson = validation,
        Severity = RuleSeverity.Error, FixMode = FixMode.Auto, SourceReferenceJson = "{}",
        Layer = "profile", Precedence = 0, Ordinal = ordinal, SnapshotSchemaVersion = 1
    };

    private static (FixPlanSource Source, FixPlanPreview Preview) Plan(IReadOnlyList<ResolvedRuleFinding> resolved)
    {
        var findings = resolved.Select(value => new FixPlanFindingSnapshot(
            Guid.NewGuid(), value.Snapshot.Ordinal, value.Snapshot.RuleCode, value.Snapshot.Domain,
            value.Snapshot.Element, value.Snapshot.ValidationKey, value.Snapshot.Severity,
            value.Snapshot.FixMode, FindingStatus.Open, JsonSerializer.Serialize(value.Finding.Actual),
            JsonSerializer.Serialize(value.Finding.Expected), JsonSerializer.Serialize(value.Finding.Location),
            value.Snapshot.SnapshotSchemaVersion)).ToArray();
        var source = new FixPlanSource(Guid.NewGuid(), AuditJobStatus.Completed, Guid.NewGuid(),
            new string('a', 64), new string('b', 64), DocumentKind.Skripsi, findings);
        return (source, new DeterministicFixPlanPreviewPlanner(
            ProductionFixCapabilities.CreatePreviewRegistry()).Create(source));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record GoldenResult(SortedDictionary<string, string> After, string[] ChangedKeys);
}

public sealed class Sprint8CorruptionRegressionMatrixTests
{
    private const string PrivacySentinel = "S8T10_PRIVATE_SYNTHETIC_SENTINEL";

    [Theory]
    [InlineData("arbitrary")]
    [InlineData("truncated")]
    [InlineData("zip-not-docx")]
    [InlineData("missing-main")]
    [InlineData("missing-document")]
    [InlineData("missing-body")]
    [InlineData("missing-package-relationship")]
    [InlineData("malformed-document-xml")]
    public async Task Corrupt_variants_fail_with_bounded_code_without_mutating_original(string kind)
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("sprint8-golden-regression-matrix");
        var originalBytes = await File.ReadAllBytesAsync(workspace.OriginalPath);
        await Corrupt(workspace.WorkingPath, kind);
        var bytes = await File.ReadAllBytesAsync(workspace.WorkingPath);

        var error = await Assert.ThrowsAsync<FixExecutionException>(() =>
            new FinalDocxOutputValidator(new OpenXmlDocxParser()).ValidatePublishedAsync(
                workspace.WorkingPath, Sha(bytes), bytes.LongLength, CancellationToken.None));

        Assert.Contains(error.DiagnosticCode,
            new[] { "fix-result-package-invalid", "fix-execution-result-size-invalid" });
        Assert.DoesNotContain(workspace.WorkingPath, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivacySentinel, error.Message, StringComparison.Ordinal);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(workspace.OriginalPath));
    }

    [Fact]
    public async Task Package_integrity_rejects_relationship_or_non_document_part_mutation_deterministically()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("sprint8-golden-regression-matrix");
        var baseline = DocxPackageIntegrity.Capture(workspace.WorkingPath);
        using (var archive = ZipFile.Open(workspace.WorkingPath, ZipArchiveMode.Update))
        {
            var header = archive.GetEntry("word/header1.xml")!;
            header.Delete();
            var replacement = archive.CreateEntry("word/header1.xml");
            await using var stream = replacement.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("<invalid/>"));
        }

        var first = Assert.Throws<FixExecutionException>(() =>
            DocxPackageIntegrity.ValidateMutation(baseline, workspace.WorkingPath));
        var second = Assert.Throws<FixExecutionException>(() =>
            DocxPackageIntegrity.ValidateMutation(baseline, workspace.WorkingPath));
        Assert.Equal("fix-execution-package-integrity-failed", first.DiagnosticCode);
        Assert.Equal(first.DiagnosticCode, second.DiagnosticCode);
    }

    private static async Task Corrupt(string path, string kind)
    {
        if (kind == "arbitrary")
        {
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(PrivacySentinel));
            return;
        }
        if (kind == "truncated")
        {
            var bytes = await File.ReadAllBytesAsync(path);
            await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)]);
            return;
        }
        if (kind == "zip-not-docx")
        {
            File.Delete(path);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            var entry = archive.CreateEntry("synthetic.txt");
            await using var stream = entry.Open();
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            return;
        }
        if (kind is "missing-main" or "missing-document" or "missing-body")
        {
            File.Delete(path);
            using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            if (kind != "missing-main")
            {
                var main = package.AddMainDocumentPart();
                if (kind == "missing-body")
                {
                    main.Document = new Document();
                    main.Document.Save();
                }
            }
            return;
        }
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var target = kind == "missing-package-relationship" ? "_rels/.rels" : "word/document.xml";
            archive.GetEntry(target)!.Delete();
            if (kind == "malformed-document-xml")
            {
                var entry = archive.CreateEntry(target);
                await using var stream = entry.Open();
                await stream.WriteAsync(Encoding.UTF8.GetBytes("<w:document>"));
            }
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
