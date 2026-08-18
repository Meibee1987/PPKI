using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Worker;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class AutomaticRemediationPolicyTests
{
    public static TheoryData<string, string, string> AllowlistedContracts => new()
    {
        { "PPKI-LAY-005", "body.font-times-new-roman-12", "body-font-direct-run" },
        { "PPKI-LAY-017", "body.line-spacing-single", "body-line-spacing-direct-paragraph" },
        { "PPKI-LAY-018", "body.first-line-indent-1cm", "body-first-line-indent-direct-paragraph" },
        { "PPKI-LAY-019", "body.justified", "body-justified-direct-paragraph" },
        { "PPKI-ABS-011", "abstract.skripsi-single-spacing-zero-paragraph-spacing", "abstract-spacing-direct-paragraph" },
        { "PPKI-ABS-019", "abstract-summary-single-spacing-zero-paragraph-spacing", "abstract-spacing-direct-paragraph" },
        { "PPKI-HDG-006", "heading.chapter-centered", "chapter-centered-direct-paragraph" }
    };

    [Theory]
    [MemberData(nameof(AllowlistedContracts))]
    public void Exact_historical_contract_is_auto_apply_and_pins_production_provider(
        string ruleCode, string validationKey, string capabilityId)
    {
        var finding = Finding(ruleCode, validationKey, fixMode: FixMode.Manual);

        Assert.Equal(AutomaticRemediationPolicyOutcome.AutoApply,
            AutomaticRemediationPolicy.Classify(finding));
        Assert.True(AutomaticRemediationPolicy.TryGetAutoApply(finding, out var contract));
        Assert.Equal((capabilityId, "1.0"), (contract.CapabilityId, contract.CapabilityVersion));

        var preview = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities
            .Single(value => value.ValidationKey == validationKey);
        Assert.Equal((contract.CapabilityId, contract.CapabilityVersion),
            (preview.CapabilityId, preview.CapabilityVersion));
        Assert.Contains(ProductionFixCapabilities.CreateApplyRegistry().Providers,
            value => value.CapabilityId == contract.CapabilityId
                && value.CapabilityVersion == contract.CapabilityVersion);
    }

    [Theory]
    [InlineData("PPKI-LAY-999", "body.font-times-new-roman-12")]
    [InlineData("PPKI-LAY-005", "content.language-correction")]
    [InlineData("PPKI-LAY-019", "body.justified-approximate")]
    public void Unsupported_or_content_contract_is_never_auto_apply(string ruleCode, string validationKey)
    {
        Assert.Equal(AutomaticRemediationPolicyOutcome.ManualOnly,
            AutomaticRemediationPolicy.Classify(Finding(ruleCode, validationKey)));
    }

    [Theory]
    [InlineData(FindingStatus.Fixed, 1)]
    [InlineData(FindingStatus.Ignored, 1)]
    [InlineData(FindingStatus.Open, 0)]
    public void Non_open_or_invalid_historical_finding_fails_closed(FindingStatus state, int schemaVersion)
    {
        Assert.Equal(AutomaticRemediationPolicyOutcome.ManualOnly,
            AutomaticRemediationPolicy.Classify(Finding("PPKI-LAY-005", "body.font-times-new-roman-12",
                state: state, schemaVersion: schemaVersion)));
    }

    [Fact]
    public void Policy_is_typed_versioned_and_bounded_to_exact_allowlist()
    {
        Assert.Equal("auto-format/1.0", AutomaticRemediationPolicy.Version);
        Assert.Equal("AutoFormat", AutomaticRemediationPolicy.OrchestrationType);
        Assert.Equal(7, AutomaticRemediationPolicy.Contracts.Count);
        Assert.Equal(7, AutomaticRemediationPolicy.Contracts
            .Select(value => $"{value.RuleCode}|{value.ValidationKey}").Distinct().Count());
    }

    private static FixPlanFindingSnapshot Finding(
        string ruleCode,
        string validationKey,
        FixMode fixMode = FixMode.Auto,
        FindingStatus state = FindingStatus.Open,
        int schemaVersion = 1) => new(
            Guid.NewGuid(), 1, ruleCode, "LAY", "paragraph", validationKey,
            RuleSeverity.Error, fixMode, state, "{}", "{}", "{}", schemaVersion);
}

public sealed class AutomaticRemediationOrchestrationTests
{
    [Fact]
    public void Canonical_identity_is_stable_for_replay_and_changes_by_source()
    {
        var source = Guid.NewGuid();
        var first = AutomaticRemediationProcessor.CanonicalGuid(source, AutomaticRemediationPolicy.Version);
        Assert.Equal(first, AutomaticRemediationProcessor.CanonicalGuid(source, AutomaticRemediationPolicy.Version));
        Assert.NotEqual(first, AutomaticRemediationProcessor.CanonicalGuid(Guid.NewGuid(), AutomaticRemediationPolicy.Version));
    }

    [Fact]
    public void Plan_policy_guard_accepts_exact_provider_and_rejects_version_or_provider_substitution()
    {
        var finding = new FixPlanFindingSnapshot(Guid.NewGuid(), 1, "PPKI-LAY-019", "LAY", "paragraph",
            "body.justified", RuleSeverity.Error, FixMode.Auto, FindingStatus.Open, "{}", "{}", "{}", 1);
        var operation = new FixPlanOperation(FixOperationKind.SetProperty,
            "body-justified-direct-paragraph", "1.0", finding.RuleCode, finding.ValidationKey,
            [finding.FindingId], new("main-document", 0, null, 0, null), "paragraph.alignment",
            new("enum-code", "both"), false, 1, "finding-snapshot-match", "set-paragraph-alignment");

        Assert.True(AutomaticRemediationProcessor.MatchesPolicy([finding], [operation]));
        Assert.False(AutomaticRemediationProcessor.MatchesPolicy([finding], [operation with { CapabilityVersion = "2.0" }]));
        Assert.False(AutomaticRemediationProcessor.MatchesPolicy([finding], [operation with { CapabilityId = "other-provider" }]));
    }

    [Fact]
    public void Abstract_specific_operation_wins_over_equivalent_body_operation_without_last_write_wins()
    {
        var target = new FixTargetLocation("main-document", 2, null, 2, null);
        var expected = new FixExpectedValueDescriptor("twips", "240");
        FixPlanOperation Operation(string validationKey, string capability, Guid id) => new(
            FixOperationKind.SetProperty, capability, "1.0", "R", validationKey, [id], target,
            "paragraph.line-spacing-value", expected, false, 1, "finding-snapshot-match", "set-spacing");
        var bodyId = Guid.NewGuid();
        var result = AutomaticRemediationProcessor.ContextuallyManualFindingIds([
            Operation("body.line-spacing-single", "body-line-spacing-direct-paragraph", bodyId),
            Operation("abstract.skripsi-single-spacing-zero-paragraph-spacing", "abstract-spacing-direct-paragraph", Guid.NewGuid())]);

        Assert.Equal([bodyId], result);
    }

    [Fact]
    public void Durable_schema_enforces_one_pass_canonical_lineage_and_terminal_states()
    {
        var migration = File.ReadAllText(Path.Combine(RepositoryRoot(), "supabase", "migrations",
            "202608070001_automatic_format_remediation.sql"));
        Assert.Contains("uq_automatic_remediation_identity", migration, StringComparison.Ordinal);
        Assert.Contains("source_fix is not null", migration, StringComparison.Ordinal);
        Assert.Contains("Automatic remediation can only target an initial audit", migration, StringComparison.Ordinal);
        Assert.Contains("Terminal automatic remediation is immutable", migration, StringComparison.Ordinal);
        Assert.Contains("uq_automatic_remediation_fix_execution", migration, StringComparison.Ordinal);
        Assert.Contains("uq_automatic_remediation_reaudit", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Backend_trigger_is_created_with_initial_audit_and_ui_only_polls_safe_read_state()
    {
        var api = File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "services", "Ppki.Api", "Program.cs"));
        var ui = File.ReadAllText(Path.Combine(RepositoryRoot(), "apps", "web", "src", "components", "audit-findings-client.tsx"));
        Assert.Contains("AutomaticRemediationOrchestrations.Add", api, StringComparison.Ordinal);
        Assert.DoesNotContain("automatic-remediation", ui, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch(", ui, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("summary.automaticRemediation", ui, StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_read_model_exposes_backend_owned_canonical_reaudit_lineage()
    {
        var properties = typeof(AutomaticRemediationSummaryDto).GetProperties()
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(AutomaticRemediationSummaryDto.ResultDocumentVersionId), properties);
        Assert.Contains(nameof(AutomaticRemediationSummaryDto.ReauditJobId), properties);

        var readService = File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "src",
            "Ppki.Infrastructure", "AuditReadService.cs"));
        Assert.Contains("automatic.ResultDocumentVersionId", readService, StringComparison.Ordinal);
        Assert.Contains("automatic.ReauditJobId", readService, StringComparison.Ordinal);
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", ".."));
}
