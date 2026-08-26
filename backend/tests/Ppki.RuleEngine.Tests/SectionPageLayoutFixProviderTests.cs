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

public sealed class SectionPageLayoutFixProviderTests
{
    private static readonly (IDocumentRuleValidator Validator, string Key, string Rule)[] Contracts =
    [
        (new PageSizeA4Validator(), "section.page-size-a4", "PPKI-LAY-003"),
        (new MarginLeftValidator(), "section.margin-left-4cm", "PPKI-LAY-008"),
        (new MarginRightValidator(), "section.margin-right-3cm", "PPKI-LAY-009"),
        (new MarginTopValidator(), "section.margin-top-3cm", "PPKI-LAY-010"),
        (new MarginBottomValidator(), "section.margin-bottom-3cm", "PPKI-LAY-011")
    ];

    [Fact]
    public async Task Golden_multisection_fixture_fails_before_and_passes_after_exact_operations()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("section-page-layout-fixers");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var beforeText = before.Paragraphs.Select(value => value.Text).ToArray();
        var originalChecksum = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        Assert.Equal(2, before.Sections.Count);
        Assert.Equal(ParsedPageOrientation.Landscape, before.Sections[1].EffectiveFormatting?.Orientation.Value);

        var resolved = Contracts.SelectMany(contract => Validate(contract.Validator,
            Snapshot(contract.Key, contract.Rule), before)).ToArray();
        Assert.Equal(10, resolved.Length);
        var plan = Plan(resolved);
        Assert.Equal(FixPlanState.Ready, plan.Preview.State);
        Assert.Equal(10, plan.Preview.Operations.Count);

        var providers = ProductionFixCapabilities.CreateApplyRegistry();
        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(providers.TryGet(operation, out var provider));
            var findingId = Assert.Single(operation.SourceFindingIds);
            var finding = plan.Source.Findings.Single(value => value.FindingId == findingId);
            Assert.Equal(FixApplyOutcome.Changed, await provider.ApplyAsync(
                new(workspace.WorkingPath, before, finding, operation), CancellationToken.None));
        }

        var after = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        Assert.Equal(beforeText, after.Paragraphs.Select(value => value.Text));
        Assert.Equal(originalChecksum, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
        foreach (var contract in Contracts)
            Assert.Empty(Validate(contract.Validator, Snapshot(contract.Key, contract.Rule), after));
        Assert.Equal(ParsedPageOrientation.Landscape, after.Sections[1].EffectiveFormatting?.Orientation.Value);

        string[] sectionXmlBeforeSecondApply;
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
            sectionXmlBeforeSecondApply = after.Sections.Select(section => XmlSection(package, section).OuterXml).ToArray();
        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(providers.TryGet(operation, out var provider));
            var findingId = Assert.Single(operation.SourceFindingIds);
            var finding = plan.Source.Findings.Single(value => value.FindingId == findingId);
            Assert.Equal(FixApplyOutcome.NoChange, await provider.ApplyAsync(
                new(workspace.WorkingPath, after, finding, operation), CancellationToken.None));
        }
        using var afterSecondPackage = WordprocessingDocument.Open(workspace.WorkingPath, false);
        Assert.Equal(sectionXmlBeforeSecondApply,
            after.Sections.Select(section => XmlSection(afterSecondPackage, section).OuterXml));
    }

    [Theory]
    [InlineData("section.page-size-a4", 0)]
    [InlineData("section.page-size-a4", 1)]
    [InlineData("section.margin-left-4cm", 0)]
    [InlineData("section.margin-right-3cm", 1)]
    [InlineData("section.margin-top-3cm", 0)]
    [InlineData("section.margin-bottom-3cm", 1)]
    public async Task Apply_mutates_only_the_intended_attribute_at_the_exact_section_anchor(
        string validationKey, int sectionIndex)
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("section-page-layout-fixers");
        var parser = new OpenXmlDocxParser();
        var parsed = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var contract = Contracts.Single(value => value.Key == validationKey);
        var resolved = Validate(contract.Validator, Snapshot(contract.Key, contract.Rule), parsed)
            .Single(value => value.Finding.Location.SectionIndex == sectionIndex);
        var plan = Plan([resolved]);
        var operation = Assert.Single(plan.Preview.Operations);

        string untouchedBefore;
        SectionProperties expected;
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            expected = (SectionProperties)XmlSection(package, parsed.Sections[sectionIndex]).CloneNode(true);
            untouchedBefore = XmlSection(package, parsed.Sections[1 - sectionIndex]).OuterXml;
        }
        SetExpected(expected, operation);

        Assert.True(ProductionFixCapabilities.CreateApplyRegistry().TryGet(operation, out var provider));
        Assert.Equal(FixApplyOutcome.Changed, await provider.ApplyAsync(
            new(workspace.WorkingPath, parsed, plan.Source.Findings.Single(), operation), CancellationToken.None));

        using var afterPackage = WordprocessingDocument.Open(workspace.WorkingPath, false);
        Assert.Equal(expected.OuterXml, XmlSection(afterPackage, parsed.Sections[sectionIndex]).OuterXml);
        Assert.Equal(untouchedBefore, XmlSection(afterPackage, parsed.Sections[1 - sectionIndex]).OuterXml);
    }

    [Fact]
    public void Preview_rejects_non_exact_section_anchor()
    {
        var finding = Finding(new ResolvedRuleFinding(Snapshot("section.margin-left-4cm", "PPKI-LAY-008"),
            new("layout.marginLeft.mismatch",
                new("marginLeft", "1440", "1440", "twip", FormattingResolutionState.Resolved,
                    FormattingSourceKind.SectionProperties, null, false, null, 0, null, null),
                new("marginLeft", ["2268"], "twip", "0", "resolved-snapshot-validation-key", "section.margin-left-4cm"),
                new("maindocument/s:0/kind:section", 0, null, null, null), 0)));
        var provider = new SectionMarginFixProvider();
        Assert.False(provider.TryCreate(finding, out _, out _));
    }

    [Fact]
    public async Task Apply_fails_closed_for_stale_section_anchor_with_bounded_diagnostic()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("section-page-layout-fixers");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var resolved = Validate(new MarginLeftValidator(),
            Snapshot("section.margin-left-4cm", "PPKI-LAY-008"), parsed).First();
        var plan = Plan([resolved]);
        var operation = Assert.Single(plan.Preview.Operations);
        var stale = operation with { Target = operation.Target with { SectionIndex = 99 } };
        Assert.True(ProductionFixCapabilities.CreateApplyRegistry().TryGet(stale, out var provider));

        var exception = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(
            new(workspace.WorkingPath, parsed, plan.Source.Findings.Single(), stale), CancellationToken.None));
        Assert.Equal("fix-operation-target-precondition-failed", exception.DiagnosticCode);
        Assert.DoesNotContain("Bagian", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Providers_reject_wrong_validation_key_and_registry_rejects_wrong_version()
    {
        var pageProvider = new SectionPageSizeFixProvider();
        var wrong = new FixPlanFindingSnapshot(Guid.NewGuid(), 1, "PPKI-LAY-008", "LAY", "Section",
            "section.margin-left-4cm", RuleSeverity.Error, FixMode.Auto, FindingStatus.Open,
            "{}", "{}", "{}", 1);
        Assert.False(pageProvider.TryCreate(wrong, out _, out _));

        var operation = new FixPlanOperation(FixOperationKind.SetProperty, SectionPageSizeFixProvider.Id, "2.0",
            "PPKI-LAY-003", "section.page-size-a4", [Guid.NewGuid()],
            new("main-document-section", 0, 0, null, null), "section.page-size",
            new("twips-pair", "11906x16838"), false, 0,
            "source-finding-snapshot-must-match", "set-section-page-size-a4");
        Assert.False(ProductionFixCapabilities.CreateApplyRegistry().CanApply(operation));
    }

    private static SectionProperties XmlSection(WordprocessingDocument package, ParsedSection parsed)
    {
        var body = package.MainDocumentPart!.Document!.Body!;
        var element = body.Elements().ElementAt(parsed.Location!.BodyElementIndex!.Value);
        return element is SectionProperties direct
            ? direct
            : ((Paragraph)element).ParagraphProperties!.SectionProperties!;
    }

    private static void SetExpected(SectionProperties section, FixPlanOperation operation)
    {
        if (operation.PropertyIdentifier == "section.page-size")
        {
            var parts = operation.Expected.Value.Split('x');
            var size = section.GetFirstChild<PageSize>()!;
            size.Width = uint.Parse(parts[0]);
            size.Height = uint.Parse(parts[1]);
            return;
        }
        var margin = section.GetFirstChild<PageMargin>()!;
        var value = long.Parse(operation.Expected.Value);
        if (operation.PropertyIdentifier == "section.margin-left") margin.Left = checked((uint)value);
        else if (operation.PropertyIdentifier == "section.margin-right") margin.Right = checked((uint)value);
        else if (operation.PropertyIdentifier == "section.margin-top") margin.Top = checked((int)value);
        else margin.Bottom = checked((int)value);
    }

    private static IReadOnlyList<ResolvedRuleFinding> Validate(IDocumentRuleValidator validator,
        AuditRuleSnapshot snapshot, ParsedDocument document) =>
        new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry([validator]))
            .Validate(document, [snapshot], CancellationToken.None).Findings;

    private static (FixPlanSource Source, FixPlanPreview Preview) Plan(IReadOnlyList<ResolvedRuleFinding> resolved)
    {
        var findings = resolved.Select(Finding).ToArray();
        var source = new FixPlanSource(Guid.NewGuid(), AuditJobStatus.Completed, Guid.NewGuid(), new string('a', 64),
            new string('b', 64), DocumentKind.Skripsi, findings);
        var preview = new DeterministicFixPlanPreviewPlanner(ProductionFixCapabilities.CreatePreviewRegistry()).Create(source);
        return (source, preview);
    }

    private static FixPlanFindingSnapshot Finding(ResolvedRuleFinding resolved) => new(
        Guid.NewGuid(), resolved.Snapshot.Ordinal, resolved.Snapshot.RuleCode, resolved.Snapshot.Domain,
        resolved.Snapshot.Element, resolved.Snapshot.ValidationKey, resolved.Snapshot.Severity,
        resolved.Snapshot.FixMode, FindingStatus.Open, JsonSerializer.Serialize(resolved.Finding.Actual),
        JsonSerializer.Serialize(resolved.Finding.Expected), JsonSerializer.Serialize(resolved.Finding.Location),
        resolved.Snapshot.SnapshotSchemaVersion);

    private static AuditRuleSnapshot Snapshot(string validationKey, string ruleCode) => new()
    {
        AuditJobId = Guid.NewGuid(), RuleId = Guid.NewGuid(), RuleCode = ruleCode, Domain = "LAY",
        Subdomain = "Synthetic", AppliesTo = "Semua", Element = "Synthetic section layout",
        RequirementJson = "{}", ValidationKey = validationKey, ValidationJson = "{}",
        Severity = RuleSeverity.Error, FixMode = FixMode.Auto, SourceReferenceJson = "{}",
        Layer = "profile", Precedence = 0, Ordinal = Array.FindIndex(Contracts, value => value.Key == validationKey) + 1,
        SnapshotSchemaVersion = 1
    };
}
