using System.Text.Json;
using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixPlanConflictAnalyzerTests
{
    private readonly DeterministicFixPlanConflictAnalyzer analyzer = new();

    [Fact]
    public void Separate_locations_remain_independent_and_batchable()
    {
        var result = Analyze(Candidate(1, paragraph: 1), Candidate(2, paragraph: 2));

        Assert.Equal(FixPlanMutationAnalysisState.Ready, result.State);
        Assert.Equal(2, result.IndependentItemCount);
        Assert.Empty(result.Conflicts);
        Assert.Equal([1, 2], result.Items.Select(value => value.ExecutionOrdinal).Order().ToArray());
    }

    [Theory]
    [InlineData("paragraph.alignment", "paragraph.line-spacing-value")]
    [InlineData("run.font-size", "run.font-family-ascii")]
    [InlineData("section.margin-left", "section.margin-right")]
    public void Separate_properties_on_same_location_remain_independent(string first, string second)
    {
        var scope = first.StartsWith("run", StringComparison.Ordinal) ? "main-document-run"
            : first.StartsWith("section", StringComparison.Ordinal) ? "main-document-section"
            : "main-document-paragraph";
        var result = Analyze(Candidate(1, property: first, scope: scope),
            Candidate(2, property: second, scope: scope));

        Assert.Equal(FixPlanMutationAnalysisState.Ready, result.State);
        Assert.All(result.Items, value => Assert.Equal(FixPlanMutationItemStatus.Independent, value.Status));
    }

    [Fact]
    public void Same_key_and_value_is_explicit_equivalent_duplicate_with_one_logical_order()
    {
        var result = Analyze(Candidate(2), Candidate(1));

        Assert.Equal(FixPlanMutationAnalysisState.Ready, result.State);
        Assert.Equal(2, result.DuplicateEquivalentItemCount);
        Assert.All(result.Items, value => Assert.Equal("fix-mutation-duplicate-equivalent", value.ReasonCode));
        Assert.Single(result.Relationships);
        Assert.Equal(FixPlanMutationRelationshipKind.DuplicateEquivalent, result.Relationships[0].Kind);
        Assert.Single(result.Items.Select(value => value.ExecutionOrdinal).Distinct());
    }

    [Fact]
    public void Equivalent_duplicate_that_capability_forbids_merging_fails_closed()
    {
        var capability = Capability("cap-a", merge: false);
        var result = Analyze(Candidate(1, capability: capability), Candidate(2, capability: capability));

        Assert.Equal(FixPlanMutationAnalysisState.Conflict, result.State);
        Assert.All(result.Items, value => Assert.Equal("fix-mutation-duplicate-not-mergeable", value.ReasonCode));
    }

    [Theory]
    [InlineData("enum-code", "left", "enum-code", "justified")]
    [InlineData("twips", "240", "twips", "360")]
    [InlineData("half-points", "22", "half-points", "24")]
    [InlineData("string-code", "Arial", "string-code", "Times New Roman")]
    public void Same_key_with_incompatible_outcome_is_conflict(
        string firstType, string firstValue, string secondType, string secondValue)
    {
        var result = Analyze(Candidate(1, expectedType: firstType, expected: firstValue),
            Candidate(2, expectedType: secondType, expected: secondValue));

        Assert.Equal(FixPlanMutationAnalysisState.Conflict, result.State);
        Assert.Equal(2, result.ConflictItemCount);
        Assert.All(result.Items, value => Assert.Equal(FixPlanMutationItemStatus.Conflicting, value.Status));
        Assert.Equal("fix-mutation-contradictory-outcome", Assert.Single(result.Conflicts).ReasonCode);
        Assert.All(result.Items, value => Assert.Null(value.ExecutionOrdinal));
    }

    [Fact]
    public void Conflict_has_bounded_actionable_code_and_no_winner()
    {
        var result = Analyze(Candidate(20, expected: "left"), Candidate(10, expected: "right"));
        var conflict = Assert.Single(result.Conflicts);

        Assert.Matches("^[a-z0-9.-]{1,128}$", conflict.ReasonCode);
        Assert.Equal([Id(10), Id(20)], conflict.ItemIds);
        Assert.DoesNotContain(result.Items, value => value.ExecutionOrdinal is not null);
    }

    [Fact]
    public void Reversing_input_order_produces_byte_identical_analysis()
    {
        var values = new[] { Candidate(3, expected: "center"), Candidate(1, expected: "left"),
            Candidate(2, expected: "right") };

        var forward = JsonSerializer.Serialize(analyzer.Analyze(SourceVersion, values));
        var reverse = JsonSerializer.Serialize(analyzer.Analyze(SourceVersion, values.Reverse().ToArray()));
        Assert.Equal(forward, reverse);
    }

    [Theory]
    [InlineData("main-document-paragraph", null, 0, null)]
    [InlineData("main-document-paragraph", 0, null, null)]
    [InlineData("main-document-run", 0, 0, null)]
    [InlineData("main-document-section", null, null, null)]
    public void Missing_anchor_fails_closed_without_fuzzy_retargeting(
        string scope, int? body, int? paragraph, int? run)
    {
        var operation = Operation(scope: scope, body: body, paragraph: paragraph, run: run);
        var result = Analyze(Candidate(1, operation: operation));
        var item = Assert.Single(result.Items);

        Assert.Equal(FixPlanMutationAnalysisState.Stale, result.State);
        Assert.Equal(FixPlanMutationItemStatus.Stale, item.Status);
        Assert.Equal("fix-mutation-anchor-missing", item.ReasonCode);
        Assert.Null(item.MutationKey);
    }

    [Fact]
    public void Wrong_source_version_anchor_fails_closed()
    {
        var result = analyzer.Analyze(SourceVersion, [Candidate(1) with { SourceDocumentVersionId = Id(999) }]);
        var item = Assert.Single(result.Items);
        Assert.Equal(FixPlanMutationItemStatus.Stale, item.Status);
        Assert.Equal("fix-mutation-source-version-mismatch", item.ReasonCode);
    }

    [Theory]
    [InlineData("unknown-target")]
    [InlineData("")]
    [InlineData("Main Document Paragraph")]
    public void Unsupported_mutation_target_fails_safely(string scope)
    {
        var result = Analyze(Candidate(1, operation: Operation(scope: scope)));
        var item = Assert.Single(result.Items);
        Assert.Equal(FixPlanMutationItemStatus.Unavailable, item.Status);
        Assert.Equal("fix-mutation-target-unsupported", item.ReasonCode);
    }

    [Fact]
    public void Requires_before_dependency_produces_deterministic_order()
    {
        var second = Capability("cap-b");
        var first = Capability("cap-a", dependencies:
            [new("cap-b", "1.0", FixCapabilityDependencyKind.RequiresBefore)]);
        var result = Analyze(Candidate(2, paragraph: 2, capability: second),
            Candidate(1, paragraph: 1, capability: first));

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal(FixPlanMutationRelationshipKind.RequiresBefore, relationship.Kind);
        Assert.Equal(Id(1), relationship.BeforeItemId);
        Assert.Equal(Id(2), relationship.AfterItemId);
        Assert.True(Item(result, 1).ExecutionOrdinal < Item(result, 2).ExecutionOrdinal);
        Assert.All(result.Items, value => Assert.Equal(FixPlanMutationItemStatus.Ordered, value.Status));
    }

    [Fact]
    public void Requires_after_dependency_produces_deterministic_order()
    {
        var first = Capability("cap-a");
        var second = Capability("cap-b", dependencies:
            [new("cap-a", "1.0", FixCapabilityDependencyKind.RequiresAfter)]);
        var result = Analyze(Candidate(2, paragraph: 2, capability: second),
            Candidate(1, paragraph: 1, capability: first));

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal(FixPlanMutationRelationshipKind.RequiresAfter, relationship.Kind);
        Assert.Equal(Id(1), relationship.BeforeItemId);
        Assert.Equal(Id(2), relationship.AfterItemId);
        Assert.True(Item(result, 1).ExecutionOrdinal < Item(result, 2).ExecutionOrdinal);
    }

    [Fact]
    public void Circular_dependency_fails_safely_without_arbitrary_order()
    {
        var first = Capability("cap-a", dependencies:
            [new("cap-b", "1.0", FixCapabilityDependencyKind.RequiresBefore)]);
        var second = Capability("cap-b", dependencies:
            [new("cap-a", "1.0", FixCapabilityDependencyKind.RequiresBefore)]);
        var result = Analyze(Candidate(1, paragraph: 1, capability: first),
            Candidate(2, paragraph: 2, capability: second));

        Assert.Equal(FixPlanMutationAnalysisState.Conflict, result.State);
        Assert.All(result.Items, value => Assert.Equal(FixPlanMutationItemStatus.DependencyCycle, value.Status));
        Assert.All(result.Items, value => Assert.Null(value.ExecutionOrdinal));
        Assert.Equal("fix-mutation-dependency-cycle", Assert.Single(result.Conflicts).ReasonCode);
    }

    [Theory]
    [InlineData("cap-a", "cap-b")]
    [InlineData("layout", "paragraph")]
    [InlineData("paragraph", "heading")]
    public void Unrelated_capabilities_get_no_invented_dependency(string firstId, string secondId)
    {
        var result = Analyze(Candidate(1, paragraph: 1, capability: Capability(firstId)),
            Candidate(2, paragraph: 2, capability: Capability(secondId)));
        Assert.Empty(result.Relationships);
        Assert.All(result.Items, value => Assert.Equal(FixPlanMutationItemStatus.Independent, value.Status));
    }

    [Theory]
    [InlineData(FixMode.Manual)]
    [InlineData(FixMode.Report)]
    public void Ineligible_modes_do_not_become_executable(FixMode mode)
    {
        var candidate = Candidate(1) with
        {
            FixMode = mode, PreviewState = FixPlanDraftPreviewItemState.Ineligible,
            PreviewReasonCode = "fix-plan-preview-item-ineligible", Capability = null, Operation = null
        };
        var item = Assert.Single(Analyze(candidate).Items);
        Assert.Equal(FixPlanMutationItemStatus.Ineligible, item.Status);
        Assert.Null(item.ExecutionOrdinal);
    }

    [Fact]
    public void Confirm_analysis_does_not_change_mode_or_imply_approval()
    {
        var candidate = Candidate(1) with { FixMode = FixMode.Confirm };
        var before = JsonSerializer.Serialize(candidate);
        var result = Analyze(candidate);
        Assert.Equal(before, JsonSerializer.Serialize(candidate));
        Assert.Equal(FixPlanMutationAnalysisState.Ready, result.State);
        Assert.Equal(FixMode.Confirm, candidate.FixMode);
    }

    [Theory]
    [InlineData("main-document-section", "section.margin-left", 0)]
    [InlineData("main-document-paragraph", "paragraph.alignment", 1)]
    [InlineData("main-document-paragraph", "heading.alignment", 2)]
    [InlineData("main-document-run", "run.font-size", 3)]
    public void Layout_paragraph_heading_and_run_keys_are_typed_and_safe(
        string scope, string property, int index)
    {
        var result = Analyze(Candidate(1, scope: scope, property: property,
            section: scope.EndsWith("section", StringComparison.Ordinal) ? index : null,
            paragraph: scope.EndsWith("section", StringComparison.Ordinal) ? null : index,
            run: scope.EndsWith("run", StringComparison.Ordinal) ? 0 : null));
        var key = Assert.Single(result.Items).MutationKey!;
        Assert.Equal(SourceVersion, key.SourceDocumentVersionId);
        Assert.Equal(scope, key.Scope);
        Assert.Equal(property, key.PropertyIdentifier);
    }

    [Theory]
    [InlineData("main-document-section", "section.margin-left", "main-document-paragraph", "paragraph.alignment")]
    [InlineData("main-document-paragraph", "paragraph.line-spacing-value", "main-document-paragraph", "heading.alignment")]
    [InlineData("main-document-paragraph", "paragraph.alignment", "main-document-run", "run.font-size")]
    public void Safe_cross_scope_combinations_remain_batchable(
        string firstScope, string firstProperty, string secondScope, string secondProperty)
    {
        var result = Analyze(Candidate(1, scope: firstScope, property: firstProperty),
            Candidate(2, scope: secondScope, property: secondProperty, paragraph: 2));
        Assert.Equal(FixPlanMutationAnalysisState.Ready, result.State);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Multiple_contradictory_items_form_one_sorted_conflict_group()
    {
        var result = Analyze(Candidate(30, expected: "left"), Candidate(10, expected: "right"),
            Candidate(20, expected: "center"));
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal([Id(10), Id(20), Id(30)], conflict.ItemIds);
        Assert.Equal(3, result.ConflictItemCount);
        Assert.Equal(3, result.Relationships.Count);
    }

    [Fact]
    public void Analysis_is_pure_and_does_not_call_provider_or_apply_contracts()
    {
        var provider = new ThrowingProvider();
        var capability = Capability("never-called", provider: provider);
        var candidates = new[] { Candidate(1, capability: capability), Candidate(2, paragraph: 2, capability: capability) };
        var before = JsonSerializer.Serialize(candidates);

        _ = Analyze(candidates);
        Assert.Equal(before, JsonSerializer.Serialize(candidates));
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void Result_contract_contains_no_text_openxml_provider_payload_or_paths()
    {
        var names = typeof(FixPlanMutationAnalysisDto).GetProperties()
            .Concat(typeof(FixPlanMutationAnalysisItemDto).GetProperties())
            .Concat(typeof(FixPlanMutationKeyDto).GetProperties())
            .Concat(typeof(FixPlanMutationConflictDto).GetProperties())
            .Select(value => value.Name).ToArray();
        foreach (var forbidden in new[] { "Text", "OpenXml", "Payload", "Path", "Url", "Storage", "Sql",
                     "Token", "Exception", "Message", "Actual", "Expected" })
            Assert.DoesNotContain(names, value => value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

        var json = JsonSerializer.Serialize(Analyze(Candidate(1)));
        Assert.DoesNotContain("thesis", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Times New Roman", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_capabilities_declare_no_dependencies_that_architecture_does_not_require()
    {
        var capabilities = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities;
        Assert.All(capabilities, value => Assert.Empty(value.Dependencies ?? []));
    }

    [Fact]
    public void Api_extends_existing_owned_preview_and_introduces_no_s7_t06_commands()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        var service = Source("backend", "src", "Ppki.FixEngine", "FixPlanDraftPreviewService.cs");
        Assert.Contains("IFixPlanConflictAnalyzer", api, StringComparison.Ordinal);
        Assert.Contains("LoadOwnedAsync", service, StringComparison.Ordinal);
        Assert.Contains("outcomes.Select(value => value.Candidate)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentVersions.Add", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Enqueue", service, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad dependency id", "1.0")]
    [InlineData("valid-id", "bad version")]
    public void Invalid_dependency_metadata_is_rejected(string id, string version)
    {
        var capability = Capability("cap-a", dependencies:
            [new(id, version, FixCapabilityDependencyKind.RequiresBefore)]);
        var error = Assert.Throws<FixPlanConfigurationException>(() => new RemediationCapabilityRegistry([capability]));
        Assert.Equal("fix-capability-dependency-configuration-invalid", error.DiagnosticCode);
    }

    private FixPlanMutationAnalysisDto Analyze(params FixPlanMutationCandidate[] values) =>
        analyzer.Analyze(SourceVersion, values);

    private static FixPlanMutationAnalysisItemDto Item(FixPlanMutationAnalysisDto result, int id) =>
        result.Items.Single(value => value.ItemId == Id(id));

    private static FixPlanMutationCandidate Candidate(int id, string property = "paragraph.alignment",
        string expectedType = "enum-code", string expected = "justified", string scope = "main-document-paragraph",
        int? body = 0, int? section = null, int? paragraph = 0, int? run = null,
        RemediationCapability? capability = null, FixOperationDraft? operation = null)
    {
        if (operation is null)
        {
            if (scope == "main-document-section")
                operation = Operation(property, expectedType, expected, scope, null, section ?? 0, null, null);
            else if (scope == "main-document-run")
                operation = Operation(property, expectedType, expected, scope, body, section, paragraph, run ?? 0);
            else
                operation = Operation(property, expectedType, expected, scope, body, section, paragraph, null);
        }
        return new(SourceVersion, Id(id), Id(100 + id), FixMode.Auto,
            FixPlanDraftPreviewItemState.Previewable, "fix-plan-preview-ready",
            capability ?? Capability("cap-a"), operation);
    }

    private static FixOperationDraft Operation(string property = "paragraph.alignment",
        string expectedType = "enum-code", string expected = "justified", string scope = "main-document-paragraph",
        int? body = 0, int? section = null, int? paragraph = 0, int? run = null) => new(
            new(scope, body, section, paragraph, run), property, new(expectedType, expected),
            "source-finding-snapshot-must-match", "safe-summary");

    private static RemediationCapability Capability(string id, bool merge = true,
        IReadOnlyList<FixCapabilityDependency>? dependencies = null, IFixPreviewProvider? provider = null) => new(
            id, "1.0", $"{id}.validation", FixOperationKind.SetProperty,
            ["actual", "expected", "location"], false, true, $"{id}.preview", $"{id}.summary", merge,
            provider ?? new ThrowingProvider(), dependencies);

    private static readonly Guid SourceVersion = Id(900);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed class ThrowingProvider : IFixPreviewProvider
    {
        public int CallCount { get; private set; }
        public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
        {
            CallCount++;
            throw new InvalidOperationException("private thesis content");
        }
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
