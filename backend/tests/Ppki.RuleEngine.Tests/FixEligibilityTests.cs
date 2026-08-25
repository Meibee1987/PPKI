using System.Text.Json;
using System.Text.RegularExpressions;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.FixEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixEligibilityTests
{
    [Fact]
    public void Open_auto_with_compatible_registered_fixer_is_eligible()
    {
        var result = Service().Evaluate(Input());

        Assert.True(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.Eligible, result.ReasonCode);
        Assert.False(result.RequiresExplicitApproval);
    }

    [Fact]
    public void Open_auto_without_registered_preview_fixer_is_ineligible()
    {
        var result = Service(preview: []).Evaluate(Input());

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FixerNotRegistered, result.ReasonCode);
    }

    [Fact]
    public void Catalog_auto_without_registered_apply_fixer_is_ineligible()
    {
        var result = Service(apply: []).Evaluate(Input());

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FixerNotRegistered, result.ReasonCode);
    }

    [Fact]
    public void Incompatible_provider_contract_is_ineligible()
    {
        var result = Service(provider: new StubPreviewProvider(accept: false)).Evaluate(Input());

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FindingContractUnsupported, result.ReasonCode);
    }

    [Fact]
    public void Provider_version_mismatch_is_ineligible()
    {
        var result = Service(apply: [new StubApplyProvider("capability", "2.0")]).Evaluate(Input());

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FixerVersionIncompatible, result.ReasonCode);
    }

    [Fact]
    public void Open_confirm_with_compatible_fixer_requires_explicit_approval()
    {
        var input = Input(FixMode.Confirm);
        var result = Service().Evaluate(input);

        Assert.True(result.IsEligible);
        Assert.Equal(FixMode.Confirm, result.FixMode);
        Assert.Equal(FixMode.Confirm, input.Finding.FixMode);
        Assert.True(result.RequiresExplicitApproval);
    }

    [Fact]
    public void Open_confirm_without_fixer_is_ineligible_and_still_requires_approval()
    {
        var result = Service(preview: []).Evaluate(Input(FixMode.Confirm));

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FixerNotRegistered, result.ReasonCode);
        Assert.True(result.RequiresExplicitApproval);
    }

    [Fact]
    public void Confirm_is_not_promoted_for_an_auto_only_provider()
    {
        var result = Service(provider: new StubPreviewProvider(supportsConfirm: false))
            .Evaluate(Input(FixMode.Confirm));

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FindingContractUnsupported, result.ReasonCode);
        Assert.Equal(FixMode.Confirm, result.FixMode);
        Assert.True(result.RequiresExplicitApproval);
    }

    [Theory]
    [InlineData(FixMode.Manual, FixEligibilityReasonCode.ManualFixMode)]
    [InlineData(FixMode.Report, FixEligibilityReasonCode.ReportFixMode)]
    public void Manual_and_report_cannot_enter_automatic_fix_selection(
        FixMode mode, FixEligibilityReasonCode reason)
    {
        var provider = new StubPreviewProvider();
        var result = Service(provider: provider).Evaluate(Input(mode));

        Assert.False(result.IsEligible);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData(FixMode.Auto, FindingStatus.Fixed)]
    [InlineData(FixMode.Auto, FindingStatus.Ignored)]
    [InlineData(FixMode.Auto, FindingStatus.ManualReview)]
    [InlineData(FixMode.Confirm, FindingStatus.Fixed)]
    [InlineData(FixMode.Confirm, FindingStatus.Ignored)]
    [InlineData(FixMode.Confirm, FindingStatus.ManualReview)]
    public void Non_open_auto_and_confirm_are_ineligible(FixMode mode, FindingStatus status)
    {
        var result = Service().Evaluate(Input(mode) with
        {
            Finding = Finding(mode) with { FindingState = status }
        });

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FindingNotOpen, result.ReasonCode);
    }

    [Theory]
    [InlineData(FindingReviewState.PendingReview)]
    [InlineData(FindingReviewState.ManualRemediationApproved)]
    [InlineData(FindingReviewState.ManualRemediationReported)]
    [InlineData(FindingReviewState.Rejected)]
    [InlineData(FindingReviewState.Ignored)]
    [InlineData(FindingReviewState.AcceptedRisk)]
    public void Authoritative_review_states_block_fix_selection(FindingReviewState state)
    {
        var result = Service().Evaluate(Input() with { ReviewState = state });

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.ReviewStateBlocksFix, result.ReasonCode);
    }

    [Fact]
    public void Verified_still_detected_and_needs_revision_remain_open_for_eligibility()
    {
        var result = Service().Evaluate(Input() with
        {
            ResolutionState = FindingResolutionState.VerifiedStillDetected,
            ReviewState = FindingReviewState.NeedsRevision
        });

        Assert.True(result.IsEligible);
    }

    [Theory]
    [InlineData(FindingResolutionState.Applied)]
    [InlineData(FindingResolutionState.ReauditPending)]
    [InlineData(FindingResolutionState.VerifiedResolved)]
    public void Unverified_or_resolved_resolution_states_block_repeat_fix(FindingResolutionState state)
    {
        var result = Service().Evaluate(Input() with { ResolutionState = state });

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.ResolutionStateBlocksFix, result.ReasonCode);
    }

    [Fact]
    public void Missing_validation_key_is_ineligible()
    {
        var result = Service().Evaluate(Input() with
        {
            Finding = Finding() with { ValidationKey = "" }
        });

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.ValidationKeyUnsupported, result.ReasonCode);
    }

    [Fact]
    public void Unsupported_validation_key_has_no_registered_fixer()
    {
        var result = Service().Evaluate(Input() with
        {
            Finding = Finding() with { ValidationKey = "unknown.validation-key" }
        });

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FixerNotRegistered, result.ReasonCode);
    }

    [Fact]
    public void Unknown_provider_exception_maps_to_safe_deterministic_reason()
    {
        var result = Service(provider: new StubPreviewProvider(throws: true)).Evaluate(Input());

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.FindingContractUnsupported, result.ReasonCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("0.8")]
    [InlineData("1")]
    [InlineData("2")]
    public void Confidence_is_preserved_without_an_invented_threshold(string? value)
    {
        decimal? confidence = value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var result = Service().Evaluate(Input(confidence: confidence));

        Assert.True(result.IsEligible);
        Assert.Equal(confidence, result.Confidence);
    }

    [Fact]
    public void Evaluation_is_repeatable_and_does_not_mutate_finding_or_fix_plan()
    {
        var input = Input(FixMode.Confirm, 0.42m);
        var before = input.Finding with { };
        var audit = new AuditJob { Id = input.AuditId, DocumentVersionId = input.SourceDocumentVersionId };
        var plan = FixPlanRecord.Create(audit, Id(9), DateTimeOffset.UnixEpoch);
        var planUpdatedAt = plan.UpdatedAt;
        var service = Service();

        var first = service.Evaluate(input);
        var second = service.Evaluate(input);

        Assert.Equal(first, second);
        Assert.Equal(before, input.Finding);
        Assert.Empty(plan.Items);
        Assert.Equal(FixPlanLifecycleState.Draft, plan.State);
        Assert.Equal(planUpdatedAt, plan.UpdatedAt);
    }

    [Fact]
    public void Result_and_reason_are_bounded_and_exclude_document_or_internal_data()
    {
        const string sensitive = "thesis-content C:\\storage\\secret.docx https://signed.example token=secret SELECT * FROM findings";
        var input = Input() with
        {
            Finding = Finding() with { ActualJson = JsonSerializer.Serialize(new { value = sensitive }) }
        };
        var result = Service(provider: new StubPreviewProvider(accept: false)).Evaluate(input);
        var serialized = JsonSerializer.Serialize(result);

        Assert.Matches(new Regex("^[A-Za-z][A-Za-z0-9]{0,127}$", RegexOptions.CultureInvariant), result.ReasonCode.ToString());
        Assert.DoesNotContain(sensitive, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("storage", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluation_has_no_persistence_execution_or_document_dependencies()
    {
        var dependencyTypes = typeof(FixEligibilityService).GetConstructors().Single()
            .GetParameters().Select(value => value.ParameterType).ToArray();

        Assert.Equal([typeof(IRemediationCapabilityRegistry), typeof(FixApplyCapabilityRegistry)], dependencyTypes);
        Assert.DoesNotContain(dependencyTypes, value => value == typeof(IFixExecutionRepository));
        Assert.DoesNotContain(dependencyTypes, value => value == typeof(DocumentVersion));
        Assert.DoesNotContain(dependencyTypes, value => value == typeof(ParsedDocument));
    }

    [Fact]
    public void Apply_registry_availability_preserves_duplicate_and_version_semantics()
    {
        Assert.Throws<FixPlanConfigurationException>(() =>
            new FixApplyCapabilityRegistry([new StubApplyProvider(), new StubApplyProvider()]));
        var registry = new FixApplyCapabilityRegistry([new StubApplyProvider()]);

        Assert.Equal(FixApplyProviderAvailability.Available,
            registry.GetAvailability("test.validation", "capability", "1.0"));
        Assert.Equal(FixApplyProviderAvailability.VersionIncompatible,
            registry.GetAvailability("test.validation", "capability", "2.0"));
        Assert.Equal(FixApplyProviderAvailability.NotRegistered,
            registry.GetAvailability("test.validation", "unknown", "1.0"));
    }

    [Fact]
    public void Incomplete_audit_is_ineligible()
    {
        var result = Service().Evaluate(Input() with { AuditStatus = AuditJobStatus.Processing });

        Assert.False(result.IsEligible);
        Assert.Equal(FixEligibilityReasonCode.AuditNotCompleted, result.ReasonCode);
    }

    private static FixEligibilityService Service(
        IReadOnlyList<RemediationCapability>? preview = null,
        IReadOnlyList<IFixApplyProvider>? apply = null,
        StubPreviewProvider? provider = null)
    {
        provider ??= new StubPreviewProvider();
        preview ??= [Capability(provider)];
        apply ??= [new StubApplyProvider()];
        return new(new RemediationCapabilityRegistry(preview), new FixApplyCapabilityRegistry(apply));
    }

    private static RemediationCapability Capability(StubPreviewProvider provider) => new(
        "capability", "1.0", "test.validation", FixOperationKind.SetProperty,
        ["actual", "expected", "location"], false, true, "preview-provider", "description-code", false, provider);

    private static FixEligibilityInput Input(FixMode mode = FixMode.Auto, decimal? confidence = null) =>
        new(Id(1), AuditJobStatus.Completed, Id(2), Finding(mode), confidence);

    private static FixPlanFindingSnapshot Finding(FixMode mode = FixMode.Auto) => new(
        Id(3), 1, "RULE-1", "Layout", "Paragraph", "test.validation",
        RuleSeverity.Error, mode, FindingStatus.Open, "{}", "{}", "{}", 1);

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed class StubPreviewProvider(
        bool accept = true,
        bool throws = false,
        bool supportsConfirm = true) : IFixPreviewProvider
    {
        public int CallCount { get; private set; }

        public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
        {
            CallCount++;
            if (throws) throw new InvalidOperationException("C:\\storage\\secret.docx token=secret");
            operation = new(new("paragraph", 0, null, 0, null), "property", new("code", "value"), "precondition", "summary");
            diagnosticCode = accept ? "planned" : "provider-rejected";
            return accept && finding.ValidationKey == "test.validation" && finding.RuleCode == "RULE-1"
                && (finding.FixMode == FixMode.Auto || supportsConfirm && finding.FixMode == FixMode.Confirm)
                && finding.FindingState == FindingStatus.Open;
        }
    }

    private sealed class StubApplyProvider(
        string capabilityId = "capability",
        string capabilityVersion = "1.0") : IFixApplyProvider
    {
        public string CapabilityId => capabilityId;
        public string CapabilityVersion => capabilityVersion;
        public IReadOnlySet<string> ValidationKeys { get; } = new HashSet<string>(["test.validation"], StringComparer.Ordinal);
        public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(FixApplyOutcome.NoChange);
    }
}
