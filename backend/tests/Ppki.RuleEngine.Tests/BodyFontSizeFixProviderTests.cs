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

public sealed class BodyFontSizeFixProviderTests
{
    [Fact]
    public async Task Golden_mixed_run_fixture_fails_before_and_passes_after_exact_direct_operations()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("body-font-size-fixer");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var originalChecksum = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var beforeText = TextFingerprint(before);
        var snapshot = Snapshot();
        var resolved = Validate(snapshot, before);
        Assert.NotEmpty(resolved);
        Assert.All(resolved, value => Assert.Equal(0, value.Finding.Location.ParagraphIndex));
        Assert.Equal([0, 1, 2, 3, 4, 8], resolved.Select(value => value.Finding.Location.RunIndex!.Value)
            .Distinct().Order().ToArray());

        var plan = Plan(resolved);
        Assert.Equal(FixPlanState.Ready, plan.Preview.State);
        Assert.Equal(resolved.Count, plan.Preview.Operations.Count);
        Assert.All(plan.Preview.Operations, operation =>
        {
            Assert.Equal(BodyFontFixProvider.Id, operation.CapabilityId);
            Assert.Equal(BodyFontFixProvider.Version, operation.CapabilityVersion);
            Assert.Equal("body.font-times-new-roman-12", operation.ValidationKey);
            Assert.Equal(0, operation.Target.SectionIndex);
        });

        string expectedMixed;
        string correctBefore;
        string headingBefore;
        string tableBefore;
        string instructionBefore;
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var body = package.MainDocumentPart!.Document!.Body!;
            var paragraphs = body.Elements<Paragraph>().ToArray();
            var expected = (Paragraph)paragraphs[0].CloneNode(true);
            foreach (var operation in plan.Preview.Operations) SetExpected(expected, operation);
            expectedMixed = expected.OuterXml;
            correctBefore = paragraphs[1].OuterXml;
            headingBefore = paragraphs[2].OuterXml;
            tableBefore = body.Elements<Table>().Single().OuterXml;
            instructionBefore = paragraphs[0].Descendants<FieldCode>().Single().Text;
        }

        var registry = ProductionFixCapabilities.CreateApplyRegistry();
        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(registry.TryGet(operation, out var provider));
            var findingId = Assert.Single(operation.SourceFindingIds);
            var finding = plan.Source.Findings.Single(value => value.FindingId == findingId);
            Assert.Equal(FixApplyOutcome.Changed, await provider.ApplyAsync(
                new(workspace.WorkingPath, before, finding, operation), CancellationToken.None));
        }

        var after = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        Assert.Empty(Validate(snapshot, after));
        var remediatedRuns = after.Paragraphs.Single(value => value.Index == 0).RunList
            .Where(value => new[] { 0, 1, 2, 3, 4, 8 }.Contains(value.Index)).ToArray();
        Assert.All(remediatedRuns, run =>
        {
            Assert.Equal("Times New Roman", run.EffectiveFormatting!.FontAscii.Value);
            Assert.Equal("Times New Roman", run.EffectiveFormatting.FontHighAnsi.Value);
            Assert.Equal(24, run.EffectiveFormatting.FontSizeHalfPoints.Value);
        });
        Assert.Equal(beforeText, TextFingerprint(after));
        Assert.Equal(originalChecksum, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
        Assert.Single(after.FieldInventory);
        Assert.Equal("CITATION", after.FieldInventory[0].NormalizedInstruction);
        Assert.True(after.FieldInventory[0].HasBegin && after.FieldInventory[0].HasSeparate
            && after.FieldInventory[0].HasEnd);

        string documentBeforeSecondApply;
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var body = package.MainDocumentPart!.Document!.Body!;
            var paragraphs = body.Elements<Paragraph>().ToArray();
            Assert.Equal(expectedMixed, paragraphs[0].OuterXml);
            Assert.Equal(correctBefore, paragraphs[1].OuterXml);
            Assert.Equal(headingBefore, paragraphs[2].OuterXml);
            Assert.Equal(tableBefore, body.Elements<Table>().Single().OuterXml);
            Assert.Equal(instructionBefore, paragraphs[0].Descendants<FieldCode>().Single().Text);
            Assert.IsType<Hyperlink>(paragraphs[0].Descendants<Run>().ElementAt(4).Parent);
            var semantic = paragraphs[0].Descendants<Run>().ElementAt(2).RunProperties!;
            Assert.Equal("MS Mincho", semantic.RunFonts!.EastAsia!.Value);
            Assert.Equal("Arabic Typesetting", semantic.RunFonts.ComplexScript!.Value);
            Assert.Equal("28", semantic.FontSizeComplexScript!.Val!.Value);
            Assert.Equal(VerticalPositionValues.Superscript,
                semantic.VerticalTextAlignment!.Val!.Value);
            Assert.NotNull(semantic.Strike);
            Assert.NotNull(semantic.NoProof);
            Assert.Equal(HighlightColorValues.Yellow, semantic.Highlight!.Val!.Value);
            Assert.Equal("D9EAD3", semantic.Shading!.Fill!.Value);
            Assert.Equal("id-ID", semantic.Languages!.Val!.Value);
            Assert.NotNull(paragraphs[0].Descendants<Run>().ElementAt(0).RunProperties!.Bold);
            Assert.NotNull(paragraphs[0].Descendants<Run>().ElementAt(1).RunProperties!.Italic);
            Assert.Equal(UnderlineValues.Single,
                paragraphs[0].Descendants<Run>().ElementAt(1).RunProperties!.Underline!.Val!.Value);
            Assert.Equal(VerticalPositionValues.Subscript,
                paragraphs[0].Descendants<Run>().ElementAt(3).RunProperties!.VerticalTextAlignment!.Val!.Value);
            Assert.IsType<InsertedRun>(paragraphs[0].Descendants<Run>().ElementAt(3).Parent);
            documentBeforeSecondApply = package.MainDocumentPart.Document.OuterXml;
        }

        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(registry.TryGet(operation, out var provider));
            var findingId = Assert.Single(operation.SourceFindingIds);
            var finding = plan.Source.Findings.Single(value => value.FindingId == findingId);
            Assert.Equal(FixApplyOutcome.NoChange, await provider.ApplyAsync(
                new(workspace.WorkingPath, after, finding, operation), CancellationToken.None));
        }
        using var afterSecond = WordprocessingDocument.Open(workspace.WorkingPath, false);
        Assert.Equal(documentBeforeSecondApply, afterSecond.MainDocumentPart!.Document!.OuterXml);
    }

    [Fact]
    public async Task Heading_and_non_visible_field_instruction_targets_fail_closed_without_mutation()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("body-font-size-fixer");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var sourceFinding = Finding(Validate(Snapshot(), parsed)
            .First(value => value.Finding.Actual.Property == "font.ascii"));
        var provider = new BodyFontFixProvider();

        foreach (var target in new[]
        {
            parsed.Paragraphs.Single(value => value.IsHeading).RunList.Single().Location,
            parsed.Paragraphs.Single(value => value.Index == 0).RunList.Single(value => value.Index == 6).Location
        })
        {
            var forged = sourceFinding with { FindingId = Guid.NewGuid(), LocationJson = JsonSerializer.Serialize(
                new LayoutFindingLocation(target.ToCompactString(), target.SectionIndex, target.BodyElementIndex,
                    target.ParagraphIndex, target.RunIndex)) };
            var plan = Plan([forged]);
            var operation = Assert.Single(plan.Preview.Operations);
            var before = await File.ReadAllBytesAsync(workspace.WorkingPath);
            var exception = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(
                new(workspace.WorkingPath, parsed, forged, operation), CancellationToken.None));
            Assert.Equal("fix-operation-target-precondition-failed", exception.DiagnosticCode);
            Assert.Equal(before, await File.ReadAllBytesAsync(workspace.WorkingPath));
        }
    }

    [Fact]
    public async Task Invalid_anchor_wrong_validation_and_wrong_version_fail_closed()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("body-font-size-fixer");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var resolved = Validate(Snapshot(), parsed).First();
        var plan = Plan([resolved]);
        var operation = Assert.Single(plan.Preview.Operations);
        var stale = operation with { Target = operation.Target with { RunIndex = 999 } };
        var provider = new BodyFontFixProvider();
        var exception = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(
            new(workspace.WorkingPath, parsed, plan.Source.Findings.Single(), stale), CancellationToken.None));
        Assert.Equal("fix-operation-target-precondition-failed", exception.DiagnosticCode);

        var wrong = plan.Source.Findings.Single() with { ValidationKey = "body.justified" };
        Assert.False(provider.TryCreate(wrong, out _, out _));
        Assert.False(ProductionFixCapabilities.CreateApplyRegistry().CanApply(
            operation with { CapabilityVersion = "2.0" }));

        var invalidLocation = JsonSerializer.Serialize(new
        {
            compactLocation = "maindocument/s:0/b:0/p:0/r:999/kind:run",
            sectionIndex = 0, bodyElementIndex = 0, paragraphIndex = 0, runIndex = 0
        });
        Assert.False(provider.TryCreate(plan.Source.Findings.Single() with { LocationJson = invalidLocation },
            out _, out _));
    }

    [Fact]
    public async Task Table_paragraph_is_excluded_by_authoritative_validator()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("body-font-size-fixer");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var tableParagraph = parsed.Paragraphs.Single(value => value.IsInTable);
        Assert.DoesNotContain(Validate(Snapshot(), parsed), value =>
            value.Finding.Location.ParagraphIndex == tableParagraph.Index);
    }

    private static void SetExpected(Paragraph paragraph, FixPlanOperation operation)
    {
        var run = paragraph.Descendants<Run>().ElementAt(operation.Target.RunIndex!.Value);
        run.RunProperties ??= new RunProperties();
        if (operation.PropertyIdentifier == "run.font-size")
            run.RunProperties.FontSize = new FontSize { Val = operation.Expected.Value };
        else
        {
            run.RunProperties.RunFonts ??= new RunFonts();
            if (operation.PropertyIdentifier == "run.font-family-ascii")
                run.RunProperties.RunFonts.Ascii = operation.Expected.Value;
            else run.RunProperties.RunFonts.HighAnsi = operation.Expected.Value;
        }
    }

    private static IReadOnlyList<ResolvedRuleFinding> Validate(AuditRuleSnapshot snapshot, ParsedDocument document) =>
        new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry([new BodyFontValidator()]))
            .Validate(document, [snapshot], CancellationToken.None).Findings;

    private static (FixPlanSource Source, FixPlanPreview Preview) Plan(IReadOnlyList<ResolvedRuleFinding> resolved) =>
        Plan(resolved.Select(Finding).ToArray());

    private static (FixPlanSource Source, FixPlanPreview Preview) Plan(IReadOnlyList<FixPlanFindingSnapshot> findings)
    {
        var source = new FixPlanSource(Guid.NewGuid(), AuditJobStatus.Completed, Guid.NewGuid(), new string('a', 64),
            new string('b', 64), DocumentKind.Skripsi, findings);
        return (source, new DeterministicFixPlanPreviewPlanner(
            ProductionFixCapabilities.CreatePreviewRegistry()).Create(source));
    }

    private static FixPlanFindingSnapshot Finding(ResolvedRuleFinding resolved) => new(
        Guid.NewGuid(), resolved.Snapshot.Ordinal, resolved.Snapshot.RuleCode, resolved.Snapshot.Domain,
        resolved.Snapshot.Element, resolved.Snapshot.ValidationKey, resolved.Snapshot.Severity,
        resolved.Snapshot.FixMode, FindingStatus.Open, JsonSerializer.Serialize(resolved.Finding.Actual),
        JsonSerializer.Serialize(resolved.Finding.Expected), JsonSerializer.Serialize(resolved.Finding.Location),
        resolved.Snapshot.SnapshotSchemaVersion);

    private static AuditRuleSnapshot Snapshot() => new()
    {
        AuditJobId = Guid.NewGuid(), RuleId = Guid.NewGuid(), RuleCode = "PPKI-LAY-005", Domain = "LAY",
        Subdomain = "Tipografi", AppliesTo = "Semua", Element = "Fon teks utama",
        RequirementJson = "{}", ValidationKey = "body.font-times-new-roman-12", ValidationJson = "{}",
        Severity = RuleSeverity.Error, FixMode = FixMode.Auto, SourceReferenceJson = "{}",
        Layer = "profile", Precedence = 0, Ordinal = 5, SnapshotSchemaVersion = 1
    };

    private static string TextFingerprint(ParsedDocument document)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var paragraph in document.Paragraphs)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(paragraph.Text));
            hash.AppendData([0]);
            foreach (var run in paragraph.RunList)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(string.Concat(run.TextSegments)));
                hash.AppendData([0]);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
