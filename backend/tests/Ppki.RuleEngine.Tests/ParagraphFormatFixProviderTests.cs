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

public sealed class ParagraphFormatFixProviderTests
{
    [Fact]
    public async Task Golden_combined_operations_compose_preserve_content_and_are_idempotent()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("paragraph-format-fixers");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var sourceChecksum = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var findings = Validate(before).Where(value => value.Finding.Location.ParagraphIndex == 0).ToArray();
        Assert.Equal(4, findings.Length);
        var registered = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities;
        Assert.Equal((BodyLineSpacingFixProvider.Id, "1.0"), Contract(registered, "body.line-spacing-single"));
        Assert.Equal((BodyFirstLineIndentFixProvider.Id, "1.0"), Contract(registered, "body.first-line-indent-1cm"));
        Assert.Equal((BodyJustifiedFixProvider.Id, "1.0"), Contract(registered, "body.justified"));
        var plan = Plan(findings);
        Assert.Equal(FixPlanState.Ready, plan.Preview.State);
        Assert.Equal(4, plan.Preview.Operations.Count);
        Assert.Equal(new[]
        {
            "paragraph.alignment", "paragraph.first-line-indent",
            "paragraph.line-spacing-rule", "paragraph.line-spacing-value"
        }, plan.Preview.Operations.Select(value => value.PropertyIdentifier).Order().ToArray());
        Assert.All(plan.Preview.Operations, operation =>
        {
            Assert.Equal(0, operation.Target.SectionIndex);
            Assert.Equal(0, operation.Target.BodyElementIndex);
            Assert.Equal(0, operation.Target.ParagraphIndex);
        });

        var capabilities = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities
            .ToDictionary(value => value.ValidationKey, StringComparer.Ordinal);
        var expectedChanges = new Dictionary<string, (string Before, string After)>(StringComparer.Ordinal)
        {
            ["paragraph.alignment"] = ("Kiri", "Rata kiri-kanan"),
            ["paragraph.first-line-indent"] = ("0 cm", "1 cm"),
            ["paragraph.line-spacing-rule"] = ("Tepat", "Otomatis"),
            ["paragraph.line-spacing-value"] = ("1,5 spasi", "1 spasi")
        };
        foreach (var finding in plan.Source.Findings)
        {
            var capability = capabilities[finding.ValidationKey];
            Assert.True(capability.Provider.TryCreate(finding, out var operation, out _));
            Assert.True(capability.Provider.TryCreateBeforeAfter(finding, operation, out var change));
            Assert.Equal("Complete", change.EvidenceState);
            Assert.Equal(expectedChanges[operation.PropertyIdentifier].Before, change.BeforeValue);
            Assert.Equal(expectedChanges[operation.PropertyIdentifier].After, change.AfterValue);
        }
        var analysis = new DeterministicFixPlanConflictAnalyzer().Analyze(plan.Source.DocumentVersionId,
            plan.Preview.Operations.Select((operation, index) => new FixPlanMutationCandidate(
                plan.Source.DocumentVersionId, Guid.NewGuid(), operation.SourceFindingIds.Single(), FixMode.Auto,
                FixPlanDraftPreviewItemState.Previewable, "fix-plan-preview-ready",
                capabilities[operation.ValidationKey], new(operation.Target, operation.PropertyIdentifier,
                    operation.Expected, operation.PreconditionCode, operation.SummaryCode))).ToArray());
        Assert.Equal(FixPlanMutationAnalysisState.Ready, analysis.State);
        Assert.Equal(4, analysis.IndependentItemCount);
        Assert.Equal(0, analysis.ConflictItemCount);

        string expectedProperties;
        string[] runsBefore;
        string headingBefore;
        string tableBefore;
        string instructionBefore;
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var body = package.MainDocumentPart!.Document!.Body!;
            var paragraphs = body.Elements<Paragraph>().ToArray();
            var expected = (ParagraphProperties)paragraphs[0].ParagraphProperties!.CloneNode(true);
            SetExpected(expected, plan.Preview.Operations);
            expectedProperties = expected.OuterXml;
            runsBefore = paragraphs[0].Descendants<Run>().Select(value => value.OuterXml).ToArray();
            headingBefore = paragraphs[7].OuterXml;
            tableBefore = body.Elements<Table>().Single().OuterXml;
            instructionBefore = paragraphs[0].Descendants<FieldCode>().Single().Text;
        }

        await Apply(workspace.WorkingPath, before, plan);
        var after = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        AssertTargetCompliant(after, 0);
        Assert.DoesNotContain(Validate(after), value => value.Finding.Location.ParagraphIndex == 0);
        Assert.Equal(TextFingerprint(before), TextFingerprint(after));
        Assert.Equal(sourceChecksum, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
        Assert.Single(after.FieldInventory);
        Assert.Equal("REF", after.FieldInventory[0].NormalizedInstruction);
        Assert.True(after.FieldInventory[0].HasBegin && after.FieldInventory[0].HasSeparate
            && after.FieldInventory[0].HasEnd);

        string documentBeforeSecondApply;
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var body = package.MainDocumentPart!.Document!.Body!;
            var paragraphs = body.Elements<Paragraph>().ToArray();
            Assert.Equal(expectedProperties, paragraphs[0].ParagraphProperties!.OuterXml);
            Assert.Equal(runsBefore, paragraphs[0].Descendants<Run>().Select(value => value.OuterXml));
            Assert.Equal(headingBefore, paragraphs[7].OuterXml);
            Assert.Equal(tableBefore, body.Elements<Table>().Single().OuterXml);
            Assert.Equal(instructionBefore, paragraphs[0].Descendants<FieldCode>().Single().Text);
            Assert.IsType<Hyperlink>(paragraphs[0].Descendants<Run>().ElementAt(1).Parent);
            documentBeforeSecondApply = package.MainDocumentPart.Document.OuterXml;
        }

        var registry = ProductionFixCapabilities.CreateApplyRegistry();
        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(registry.TryGet(operation, out var provider));
            var finding = plan.Source.Findings.Single(value => value.FindingId == operation.SourceFindingIds.Single());
            Assert.Equal(FixApplyOutcome.NoChange, await provider.ApplyAsync(
                new(workspace.WorkingPath, after, finding, operation), CancellationToken.None));
        }
        using var second = WordprocessingDocument.Open(workspace.WorkingPath, false);
        Assert.Equal(documentBeforeSecondApply, second.MainDocumentPart!.Document!.OuterXml);
    }

    [Fact]
    public async Task Independent_fixers_change_only_the_approved_property_and_orders_converge()
    {
        await using var first = await DocxFixtureWorkspace.CreateAsync("paragraph-format-fixers");
        await using var second = await DocxFixtureWorkspace.CreateAsync("paragraph-format-fixers");
        var firstXml = await ApplyCombined(first.WorkingPath, reverse: false);
        var secondXml = await ApplyCombined(second.WorkingPath, reverse: true);
        Assert.Equal(firstXml, secondXml);

        await using var targeted = await DocxFixtureWorkspace.CreateAsync("paragraph-format-fixers");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(targeted.WorkingPath, CancellationToken.None);
        foreach (var selection in new[]
        {
            (Paragraph: 1, Property: "lineSpacingValue"),
            (Paragraph: 1, Property: "lineSpacingRule"),
            (Paragraph: 2, Property: "firstLineIndent"),
            (Paragraph: 3, Property: "alignment")
        })
        {
            var resolved = Validate(before).Single(value => value.Finding.Location.ParagraphIndex == selection.Paragraph
                && value.Finding.Actual.Property == selection.Property);
            var plan = Plan([resolved]);
            string expected;
            using (var package = WordprocessingDocument.Open(targeted.WorkingPath, false))
            {
                var paragraph = package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ElementAt(selection.Paragraph);
                var properties = (ParagraphProperties)paragraph.ParagraphProperties!.CloneNode(true);
                SetExpected(properties, plan.Preview.Operations);
                expected = properties.OuterXml;
            }
            await Apply(targeted.WorkingPath, before, plan);
            using var verified = WordprocessingDocument.Open(targeted.WorkingPath, false);
            Assert.Equal(expected, verified.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
                .ElementAt(selection.Paragraph).ParagraphProperties!.OuterXml);
            before = await parser.ParseAsync(targeted.WorkingPath, CancellationToken.None);
        }
    }

    [Fact]
    public async Task List_and_hanging_semantics_are_preserved_and_first_line_conflict_fails_closed()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("paragraph-format-fixers");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var list = before.Paragraphs.Single(value => value.Index == 5);
        Assert.Equal(1, list.EffectiveNumbering!.NumberingId);
        Assert.Equal(0, list.EffectiveNumbering.Level);
        Assert.Equal(360, list.DirectHangingIndentTwips);

        var allowed = Validate(before).Where(value => value.Finding.Location.ParagraphIndex == 5
            && value.Finding.Actual.Property is "lineSpacingValue" or "lineSpacingRule" or "alignment").ToArray();
        await Apply(workspace.WorkingPath, before, Plan(allowed));
        var afterAllowed = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var afterList = afterAllowed.Paragraphs.Single(value => value.Index == 5);
        Assert.Equal(1, afterList.EffectiveNumbering!.NumberingId);
        Assert.Equal(0, afterList.EffectiveNumbering.Level);
        Assert.Equal(900, afterList.DirectIndentLeftTwips);
        Assert.Equal(300, afterList.DirectIndentRightTwips);
        Assert.Equal(360, afterList.DirectHangingIndentTwips);
        AssertTargetCompliant(afterAllowed, 5, expectFirstLine: false);

        var indent = Validate(afterAllowed).Single(value => value.Finding.Location.ParagraphIndex == 5
            && value.Finding.Actual.Property == "firstLineIndent");
        var indentPlan = Plan([indent]);
        var bytes = await File.ReadAllBytesAsync(workspace.WorkingPath);
        var operation = Assert.Single(indentPlan.Preview.Operations);
        var exception = await Assert.ThrowsAsync<FixExecutionException>(() => new BodyFirstLineIndentFixProvider()
            .ApplyAsync(new(workspace.WorkingPath, afterAllowed, indentPlan.Source.Findings.Single(), operation), CancellationToken.None));
        Assert.Equal("fix-operation-indent-semantics-unsupported", exception.DiagnosticCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(workspace.WorkingPath));
    }

    [Fact]
    public async Task Excluded_forged_stale_and_wrong_contract_targets_fail_closed()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("paragraph-format-fixers");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var resolved = Validate(parsed).Single(value => value.Finding.Location.ParagraphIndex == 0
            && value.Finding.Actual.Property == "alignment");
        var source = Finding(resolved);
        var provider = new BodyJustifiedFixProvider();
        foreach (var target in new[]
        {
            parsed.Paragraphs.Single(value => value.IsHeading).Location!,
            parsed.Paragraphs.Single(value => value.IsInTable).Location!
        })
        {
            var compact = $"maindocument/s:{target.SectionIndex}/b:{target.BodyElementIndex}/p:{target.ParagraphIndex}/kind:paragraph";
            var forged = source with { FindingId = Guid.NewGuid(), LocationJson = JsonSerializer.Serialize(
                new LayoutFindingLocation(compact, target.SectionIndex, target.BodyElementIndex,
                    target.ParagraphIndex, null)) };
            var plan = Plan([forged]);
            var bytes = await File.ReadAllBytesAsync(workspace.WorkingPath);
            var exception = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(new(
                workspace.WorkingPath, parsed, forged, Assert.Single(plan.Preview.Operations)), CancellationToken.None));
            Assert.Equal("fix-operation-target-precondition-failed", exception.DiagnosticCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(workspace.WorkingPath));
        }

        var validPlan = Plan([source]);
        var operation = Assert.Single(validPlan.Preview.Operations);
        var stale = operation with { Target = operation.Target with { BodyElementIndex = 999 } };
        var staleError = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(new(
            workspace.WorkingPath, parsed, source, stale), CancellationToken.None));
        Assert.Equal("fix-operation-target-precondition-failed", staleError.DiagnosticCode);
        Assert.False(provider.TryCreate(source with { ValidationKey = "body.line-spacing-single" }, out _, out _));
        Assert.False(ProductionFixCapabilities.CreateApplyRegistry().CanApply(
            operation with { CapabilityVersion = "2.0" }));
        var inconsistent = source with { LocationJson = JsonSerializer.Serialize(new
        {
            compactLocation = "maindocument/s:0/b:999/p:0/kind:paragraph",
            sectionIndex = 0, bodyElementIndex = 0, paragraphIndex = 0
        }) };
        Assert.False(provider.TryCreate(inconsistent, out _, out _));
        var spacingFinding = Finding(Validate(parsed).First(value => value.Finding.Actual.Property == "lineSpacingValue"));
        var malformed = spacingFinding with { ExpectedJson = JsonSerializer.Serialize(new
        {
            property = "lineSpacingValue", validationKey = spacingFinding.ValidationKey,
            acceptedValues = new[] { "not-a-number" }
        }) };
        Assert.False(new BodyLineSpacingFixProvider().TryCreate(malformed, out _, out _));

        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, true))
        {
            package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First()
                .ParagraphProperties!.Justification!.Val = JustificationValues.Right;
            package.MainDocumentPart.Document.Save();
        }
        var changed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var changedError = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(new(
            workspace.WorkingPath, changed, source, operation), CancellationToken.None));
        Assert.Equal("fix-operation-source-snapshot-mismatch", changedError.DiagnosticCode);
    }

    [Fact]
    public async Task Authoritative_validators_exclude_heading_and_table_and_keep_non_target_compliant()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("paragraph-format-fixers");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var findings = Validate(parsed);
        Assert.NotEmpty(findings);
        Assert.DoesNotContain(findings, value => value.Finding.Location.ParagraphIndex is 7 or 8);
        Assert.DoesNotContain(findings, value => value.Finding.Location.ParagraphIndex == 4);
    }

    private static async Task<string> ApplyCombined(string path, bool reverse)
    {
        var parsed = await new OpenXmlDocxParser().ParseAsync(path, CancellationToken.None);
        var plan = Plan(Validate(parsed).Where(value => value.Finding.Location.ParagraphIndex == 0).ToArray());
        var operations = reverse ? plan.Preview.Operations.Reverse() : plan.Preview.Operations;
        await Apply(path, parsed, plan, operations);
        using var package = WordprocessingDocument.Open(path, false);
        return package.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First().ParagraphProperties!.OuterXml;
    }

    private static async Task Apply(string path, ParsedDocument parsed,
        (FixPlanSource Source, FixPlanPreview Preview) plan, IEnumerable<FixPlanOperation>? operations = null)
    {
        var registry = ProductionFixCapabilities.CreateApplyRegistry();
        foreach (var operation in operations ?? plan.Preview.Operations)
        {
            Assert.True(registry.TryGet(operation, out var provider));
            var finding = plan.Source.Findings.Single(value => value.FindingId == operation.SourceFindingIds.Single());
            Assert.Equal(FixApplyOutcome.Changed, await provider.ApplyAsync(
                new(path, parsed, finding, operation), CancellationToken.None));
        }
    }

    private static void SetExpected(ParagraphProperties properties, IEnumerable<FixPlanOperation> operations)
    {
        foreach (var operation in operations)
        {
            if (operation.PropertyIdentifier == "paragraph.alignment")
                properties.Justification = new Justification { Val = JustificationValues.Both };
            else if (operation.PropertyIdentifier == "paragraph.first-line-indent")
            {
                properties.Indentation ??= new Indentation();
                properties.Indentation.FirstLine = operation.Expected.Value;
            }
            else
            {
                properties.SpacingBetweenLines ??= new SpacingBetweenLines();
                if (operation.PropertyIdentifier == "paragraph.line-spacing-value")
                    properties.SpacingBetweenLines.Line = operation.Expected.Value;
                else properties.SpacingBetweenLines.LineRule = LineSpacingRuleValues.Auto;
            }
        }
    }

    private static void AssertTargetCompliant(ParsedDocument document, int paragraphIndex, bool expectFirstLine = true)
    {
        var paragraph = document.Paragraphs.Single(value => value.Index == paragraphIndex);
        Assert.Equal(240, paragraph.DirectLineSpacingValue);
        Assert.Equal("auto", paragraph.DirectLineSpacingRule, ignoreCase: true);
        Assert.Equal(ParsedAlignment.Justified, paragraph.DirectAlignment);
        if (expectFirstLine) Assert.Equal(567, paragraph.DirectFirstLineIndentTwips);
    }

    private static IReadOnlyList<ResolvedRuleFinding> Validate(ParsedDocument document) =>
        new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry(
            [new LineSpacingValidator(), new FirstLineIndentValidator(), new JustifiedValidator()]))
            .Validate(document, Snapshots(), CancellationToken.None).Findings;

    private static AuditRuleSnapshot[] Snapshots() =>
    [
        Snapshot("PPKI-LAY-017", "body.line-spacing-single", 17,
            "{\"value\":240,\"unit\":\"twip\",\"rule\":\"auto\"}"),
        Snapshot("PPKI-LAY-018", "body.first-line-indent-1cm", 18,
            "{\"value\":1,\"unit\":\"cm\"}"),
        Snapshot("PPKI-LAY-019", "body.justified", 19,
            "{\"accepted\":[\"Justified\"]}")
    ];

    private static AuditRuleSnapshot Snapshot(string rule, string key, int ordinal, string validation) => new()
    {
        AuditJobId = Guid.NewGuid(), RuleId = Guid.NewGuid(), RuleCode = rule, Domain = "LAY",
        Subdomain = "Paragraf", AppliesTo = "Semua", Element = "Format paragraf",
        RequirementJson = "{}", ValidationKey = key, ValidationJson = validation,
        Severity = RuleSeverity.Error, FixMode = FixMode.Auto, SourceReferenceJson = "{}",
        Layer = "profile", Precedence = 0, Ordinal = ordinal, SnapshotSchemaVersion = 1
    };

    private static (FixPlanSource Source, FixPlanPreview Preview) Plan(IReadOnlyList<ResolvedRuleFinding> values) =>
        Plan(values.Select(Finding).ToArray());

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

    private static (string CapabilityId, string Version) Contract(
        IReadOnlyList<RemediationCapability> capabilities, string validationKey)
    {
        var capability = capabilities.Single(value => value.ValidationKey == validationKey);
        return (capability.CapabilityId, capability.CapabilityVersion);
    }
}
