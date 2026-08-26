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

public sealed class SafeHeadingFixProviderTests
{
    private static readonly IReadOnlyDictionary<string, string> Implemented =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["heading.chapter-bold"] = ChapterBoldFixProvider.Id,
            ["heading.chapter-no-period-no-underline"] = ChapterDecorationFixProvider.Id,
            ["heading.chapter-centered"] = ChapterCenteredFixProvider.Id,
            ["heading.subheading-decimal-left"] = SubheadingLeftFixProvider.Id,
            ["heading.subheading-bold-no-period-no-underline"] = SubheadingDecorationFixProvider.Id,
            ["heading.subsubheading-decimal-left"] = SubSubheadingLeftFixProvider.Id,
            ["heading.subsubheading-regular-no-period-no-underline"] = SubSubheadingDecorationFixProvider.Id
        };

    [Fact]
    public async Task Golden_safe_formatting_composes_preserves_content_and_classification_and_is_idempotent()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("safe-heading-fixers-mvp");
        var parser = new OpenXmlDocxParser();
        var before = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var sourceHash = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var classification = HeadingFingerprint(before);
        Assert.Equal(5, before.Headings.Count(value => value.Classification == HeadingClassification.Confirmed));
        Assert.Equal(4, before.Headings.Count(value => !before.Paragraphs.Single(paragraph => paragraph.Index == value.ParagraphIndex).IsInTable));
        Assert.DoesNotContain(before.Headings, value => value.Location.BodyElementIndex == 5);
        Assert.Single(before.FieldInventory);

        var safeFindings = Validate(before).Where(value =>
            value.Finding.Actual.Property is "bold" or "underline" or "alignment").ToArray();
        Assert.Equal(9, safeFindings.Length);
        var plan = Plan(safeFindings);
        Assert.Equal(FixPlanState.Ready, plan.Preview.State);
        Assert.Equal(9, plan.Preview.Operations.Count);
        var eligibility = new FixEligibilityService(ProductionFixCapabilities.CreatePreviewRegistry(),
            ProductionFixCapabilities.CreateApplyRegistry());
        Assert.All(plan.Source.Findings, finding => Assert.True(eligibility.Evaluate(new(
            plan.Source.AuditId, plan.Source.AuditStatus, plan.Source.DocumentVersionId, finding, null)).IsEligible));
        Assert.All(plan.Preview.Operations, operation =>
        {
            Assert.StartsWith("heading.", operation.PropertyIdentifier, StringComparison.Ordinal);
            Assert.False(operation.RequiresConfirmation);
            Assert.Equal("main-document-paragraph", operation.Target.Scope);
        });

        var capabilities = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities;
        foreach (var (validationKey, capabilityId) in Implemented)
        {
            var capability = capabilities.Single(value => value.ValidationKey == validationKey);
            Assert.Equal((capabilityId, "1.0"), (capability.CapabilityId, capability.CapabilityVersion));
        }

        string bodyBefore;
        string tableBefore;
        string fieldCodeBefore;
        string[] unrelatedRunProperties;
        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var body = package.MainDocumentPart!.Document!.Body!;
            var paragraphs = body.Elements<Paragraph>().ToArray();
            bodyBefore = paragraphs[3].OuterXml;
            tableBefore = body.Elements<Table>().Single().OuterXml;
            fieldCodeBefore = paragraphs[0].Descendants<FieldCode>().Single().Text;
            unrelatedRunProperties = paragraphs.Take(3).SelectMany(value => value.Descendants<Run>())
                .Select(value => PreservedRunProperties(value.RunProperties)).ToArray();
        }

        await Apply(workspace.WorkingPath, before, plan);
        var after = await parser.ParseAsync(workspace.WorkingPath, CancellationToken.None);
        Assert.DoesNotContain(Validate(after), value => value.Finding.Actual.Property is "bold" or "underline" or "alignment");
        Assert.Equal(TextFingerprint(before), TextFingerprint(after));
        Assert.Equal(classification, HeadingFingerprint(after));
        Assert.Equal(sourceHash, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
        Assert.Single(after.FieldInventory);
        Assert.Equal("REF", after.FieldInventory.Single().NormalizedInstruction);

        using (var package = WordprocessingDocument.Open(workspace.WorkingPath, false))
        {
            var body = package.MainDocumentPart!.Document!.Body!;
            var paragraphs = body.Elements<Paragraph>().ToArray();
            Assert.Equal(bodyBefore, paragraphs[3].OuterXml);
            Assert.Equal(tableBefore, body.Elements<Table>().Single().OuterXml);
            Assert.Equal(fieldCodeBefore, paragraphs[0].Descendants<FieldCode>().Single().Text);
            Assert.Equal(unrelatedRunProperties, paragraphs.Take(3).SelectMany(value => value.Descendants<Run>())
                .Select(value => PreservedRunProperties(value.RunProperties)));
            Assert.IsType<Hyperlink>(paragraphs[0].Descendants<Run>().ElementAt(1).Parent);
        }

        var documentBeforeReplay = await File.ReadAllBytesAsync(workspace.WorkingPath);
        var registry = ProductionFixCapabilities.CreateApplyRegistry();
        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(registry.TryGet(operation, out var provider));
            var finding = plan.Source.Findings.Single(value => value.FindingId == operation.SourceFindingIds.Single());
            Assert.Equal(FixApplyOutcome.NoChange, await provider.ApplyAsync(
                new(workspace.WorkingPath, after, finding, operation), CancellationToken.None));
        }
        Assert.Equal(documentBeforeReplay, await File.ReadAllBytesAsync(workspace.WorkingPath));
    }

    [Fact]
    public async Task Text_rewrites_numbering_and_ambiguous_targets_remain_unavailable()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("safe-heading-fixers-mvp");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var resolved = Validate(parsed);
        var unsupported = resolved.Where(value => value.Finding.Actual.Property is
            "trailingPunctuation" or "numberingPattern" or "uppercase").Select(Finding).ToArray();
        Assert.All(unsupported, finding =>
        {
            var matches = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities
                .Where(value => value.ValidationKey == finding.ValidationKey).ToArray();
            Assert.True(matches.Length == 0 || matches.All(value => !value.Provider.TryCreate(finding, out _, out _)));
            Assert.Equal(AutomaticRemediationPolicyOutcome.ManualOnly, AutomaticRemediationPolicy.Classify(finding));
        });
        Assert.DoesNotContain(ProductionFixCapabilities.CreatePreviewRegistry().Capabilities,
            value => value.ValidationKey is "heading.chapter-uppercase"
                or "heading.chapter-number-upper-roman-no-period" or "heading.maximum-depth-3");

        var punctuation = Finding(resolved.First(value => value.Finding.Actual.Property == "underline")) with
        {
            ActualJson = JsonSerializer.Serialize(new { property = "trailingPunctuation", normalizedValue = "period" }),
            ExpectedJson = JsonSerializer.Serialize(new
            {
                property = "trailingPunctuation", validationKey = "heading.chapter-no-period-no-underline",
                acceptedValues = new[] { "not-period" }
            })
        };
        var provider = new ChapterDecorationFixProvider();
        Assert.False(provider.TryCreate(punctuation, out _, out _));
        Assert.False(provider.TryCreate(punctuation with { FixMode = FixMode.Confirm }, out _, out _));
        var eligibility = new FixEligibilityService(ProductionFixCapabilities.CreatePreviewRegistry(),
            ProductionFixCapabilities.CreateApplyRegistry());
        Assert.Equal(FixEligibilityReasonCode.FindingContractUnsupported, eligibility.Evaluate(new(
            Guid.NewGuid(), AuditJobStatus.Completed, Guid.NewGuid(), punctuation, null)).ReasonCode);

        var candidate = parsed.Paragraphs.Single(value => value.Location?.BodyElementIndex == 5);
        var valid = Finding(resolved.First(value => value.Finding.Actual.Property == "bold"));
        var forged = valid with { LocationJson = JsonSerializer.Serialize(Location(candidate.Location!)) };
        Assert.True(new ChapterBoldFixProvider().TryCreate(forged, out var operation, out _));
        var bytes = await File.ReadAllBytesAsync(workspace.WorkingPath);
        var error = await Assert.ThrowsAsync<FixExecutionException>(() => new ChapterBoldFixProvider().ApplyAsync(
            new(workspace.WorkingPath, parsed, forged, ToOperation(operation, forged, ChapterBoldFixProvider.Id)), CancellationToken.None));
        Assert.Equal("fix-operation-target-precondition-failed", error.DiagnosticCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(workspace.WorkingPath));
    }

    [Fact]
    public async Task Exact_anchor_level_contract_and_expected_values_fail_closed()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("safe-heading-fixers-mvp");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var source = Finding(Validate(parsed).First(value => value.Snapshot.ValidationKey == "heading.chapter-centered"));
        var provider = new ChapterCenteredFixProvider();
        Assert.True(provider.TryCreate(source, out var draft, out _));
        var operation = ToOperation(draft, source, ChapterCenteredFixProvider.Id);
        Assert.False(provider.TryCreate(source with { ValidationKey = "heading.subheading-decimal-left" }, out _, out _));
        Assert.False(provider.TryCreate(source with { ExpectedJson = JsonSerializer.Serialize(new
        {
            property = "alignment", validationKey = source.ValidationKey, acceptedValues = new[] { "Both" }
        }) }, out _, out _));
        Assert.False(ProductionFixCapabilities.CreateApplyRegistry().CanApply(operation with { CapabilityVersion = "2.0" }));

        foreach (var forged in new[]
        {
            operation with { Target = operation.Target with { BodyElementIndex = 999 } },
            operation with { Target = operation.Target with { ParagraphIndex = 1, BodyElementIndex = 1 } },
            operation with { PropertyIdentifier = "heading.runs-bold" }
        })
        {
            var bytes = await File.ReadAllBytesAsync(workspace.WorkingPath);
            var error = await Assert.ThrowsAsync<FixExecutionException>(() => provider.ApplyAsync(
                new(workspace.WorkingPath, parsed, source, forged), CancellationToken.None));
            Assert.Contains(error.DiagnosticCode, new[]
            {
                "fix-operation-target-precondition-failed", "fix-operation-contract-invalid"
            });
            Assert.Equal(bytes, await File.ReadAllBytesAsync(workspace.WorkingPath));
        }
    }

    [Fact]
    public async Task Independent_heading_properties_compose_without_conflict_and_same_property_conflicts()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("safe-heading-fixers-mvp");
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        var chapter = Validate(parsed).Where(value => value.Finding.Location.ParagraphIndex == 0
            && value.Finding.Actual.Property is "bold" or "underline" or "alignment").ToArray();
        var plan = Plan(chapter);
        var capabilities = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities
            .ToDictionary(value => value.ValidationKey, StringComparer.Ordinal);
        FixPlanMutationCandidate Candidate(FixPlanOperation operation, FixExpectedValueDescriptor? expected = null) => new(
            plan.Source.DocumentVersionId, Guid.NewGuid(), operation.SourceFindingIds.Single(), FixMode.Auto,
            FixPlanDraftPreviewItemState.Previewable, "fix-plan-preview-ready", capabilities[operation.ValidationKey],
            new(operation.Target, operation.PropertyIdentifier, expected ?? operation.Expected,
                operation.PreconditionCode, operation.SummaryCode));
        var analyzer = new DeterministicFixPlanConflictAnalyzer();
        var independent = analyzer.Analyze(plan.Source.DocumentVersionId, plan.Preview.Operations.Select(value => Candidate(value)).ToArray());
        Assert.Equal(FixPlanMutationAnalysisState.Ready, independent.State);
        Assert.Equal(3, independent.IndependentItemCount);

        var alignment = plan.Preview.Operations.Single(value => value.PropertyIdentifier == "heading.alignment");
        var conflict = analyzer.Analyze(plan.Source.DocumentVersionId,
            [Candidate(alignment), Candidate(alignment, new("enum-code", "left"))]);
        Assert.Equal(FixPlanMutationAnalysisState.Conflict, conflict.State);
        Assert.Equal(2, conflict.ConflictItemCount);
    }

    private static async Task Apply(string path, ParsedDocument parsed, (FixPlanSource Source, FixPlanPreview Preview) plan)
    {
        var registry = ProductionFixCapabilities.CreateApplyRegistry();
        foreach (var operation in plan.Preview.Operations)
        {
            Assert.True(registry.TryGet(operation, out var provider));
            var finding = plan.Source.Findings.Single(value => value.FindingId == operation.SourceFindingIds.Single());
            Assert.Equal(FixApplyOutcome.Changed, await provider.ApplyAsync(
                new(path, parsed, finding, operation), CancellationToken.None));
        }
    }

    private static IReadOnlyList<ResolvedRuleFinding> Validate(ParsedDocument document) =>
        new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry([
            new ChapterNumberingValidator(), new HeadingDepthValidator(), new ChapterUppercaseValidator(),
            new ChapterBoldValidator(), new ChapterDecorationValidator(), new ChapterAlignmentValidator(),
            new SubheadingNumberingAlignmentValidator(), new SubheadingDecorationValidator(),
            new SubSubheadingNumberingAlignmentValidator(), new SubSubheadingDecorationValidator()
        ])).Validate(document, Snapshots(), CancellationToken.None).Findings;

    private static AuditRuleSnapshot[] Snapshots() =>
    [
        Snapshot("PPKI-HDG-001", "heading.chapter-number-upper-roman-no-period", 1),
        Snapshot("PPKI-HDG-002", "heading.maximum-depth-3", 2, "{\"maximumLevel\":3}"),
        Snapshot("PPKI-HDG-003", "heading.chapter-uppercase", 3),
        Snapshot("PPKI-HDG-004", "heading.chapter-bold", 4),
        Snapshot("PPKI-HDG-005", "heading.chapter-no-period-no-underline", 5),
        Snapshot("PPKI-HDG-006", "heading.chapter-centered", 6),
        Snapshot("PPKI-HDG-007", "heading.subheading-decimal-left", 7),
        Snapshot("PPKI-HDG-009", "heading.subheading-bold-no-period-no-underline", 9),
        Snapshot("PPKI-HDG-011", "heading.subsubheading-decimal-left", 11),
        Snapshot("PPKI-HDG-013", "heading.subsubheading-regular-no-period-no-underline", 13)
    ];

    private static AuditRuleSnapshot Snapshot(string rule, string key, int ordinal, string validation = "{}") => new()
    {
        AuditJobId = Guid.NewGuid(), RuleId = Guid.NewGuid(), RuleCode = rule, Domain = "HDG",
        Subdomain = "Heading", AppliesTo = "Semua", Element = "Heading", RequirementJson = "{}",
        ValidationKey = key, ValidationJson = validation, Severity = RuleSeverity.Error,
        FixMode = FixMode.Auto, SourceReferenceJson = "{}", Layer = "profile", Precedence = 0,
        Ordinal = ordinal, SnapshotSchemaVersion = 1
    };

    private static (FixPlanSource Source, FixPlanPreview Preview) Plan(IReadOnlyList<ResolvedRuleFinding> findings) =>
        Plan(findings.Select(Finding).ToArray());

    private static (FixPlanSource Source, FixPlanPreview Preview) Plan(IReadOnlyList<FixPlanFindingSnapshot> findings)
    {
        var source = new FixPlanSource(Guid.NewGuid(), AuditJobStatus.Completed, Guid.NewGuid(), new string('a', 64),
            new string('b', 64), DocumentKind.Skripsi, findings);
        return (source, new DeterministicFixPlanPreviewPlanner(ProductionFixCapabilities.CreatePreviewRegistry()).Create(source));
    }

    private static FixPlanFindingSnapshot Finding(ResolvedRuleFinding resolved) => new(
        Guid.NewGuid(), resolved.Snapshot.Ordinal, resolved.Snapshot.RuleCode, resolved.Snapshot.Domain,
        resolved.Snapshot.Element, resolved.Snapshot.ValidationKey, resolved.Snapshot.Severity,
        resolved.Snapshot.FixMode, FindingStatus.Open, JsonSerializer.Serialize(resolved.Finding.Actual),
        JsonSerializer.Serialize(resolved.Finding.Expected), JsonSerializer.Serialize(resolved.Finding.Location),
        resolved.Snapshot.SnapshotSchemaVersion);

    private static FixPlanOperation ToOperation(FixOperationDraft draft, FixPlanFindingSnapshot finding, string capabilityId) =>
        new(FixOperationKind.SetProperty, capabilityId, "1.0", finding.RuleCode, finding.ValidationKey,
            [finding.FindingId], draft.Target, draft.PropertyIdentifier, draft.Expected, false, 1,
            draft.PreconditionCode, draft.SummaryCode);

    private static LayoutFindingLocation Location(DocumentElementLocation location) => new(
        location.ToCompactString(), location.SectionIndex, location.BodyElementIndex,
        location.ParagraphIndex, location.RunIndex);

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

    private static string HeadingFingerprint(ParsedDocument document) => string.Join("\n", document.Headings
        .OrderBy(value => value.Index).Select(value => JsonSerializer.Serialize(new
        {
            value.Index, value.ParagraphIndex, Location = value.Location.ToCompactString(), value.Level,
            value.Classification, value.EffectiveParagraphStyleId, value.OutlineLevel, value.StartsNewSection,
            Evidence = value.Evidence,
            Semantic = document.DocumentStructure.Sections.Where(section => section.HeadingIndex == value.Index)
                .OrderBy(section => section.Index).Select(section => new
                {
                    section.Index, section.Kind, section.Zone, section.ClassificationState,
                    section.ClassificationBasis, section.HeadingLevel, section.NumberingCategory,
                    section.ParentSectionIndex, section.HeadingLocation, section.Evidence
                })
        })));

    private static string PreservedRunProperties(RunProperties? properties)
    {
        var clone = properties is null ? new RunProperties() : (RunProperties)properties.CloneNode(true);
        clone.RemoveAllChildren<Bold>();
        clone.RemoveAllChildren<Underline>();
        return clone.OuterXml;
    }
}
