using System.Text.Json;
using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixPlanDraftPreviewServiceTests
{
    [Fact]
    public async Task Valid_auto_item_returns_safe_normalized_preview()
    {
        var context = Context();
        var result = await context.Service.PreviewAsync(context.AuditId, context.Plan.Id, context.OwnerId, default);

        Assert.NotNull(result);
        Assert.Equal(FixPlanDraftPreviewState.Ready, result.State);
        var item = Assert.Single(result.Items);
        Assert.Equal(FixPlanDraftPreviewItemState.Previewable, item.PreviewState);
        Assert.Equal("paragraph.alignment", item.PropertyIdentifier);
        Assert.Equal("Kiri", item.Change!.BeforeValue);
        Assert.Equal("Rata kiri-kanan", item.Change.AfterValue);
        Assert.Equal("Setelah", item.Change.AfterLabel);
        Assert.Equal("main-document-paragraph", item.Location!.Scope);
        Assert.Equal(0, context.ApplyProvider.ApplyCalls);
    }

    [Fact]
    public async Task Conflict_analysis_consumes_provider_mutation_and_updates_preview_state()
    {
        var analyzer = new CapturingConflictAnalyzer();
        var context = Context(conflictAnalyzer: analyzer);

        var result = await context.Service.PreviewAsync(context.AuditId, context.Plan.Id, context.OwnerId, default);

        Assert.Equal(1, analyzer.CallCount);
        var candidate = Assert.Single(analyzer.Candidates!);
        Assert.Equal("paragraph.alignment", candidate.Operation!.PropertyIdentifier);
        Assert.Equal("justified", candidate.Operation.Expected.Value);
        Assert.Equal(FixPlanDraftPreviewState.Conflict, result!.State);
        Assert.Equal(FixPlanMutationAnalysisState.Conflict, result.MutationAnalysis!.State);
    }

    [Fact]
    public async Task Eligible_confirm_item_remains_explicitly_unapproved()
    {
        var context = Context(FixMode.Confirm, new ConfirmPreviewProvider(), new AlwaysEligible());
        var result = await context.Service.PreviewAsync(context.AuditId, context.Plan.Id, context.OwnerId, default);

        var item = Assert.Single(result!.Items);
        Assert.Equal(FixPlanDraftPreviewItemState.Previewable, item.PreviewState);
        Assert.True(item.RequiresExplicitApproval);
        Assert.Equal(FixPlanLifecycleState.Draft, result.PlanState);
        Assert.Null(context.Plan.ApproverUserId);
        Assert.Null(context.Plan.ApprovedAt);
    }

    [Theory]
    [InlineData(FixMode.Manual, FixEligibilityReasonCode.ManualFixMode)]
    [InlineData(FixMode.Report, FixEligibilityReasonCode.ReportFixMode)]
    public async Task Non_executable_modes_never_gain_a_preview(
        FixMode mode, FixEligibilityReasonCode reason)
    {
        var context = Context(mode);
        var item = Assert.Single((await context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default))!.Items);

        Assert.Equal(FixPlanDraftPreviewItemState.Ineligible, item.PreviewState);
        Assert.Equal(reason, item.EligibilityReason);
        Assert.Null(item.Change);
        Assert.Null(item.PropertyIdentifier);
    }

    [Theory]
    [InlineData(FixEligibilityReasonCode.FindingNotOpen)]
    [InlineData(FixEligibilityReasonCode.ResolutionStateBlocksFix)]
    [InlineData(FixEligibilityReasonCode.ReviewStateBlocksFix)]
    [InlineData(FixEligibilityReasonCode.AuditNotCompleted)]
    public async Task Current_eligibility_is_revalidated_and_never_silently_omitted(
        FixEligibilityReasonCode reason)
    {
        var eligibility = new AlwaysEligible(reason);
        var context = Context(eligibility: eligibility);
        var result = await context.Service.PreviewAsync(context.AuditId, context.Plan.Id, context.OwnerId, default);

        var item = Assert.Single(result!.Items);
        Assert.Equal(1, eligibility.CallCount);
        Assert.Equal(FixPlanDraftPreviewItemState.Ineligible, item.PreviewState);
        Assert.Equal(reason, item.EligibilityReason);
        Assert.Equal(0, result.PreviewableCount);
        Assert.Equal(1, result.IneligibleCount);
    }

    [Fact]
    public async Task Missing_preview_capability_fails_closed()
    {
        var context = Context(previewRegistry: RemediationCapabilityRegistry.Empty(),
            eligibility: new AlwaysEligible());
        var item = Assert.Single((await context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default))!.Items);

        Assert.Equal(FixPlanDraftPreviewItemState.Unavailable, item.PreviewState);
        Assert.Equal("fix-preview-provider-not-registered", item.ReasonCode);
        Assert.Null(item.Change);
    }

    [Fact]
    public async Task Missing_apply_capability_fails_closed()
    {
        var context = Context(applyRegistry: new FixApplyCapabilityRegistry([]),
            eligibility: new AlwaysEligible());
        var item = Assert.Single((await context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default))!.Items);

        Assert.Equal("fix-apply-provider-not-registered", item.ReasonCode);
        Assert.Null(item.Change);
    }

    [Fact]
    public async Task Wrong_apply_provider_version_fails_closed()
    {
        var context = Context(applyRegistry: new FixApplyCapabilityRegistry(
            [new NoopApplyProvider(BodyJustifiedFixProvider.Id, "2.0")]),
            eligibility: new AlwaysEligible());
        var item = Assert.Single((await context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default))!.Items);

        Assert.Equal("fix-apply-provider-version-incompatible", item.ReasonCode);
        Assert.Equal(BodyJustifiedFixProvider.Version, item.CapabilityVersion);
        Assert.Null(item.Change);
    }

    [Fact]
    public async Task Provider_exception_becomes_bounded_result_without_message_leak()
    {
        var provider = new ThrowingPreviewProvider();
        var registry = Registry(provider, provider.CapabilityId, provider.CapabilityVersion);
        var context = Context(previewProvider: provider, previewRegistry: registry,
            applyRegistry: new FixApplyCapabilityRegistry(
                [new NoopApplyProvider(provider.CapabilityId, provider.CapabilityVersion)]),
            eligibility: new AlwaysEligible());

        var item = Assert.Single((await context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default))!.Items);
        var json = JsonSerializer.Serialize(item);
        Assert.Equal("fix-preview-provider-failed", item.ReasonCode);
        Assert.DoesNotContain("private thesis content", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public async Task Wrong_route_or_owner_is_not_disclosed(int wrongAudit, int wrongPlan, int wrongOwner)
    {
        var context = Context();
        var result = await context.Service.PreviewAsync(
            wrongAudit == 1 ? Id(91) : context.AuditId,
            wrongPlan == 1 ? Id(92) : context.Plan.Id,
            wrongOwner == 1 ? Id(93) : context.OwnerId, default);

        Assert.Null(result);
    }

    [Fact]
    public async Task Non_draft_plan_is_rejected_without_approval_side_effect()
    {
        var context = Context();
        context.Plan.Approve(Id(70), Now.AddMinutes(1));

        var error = await Assert.ThrowsAsync<FixPlanDraftPreviewException>(() => context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default));
        Assert.Equal("fix-plan-preview-not-draft", error.DiagnosticCode);
        Assert.Equal(Id(70), context.Plan.ApproverUserId);
    }

    [Theory]
    [InlineData("fix-plan-source-version-superseded")]
    [InlineData("fix-plan-source-version-unavailable")]
    public async Task Stale_source_is_rejected_with_existing_bounded_code(string staleCode)
    {
        var context = Context(staleCode: staleCode);
        var error = await Assert.ThrowsAsync<FixPlanDraftPreviewException>(() => context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default));
        Assert.Equal(staleCode, error.DiagnosticCode);
    }

    [Fact]
    public async Task Source_version_lineage_mismatch_is_rejected()
    {
        var context = Context();
        context.Source.Audit.DocumentVersionId = Id(88);

        var error = await Assert.ThrowsAsync<FixPlanDraftPreviewException>(() => context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default));
        Assert.Equal("fix-plan-preview-source-lineage-invalid", error.DiagnosticCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Invalid_source_hash_is_rejected(string sha)
    {
        var context = Context();
        context.Source.Audit.DocumentVersion!.Sha256 = sha;
        var error = await Assert.ThrowsAsync<FixPlanDraftPreviewException>(() => context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default));
        Assert.Equal("fix-plan-preview-source-snapshot-invalid", error.DiagnosticCode);
    }

    [Fact]
    public async Task Repeated_preview_is_deterministic_and_preserves_all_authoritative_entities()
    {
        var context = Context();
        var before = EntitySnapshot(context);
        var first = await context.Service.PreviewAsync(context.AuditId, context.Plan.Id, context.OwnerId, default);
        var middle = EntitySnapshot(context);
        var second = await context.Service.PreviewAsync(context.AuditId, context.Plan.Id, context.OwnerId, default);
        var after = EntitySnapshot(context);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(before, middle);
        Assert.Equal(before, after);
        Assert.Equal(2, context.Repository.LoadCount);
        Assert.Equal(0, context.Repository.WriteCount);
        Assert.Equal(0, context.ApplyProvider.ApplyCalls);
    }

    [Fact]
    public async Task Oversized_evidence_is_not_returned_by_before_after_contract()
    {
        var context = Context();
        var finding = context.Source.Findings.Single();
        finding.Finding.ActualValueJson = JsonSerializer.Serialize(new
            { property = "alignment", normalizedValue = new string('x', 2_000) });
        context.Repository.Aggregate = context.Repository.Aggregate with
        {
            Source = context.Source with
            {
                Findings = [finding with { Snapshot = finding.Snapshot with { ActualJson = finding.Finding.ActualValueJson } }]
            }
        };

        var result = await context.Service.PreviewAsync(context.AuditId, context.Plan.Id, context.OwnerId, default);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(new string('x', 81), serialized, StringComparison.Ordinal);
        Assert.True(serialized.Length < 4_096);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public async Task Invalid_resolved_rule_snapshot_is_rejected(string hash)
    {
        var context = Context();
        context.Source.Audit.ResolvedRuleSetHash = hash;
        var error = await Assert.ThrowsAsync<FixPlanDraftPreviewException>(() => context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default));
        Assert.Equal("fix-plan-preview-source-snapshot-invalid", error.DiagnosticCode);
    }

    [Fact]
    public async Task Missing_document_kind_snapshot_is_rejected()
    {
        var context = Context();
        context.Source.Audit.DocumentKindSnapshot = null;
        var error = await Assert.ThrowsAsync<FixPlanDraftPreviewException>(() => context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default));
        Assert.Equal("fix-plan-preview-source-snapshot-invalid", error.DiagnosticCode);
    }

    [Fact]
    public async Task Eligibility_failure_short_circuits_provider_resolution()
    {
        var provider = new CountingPreviewProvider();
        var context = Context(previewProvider: provider,
            eligibility: new AlwaysEligible(FixEligibilityReasonCode.FindingNotOpen));

        var item = Assert.Single((await context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default))!.Items);
        Assert.Equal(FixPlanDraftPreviewItemState.Ineligible, item.PreviewState);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Persisted_plan_membership_mismatch_is_rejected()
    {
        var context = Context();
        context.Repository.Aggregate = context.Repository.Aggregate with
            { Source = context.Source with { Findings = [] } };

        var error = await Assert.ThrowsAsync<FixPlanDraftPreviewException>(() => context.Service.PreviewAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, default));
        Assert.Equal("fix-plan-preview-membership-invalid", error.DiagnosticCode);
    }

    [Fact]
    public void Response_contract_excludes_raw_snapshots_provider_payloads_and_document_content()
    {
        var properties = typeof(FixPlanDraftPreviewDto).GetProperties()
            .Concat(typeof(FixPlanDraftPreviewItemDto).GetProperties())
            .Concat(typeof(FixPlanDraftBeforeAfterDto).GetProperties())
            .Select(value => value.Name).ToArray();

        foreach (var forbidden in new[] { "ActualJson", "ExpectedJson", "LocationJson", "OpenXml", "Storage",
                     "Path", "Url", "ProviderPayload", "DocumentText", "Message", "Exception", "Stack" })
            Assert.DoesNotContain(properties, value => value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Api_route_is_authenticated_read_only_and_does_not_introduce_approval_or_execution()
    {
        var api = File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "services", "Ppki.Api", "Program.cs"));
        const string route = "MapGet(\"/audits/{id:guid}/fix-plans/{planId:guid}/preview\"";
        var start = api.IndexOf(route, StringComparison.Ordinal);
        var end = api.IndexOf("api.MapPut", start, StringComparison.Ordinal);
        var endpoint = api[start..end];

        Assert.True(start >= 0);
        Assert.Contains("MapGroup(\"/api\").RequireAuthorization()", api, StringComparison.Ordinal);
        Assert.Contains("UserId(user)", endpoint, StringComparison.Ordinal);
        Assert.Contains("IFixPlanDraftPreviewService", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve(", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Enqueue", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentVersion", endpoint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("section.page-size-a4")]
    [InlineData("section.margin-left-4cm")]
    [InlineData("section.margin-right-3cm")]
    [InlineData("section.margin-top-3cm")]
    [InlineData("section.margin-bottom-3cm")]
    public void Section_layout_targets_have_exact_preview_and_apply_capabilities(string validationKey)
    {
        Assert.True(ProductionFixCapabilities.CreatePreviewRegistry().TryGet(validationKey, out var capability));
        Assert.Contains(ProductionFixCapabilities.CreateApplyRegistry().Providers,
            provider => provider.CapabilityId == capability.CapabilityId
                && provider.CapabilityVersion == capability.CapabilityVersion
                && provider.ValidationKeys.Contains(validationKey));
    }

    [Theory]
    [InlineData("body.font-times-new-roman-12", "PPKI-LAY-005", "font.ascii", "Arial", "Times New Roman", true)]
    [InlineData("body.font-times-new-roman-12", "PPKI-LAY-005", "fontSize", "22", "24", true)]
    [InlineData("body.justified", "PPKI-LAY-019", "alignment", "Left", "Justified", false)]
    [InlineData("heading.chapter-centered", "PPKI-HDG-006", "alignment", "Left", "Center", false)]
    [InlineData("body.line-spacing-single", "PPKI-LAY-017", "lineSpacingValue", "360", "240", false)]
    [InlineData("body.line-spacing-single", "PPKI-LAY-017", "lineSpacingRule", "exact", "auto", false)]
    [InlineData("abstract.skripsi-single-spacing-zero-paragraph-spacing", "PPKI-ABS-011", "spacingBeforeTwips", "120", "0", false)]
    [InlineData("body.first-line-indent-1cm", "PPKI-LAY-018", "firstLineIndent", "0", "567", false)]
    public void Registered_fix_targets_produce_normalized_bounded_before_after(
        string validationKey, string ruleCode, string property, string before, string after, bool runTarget)
    {
        var capability = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities
            .Single(value => value.ValidationKey == validationKey);
        var location = runTarget
            ? "{\"compactLocation\":\"maindocument/s:0/b:0/p:0/r:0/kind:run\",\"sectionIndex\":0,\"bodyElementIndex\":0,\"paragraphIndex\":0,\"runIndex\":0}"
            : "{\"compactLocation\":\"maindocument/p0\",\"bodyElementIndex\":0,\"paragraphIndex\":0}";
        var finding = new FixPlanFindingSnapshot(Id(80), 1, ruleCode, "layout", "body", validationKey,
            RuleSeverity.Error, FixMode.Auto, FindingStatus.Open,
            JsonSerializer.Serialize(new { property, normalizedValue = before }),
            JsonSerializer.Serialize(new { property, validationKey, acceptedValues = new[] { after } }),
            location, 1);

        Assert.True(capability.Provider.TryCreate(finding, out var operation, out _));
        Assert.True(capability.Provider.TryCreateBeforeAfter(finding, operation, out var preview));
        Assert.NotNull(preview.AfterValue);
        Assert.True(preview.BeforeValue is null || preview.BeforeValue.Length <= 80);
        Assert.True(preview.AfterValue.Length <= 80);
        Assert.Equal(preview.BeforeValue is null ? "Partial" : "Complete", preview.EvidenceState);
    }

    private static string EntitySnapshot(TestContext context) => JsonSerializer.Serialize(new
    {
        context.Plan.State, context.Plan.UpdatedAt, context.Plan.ApproverUserId, context.Plan.ApprovedAt,
        context.Plan.IdempotencyKey, context.Plan.RequestHash,
        Items = context.Plan.Items.Select(value => new { value.Id, value.FindingId, value.CreatedAt }),
        FindingState = context.Source.Findings.Single().Finding.Status,
        context.Source.Audit.DocumentVersion!.Document!.CurrentVersionNo,
        VersionCount = context.Source.Audit.DocumentVersion.Document.Versions.Count
    });

    private static TestContext Context(
        FixMode mode = FixMode.Auto,
        IFixPreviewProvider? previewProvider = null,
        IFixEligibilityService? eligibility = null,
        RemediationCapabilityRegistry? previewRegistry = null,
        FixApplyCapabilityRegistry? applyRegistry = null,
        IFixPlanConflictAnalyzer? conflictAnalyzer = null,
        string? staleCode = null)
    {
        var ownerId = Id(10);
        var document = new DocumentRecord
        {
            Id = Id(20), OwnerUserId = ownerId, DocumentTypeId = Id(21), Title = "safe-title",
            Status = DocumentStatus.Active, CurrentVersionNo = 1
        };
        var version = new DocumentVersion
        {
            Id = Id(22), DocumentId = document.Id, Document = document, VersionNo = 1,
            StorageBucket = "private", StorageKey = "not-returned", OriginalFilename = "source.docx",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            SizeBytes = 1, Sha256 = new string('a', 64), CreatedByUserId = ownerId
        };
        document.Versions.Add(version);
        var audit = new AuditJob
        {
            Id = Id(30), DocumentVersionId = version.Id, DocumentVersion = version,
            ProfileVersionId = Id(31), DocumentKindSnapshot = DocumentKind.Skripsi,
            ResolvedRuleSetHash = new string('b', 64), Status = AuditJobStatus.Completed
        };
        var finding = new AuditFinding
        {
            Id = Id(40), AuditJobId = audit.Id, AuditJob = audit, RuleId = Id(41),
            RuleCodeSnapshot = "PPKI-LAY-019", FixModeSnapshot = mode, Status = FindingStatus.Open,
            Message = "must-never-be-returned", Confidence = 0.75m,
            ActualValueJson = "{\"property\":\"alignment\",\"normalizedValue\":\"Left\"}",
            ExpectedValueJson = "{\"property\":\"alignment\",\"validationKey\":\"body.justified\",\"acceptedValues\":[\"Justified\"]}",
            LocationJson = "{\"compactLocation\":\"maindocument/p0\",\"bodyElementIndex\":0,\"paragraphIndex\":0}"
        };
        var snapshot = new FixPlanFindingSnapshot(finding.Id, 19, finding.RuleCodeSnapshot, "layout", "body",
            "body.justified", RuleSeverity.Error, mode, finding.Status, finding.ActualValueJson,
            finding.ExpectedValueJson, finding.LocationJson, 1);
        var source = new FixPlanDraftSource(audit, version.Id, staleCode,
            [new(finding, snapshot, FindingResolutionState.Open, FindingReviewState.NoReview)]);
        var plan = FixPlanRecord.Create(audit, ownerId, Id(50), new string('c', 64), Now);
        plan.ReplaceItems([finding], Now);
        var repository = new PreviewRepository(new(plan, source));

        previewProvider ??= new BodyJustifiedFixProvider();
        previewRegistry ??= previewProvider is BodyJustifiedFixProvider
            ? ProductionFixCapabilities.CreatePreviewRegistry()
            : Registry(previewProvider,
                previewProvider switch
                {
                    ConfirmPreviewProvider => ConfirmPreviewProvider.Id,
                    ThrowingPreviewProvider value => value.CapabilityId,
                    CountingPreviewProvider => CountingPreviewProvider.Id,
                    _ => throw new InvalidOperationException("Unknown test provider.")
                },
                previewProvider switch
                {
                    ConfirmPreviewProvider => ConfirmPreviewProvider.Version,
                    ThrowingPreviewProvider value => value.CapabilityVersion,
                    CountingPreviewProvider => CountingPreviewProvider.Version,
                    _ => throw new InvalidOperationException("Unknown test provider.")
                });
        var applyProvider = new NoopApplyProvider(
            previewRegistry.Capabilities.SingleOrDefault(value => value.ValidationKey == "body.justified")?.CapabilityId
                ?? BodyJustifiedFixProvider.Id,
            previewRegistry.Capabilities.SingleOrDefault(value => value.ValidationKey == "body.justified")?.CapabilityVersion
                ?? BodyJustifiedFixProvider.Version);
        applyRegistry ??= new([applyProvider]);
        eligibility ??= new FixEligibilityService(previewRegistry, applyRegistry);
        return new(audit.Id, ownerId, plan, source, repository, applyProvider,
            new(repository, eligibility, previewRegistry, applyRegistry, conflictAnalyzer));
    }

    private static RemediationCapabilityRegistry Registry(IFixPreviewProvider provider, string id, string version) => new([
        new(id, version, "body.justified", FixOperationKind.SetProperty, ["actual", "expected", "location"],
            false, true, "test-preview", "test-change", true, provider)
    ]);

    private static readonly DateTimeOffset Now = new(2026, 8, 25, 4, 5, 6, TimeSpan.Zero);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed record TestContext(Guid AuditId, Guid OwnerId, FixPlanRecord Plan,
        FixPlanDraftSource Source, PreviewRepository Repository, NoopApplyProvider ApplyProvider,
        FixPlanDraftPreviewService Service);

    private sealed class PreviewRepository(FixPlanDraftAggregate aggregate) : IFixPlanDraftRepository
    {
        public FixPlanDraftAggregate Aggregate { get; set; } = aggregate;
        public int LoadCount { get; private set; }
        public int WriteCount { get; private set; }
        public Task<FixPlanDraftAggregate?> LoadOwnedAsync(Guid auditId, Guid planId, Guid ownerUserId,
            CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult<FixPlanDraftAggregate?>(Aggregate.Plan.Id == planId
                && Aggregate.Plan.SourceAuditJobId == auditId && Aggregate.Plan.OwnerUserId == ownerUserId
                ? Aggregate : null);
        }
        public Task<FixPlanDraftSource?> LoadSourceAsync(Guid auditId, FixPlanSelection selection,
            CancellationToken cancellationToken) => throw new InvalidOperationException("preview must use persisted membership");
        public Task<FixPlanDraftWriteResult> CreateAsync(FixPlanDraftSource source, Guid ownerUserId,
            Guid idempotencyKey, string requestHash, DateTimeOffset now, CancellationToken cancellationToken)
        { WriteCount++; throw new InvalidOperationException("preview is read-only"); }
        public Task<FixPlanDraftWriteResult> ReplaceAsync(Guid auditId, Guid planId, Guid ownerUserId,
            FixPlanDraftSource source, DateTimeOffset now, CancellationToken cancellationToken)
        { WriteCount++; throw new InvalidOperationException("preview is read-only"); }
        public Task<string?> DeleteAsync(Guid auditId, Guid planId, Guid ownerUserId,
            CancellationToken cancellationToken)
        { WriteCount++; throw new InvalidOperationException("preview is read-only"); }
    }

    private sealed class AlwaysEligible(FixEligibilityReasonCode reason = FixEligibilityReasonCode.Eligible)
        : IFixEligibilityService
    {
        public int CallCount { get; private set; }
        public FixEligibilityResult Evaluate(FixEligibilityInput input)
        {
            CallCount++;
            return new(input.Finding.FindingId, input.Finding.FixMode, input.Confidence,
                reason == FixEligibilityReasonCode.Eligible ? FixEligibilityStatus.Eligible : FixEligibilityStatus.Ineligible,
                reason, input.Finding.FixMode == FixMode.Confirm);
        }
    }

    private sealed class ConfirmPreviewProvider : IFixPreviewProvider
    {
        public const string Id = "confirm-preview-test";
        public const string Version = "1.0";
        public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
        {
            operation = new(new("main-document-paragraph", 0, null, 0, null), "paragraph.alignment",
                new("enum-code", "justified"), "source-finding-snapshot-must-match", "set-paragraph-alignment-justified");
            diagnosticCode = "fix-operation-planned";
            return finding.FixMode == FixMode.Confirm;
        }
    }

    private sealed class ThrowingPreviewProvider : IFixPreviewProvider
    {
        public string CapabilityId => "throwing-preview-test";
        public string CapabilityVersion => "1.0";
        public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode) =>
            throw new InvalidOperationException("private thesis content and stack details");
    }

    private sealed class CountingPreviewProvider : IFixPreviewProvider
    {
        public const string Id = "counting-preview-test";
        public const string Version = "1.0";
        public int CallCount { get; private set; }
        public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
        {
            CallCount++;
            operation = new(new("main-document-paragraph", 0, null, 0, null), "paragraph.alignment",
                new("enum-code", "justified"), "source-finding-snapshot-must-match", "set-paragraph-alignment-justified");
            diagnosticCode = "fix-operation-planned";
            return true;
        }
    }

    private sealed class NoopApplyProvider(string id, string version) : IFixApplyProvider
    {
        public int ApplyCalls { get; private set; }
        public string CapabilityId => id;
        public string CapabilityVersion => version;
        public IReadOnlySet<string> ValidationKeys { get; } = new HashSet<string>(["body.justified"], StringComparer.Ordinal);
        public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken)
        { ApplyCalls++; throw new InvalidOperationException("preview invoked apply"); }
    }

    private sealed class CapturingConflictAnalyzer : IFixPlanConflictAnalyzer
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<FixPlanMutationCandidate>? Candidates { get; private set; }
        public FixPlanMutationAnalysisDto Analyze(Guid sourceDocumentVersionId,
            IReadOnlyList<FixPlanMutationCandidate> candidates)
        {
            CallCount++;
            Candidates = candidates;
            var candidate = candidates.Single();
            return new(DeterministicFixPlanConflictAnalyzer.SchemaVersion,
                FixPlanMutationAnalysisState.Conflict, 0, 0, 0, 0, 1, 0,
                [new(candidate.ItemId, candidate.FindingId, FixPlanMutationItemStatus.Conflicting,
                    "fix-mutation-contradictory-outcome", null, null, [])], [],
                [new(null, [candidate.ItemId], "fix-mutation-contradictory-outcome")],
                ["fix-mutation-contradictory-outcome"]);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
