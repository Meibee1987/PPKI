using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class AutoFormatProviderTests
{
    [Fact]
    public async Task Font_family_changes_exact_mixed_run_and_preserves_content_and_other_formatting()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("auto-format-provider-mixed");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var snapshot = Snapshot("body.font-times-new-roman-12", "PPKI-LAY-005",
            "{\"fontFamily\":\"Times New Roman\",\"fontSize\":11,\"fontSizeUnit\":\"pt\",\"fontSlots\":[\"ascii\",\"highAnsi\"]}");
        var resolved = Validate(new BodyFontValidator(), snapshot, before)
            .Single(value => value.Finding.Location.RunIndex == 1 && value.Finding.Actual.Property == "font.ascii");
        var plan = Plan(resolved);
        var operation = Assert.Single(plan.Preview.Operations);
        Assert.Equal(BodyFontFixProvider.Id, operation.CapabilityId);
        Assert.Equal("Times New Roman", operation.Expected.Value);
        string[] beforeRuns;
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
            beforeRuns = package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First().Descendants<Run>().Select(value => value.OuterXml).ToArray();

        var outcome = await new BodyFontFixProvider().ApplyAsync(new(workspace.WorkingPath, before,
            plan.Source.Findings.Single(), operation), CancellationToken.None);

        Assert.Equal(FixApplyOutcome.Changed, outcome);
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var paragraph = package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
            var runs = paragraph.Descendants<Run>().ToArray();
            Assert.Equal(beforeRuns[0], runs[0].OuterXml);
            Assert.Equal(beforeRuns.Skip(2), runs.Skip(2).Select(value => value.OuterXml));
            Assert.Equal("Times New Roman", runs[1].RunProperties!.RunFonts!.Ascii!.Value);
            Assert.Equal("Arial", runs[1].RunProperties!.RunFonts!.HighAnsi!.Value);
            Assert.NotNull(runs[1].RunProperties!.Bold);
            Assert.Equal(UnderlineValues.Single, runs[1].RunProperties!.Underline!.Val!.Value);
            Assert.Equal("22", runs[1].RunProperties!.FontSize!.Val!.Value);
            Assert.IsType<Hyperlink>(runs[2].Parent);
        }
        var after = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        Assert.Equal(TextFingerprint(before), TextFingerprint(after));
        Assert.Equal(FixApplyOutcome.NoChange, await new BodyFontFixProvider().ApplyAsync(new(
            workspace.WorkingPath, after, plan.Source.Findings.Single(), operation), CancellationToken.None));
    }

    [Fact]
    public async Task Font_size_uses_historical_half_points_and_preserves_family_bold_and_underline()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("auto-format-provider-mixed");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var snapshot = Snapshot("body.font-times-new-roman-12", "PPKI-LAY-005",
            "{\"fontFamily\":\"Arial\",\"fontSize\":12,\"fontSizeUnit\":\"pt\",\"fontSlots\":[\"ascii\",\"highAnsi\"]}");
        var resolved = Validate(new BodyFontValidator(), snapshot, before)
            .Single(value => value.Finding.Location.RunIndex == 1 && value.Finding.Actual.Property == "fontSize");
        var plan = Plan(resolved);
        Assert.Equal(new FixExpectedValueDescriptor("half-points", "24"), plan.Preview.Operations.Single().Expected);

        await new BodyFontFixProvider().ApplyAsync(new(workspace.WorkingPath, before,
            plan.Source.Findings.Single(), plan.Preview.Operations.Single()), CancellationToken.None);

        using var package = WordprocessingDocument.Open(workspace.WorkingPath, false);
        var run = package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First().Descendants<Run>().ElementAt(1);
        Assert.Equal("24", run.RunProperties!.FontSize!.Val!.Value);
        Assert.Equal("Arial", run.RunProperties!.RunFonts!.Ascii!.Value);
        Assert.NotNull(run.RunProperties!.Bold);
        Assert.Equal(UnderlineValues.Single, run.RunProperties!.Underline!.Val!.Value);
    }

    [Fact]
    public async Task Line_spacing_and_first_line_indent_change_only_targeted_paragraph_properties()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("auto-format-provider-mixed");
        var parser = new OpenXmlDocxParser();
        var source = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var line = Validate(new LineSpacingValidator(), Snapshot("body.line-spacing-single", "PPKI-LAY-017",
            "{\"value\":240,\"unit\":\"twip\",\"rule\":\"auto\"}"), source)
            .Single(value => value.Finding.Actual.Property == "lineSpacingValue");
        var indent = Validate(new FirstLineIndentValidator(), Snapshot("body.first-line-indent-1cm", "PPKI-LAY-018",
            "{\"value\":1,\"unit\":\"cm\"}"), source).Single();

        await Apply(workspace.WorkingPath, source, line, new BodyLineSpacingFixProvider());
        await Apply(workspace.WorkingPath, source, indent, new BodyFirstLineIndentFixProvider());

        using var package = WordprocessingDocument.Open(workspace.WorkingPath, false);
        var properties = package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First().ParagraphProperties!;
        Assert.Equal("240", properties.SpacingBetweenLines!.Line!.Value);
        Assert.Equal("120", properties.SpacingBetweenLines.Before!.Value);
        Assert.Equal("80", properties.SpacingBetweenLines.After!.Value);
        Assert.Equal(JustificationValues.Left, properties.Justification!.Val!.Value);
        Assert.Equal("567", properties.Indentation!.FirstLine!.Value);
        Assert.Null(properties.Indentation.Hanging);
        Assert.Equal("720", properties.Indentation.Left!.Value);
        Assert.Equal("400", properties.Indentation.Right!.Value);
    }

    [Fact]
    public async Task Abstract_spacing_before_and_after_are_independent_historical_operations()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-document-sections-layout");
        var parser = new OpenXmlDocxParser();
        var source = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var snapshot = Snapshot("abstract.skripsi-single-spacing-zero-paragraph-spacing", "PPKI-ABS-011",
            "{\"lineSpacingTwips\":240,\"spacingBeforeTwips\":0,\"spacingAfterTwips\":0}");
        var validation = new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry([new SkripsiAbstractSpacingValidator()]))
            .Validate(source, [snapshot], DocumentKind.Skripsi, CancellationToken.None).Findings;
        var beforeFinding = validation.First(value => value.Finding.Actual.Property == "spacingBeforeTwips");
        var afterFinding = validation.First(value => value.Finding.Actual.Property == "spacingAfterTwips"
            && value.Finding.Location.ParagraphIndex == beforeFinding.Finding.Location.ParagraphIndex);

        await Apply(workspace.WorkingPath, source, beforeFinding, new AbstractParagraphSpacingFixProvider());
        await Apply(workspace.WorkingPath, source, afterFinding, new AbstractParagraphSpacingFixProvider());

        var result = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var paragraph = result.Paragraphs.Single(value => value.Index == beforeFinding.Finding.Location.ParagraphIndex);
        Assert.Equal(0, paragraph.DirectSpacingBeforeTwips);
        Assert.Equal(0, paragraph.DirectSpacingAfterTwips);
    }

    [Fact]
    public async Task Missing_exact_run_and_changed_precondition_fail_closed()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("auto-format-provider-mixed");
        var source = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var resolved = Validate(new BodyFontValidator(), Snapshot("body.font-times-new-roman-12", "PPKI-LAY-005",
            "{\"fontFamily\":\"Times New Roman\",\"fontSize\":11,\"fontSizeUnit\":\"pt\",\"fontSlots\":[\"ascii\",\"highAnsi\"]}"), source)
            .Single(value => value.Finding.Location.RunIndex == 1 && value.Finding.Actual.Property == "font.ascii");
        var plan = Plan(resolved);
        var missing = plan.Preview.Operations.Single() with
        {
            Target = plan.Preview.Operations.Single().Target with { RunIndex = 999 }
        };
        var missingError = await Assert.ThrowsAsync<FixExecutionException>(() => new BodyFontFixProvider().ApplyAsync(
            new(workspace.WorkingPath, source, plan.Source.Findings.Single(), missing), CancellationToken.None));
        Assert.Equal("fix-operation-target-precondition-failed", missingError.DiagnosticCode);

        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, true))
        {
            var run = package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First().Descendants<Run>().ElementAt(1);
            run.RunProperties!.RunFonts!.Ascii = "Changed after audit";
            package.MainDocumentPart.Document.Save();
        }
        var changedSource = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var changedError = await Assert.ThrowsAsync<FixExecutionException>(() => new BodyFontFixProvider().ApplyAsync(
            new(workspace.WorkingPath, changedSource, plan.Source.Findings.Single(), plan.Preview.Operations.Single()), CancellationToken.None));
        Assert.Equal("fix-operation-source-snapshot-mismatch", changedError.DiagnosticCode);
    }

    [Fact]
    public void Registry_is_explicit_and_unsafe_formatting_contracts_remain_unregistered()
    {
        var capabilities = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities;
        Assert.All(capabilities, value => Assert.Equal("1.0", value.CapabilityVersion));
        Assert.DoesNotContain(capabilities, value => value.ValidationKey is
            "heading.chapter-bold" or "heading.chapter-no-period-no-underline"
            or "heading.subheading-bold-no-period-no-underline"
            or "heading.subsubheading-regular-no-period-no-underline");
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "src", "Ppki.FixEngine", "DeterministicFormattingFixProviders.cs"));
        Assert.DoesNotContain("public.rules", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rules.json", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Regex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Replace(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Random", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Authorized_download_route_accepts_canonical_original_and_remediated_version_paths()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "services", "Ppki.Api", "Program.cs"));
        var start = source.IndexOf("api.MapGet(\"/document-versions/{id:guid}/download\"", StringComparison.Ordinal);
        var route = source[start..source.IndexOf("app.Run();", start, StringComparison.Ordinal)];

        Assert.Contains("ParentVersionId is null", route, StringComparison.Ordinal);
        Assert.Contains("BuildOriginalPath", route, StringComparison.Ordinal);
        Assert.Contains("BuildVersionPath", route, StringComparison.Ordinal);
        Assert.Contains("Storage.VersionBucket", route, StringComparison.Ordinal);
        Assert.Contains("isOriginal?\"original\":\"remediated\"", route, StringComparison.Ordinal);
    }

    private static async Task Apply(string path, ParsedDocument source, ResolvedRuleFinding resolved, IFixApplyProvider provider)
    {
        var plan = Plan(resolved);
        await provider.ApplyAsync(new(path, source, plan.Source.Findings.Single(), plan.Preview.Operations.Single()), CancellationToken.None);
    }

    private static IReadOnlyList<ResolvedRuleFinding> Validate(IDocumentRuleValidator validator, AuditRuleSnapshot snapshot, ParsedDocument document) =>
        new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry([validator]))
            .Validate(document, [snapshot], CancellationToken.None).Findings;

    private static (FixPlanSource Source, FixPlanPreview Preview) Plan(ResolvedRuleFinding resolved)
    {
        var finding = new FixPlanFindingSnapshot(Guid.NewGuid(), resolved.Snapshot.Ordinal, resolved.Snapshot.RuleCode,
            resolved.Snapshot.Domain, resolved.Snapshot.Element, resolved.Snapshot.ValidationKey,
            resolved.Snapshot.Severity, resolved.Snapshot.FixMode, FindingStatus.Open,
            JsonSerializer.Serialize(resolved.Finding.Actual), JsonSerializer.Serialize(resolved.Finding.Expected),
            JsonSerializer.Serialize(resolved.Finding.Location), resolved.Snapshot.SnapshotSchemaVersion);
        var source = new FixPlanSource(Guid.NewGuid(), AuditJobStatus.Completed, Guid.NewGuid(), new string('a', 64),
            new string('b', 64), DocumentKind.Skripsi, [finding]);
        var preview = new DeterministicFixPlanPreviewPlanner(ProductionFixCapabilities.CreatePreviewRegistry()).Create(source);
        Assert.Equal(FixPlanState.Ready, preview.State);
        return (source, preview);
    }

    private static AuditRuleSnapshot Snapshot(string validationKey, string ruleCode, string validationJson) => new()
    {
        AuditJobId = Guid.NewGuid(), RuleId = Guid.NewGuid(), RuleCode = ruleCode, Domain = ruleCode.Contains("ABS") ? "ABS" : "LAY",
        Subdomain = "Synthetic", AppliesTo = "Semua", Element = "Synthetic formatting", RequirementJson = "{}",
        ValidationKey = validationKey, ValidationJson = validationJson, Severity = RuleSeverity.Error, FixMode = FixMode.Auto,
        SourceReferenceJson = "{}", Layer = "profile", Precedence = 0, Ordinal = 1, SnapshotSchemaVersion = 1
    };

    private static string TextFingerprint(ParsedDocument document)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var paragraph in document.Paragraphs)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(paragraph.Text));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "package.json"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
