using System.Text.Json;
using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixPlanApprovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 6, 7, 8, TimeSpan.Zero);

    [Fact]
    public async Task Auto_only_plan_can_be_explicitly_approved_without_confirm_ids()
    {
        var context = Context(FixMode.Auto);
        var result = await context.Service.ApproveAsync(context.AuditId, context.Plan.Id,
            context.OwnerId, [], default);

        Assert.Equal(FixPlanLifecycleState.Approved, result!.State);
        Assert.False(result.Replayed);
        Assert.Equal(context.OwnerId, result.ApprovedByUserId);
        Assert.Equal(Now, result.ApprovedAt);
        Assert.Equal(64, result.PlanHash.Length);
        Assert.Equal(1, result.ItemCount);
    }

    [Fact]
    public async Task Confirm_item_requires_exact_per_item_consent()
    {
        var context = Context(FixMode.Confirm);
        var error = await Assert.ThrowsAsync<FixPlanApprovalException>(() => context.Service.ApproveAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, [], default));
        Assert.Equal("fix-plan-confirm-approval-required", error.DiagnosticCode);
        Assert.Equal(FixPlanLifecycleState.Draft, context.Plan.State);
    }

    [Fact]
    public async Task Confirm_item_is_approved_when_its_plan_item_id_is_supplied()
    {
        var context = Context(FixMode.Confirm);
        var itemId = Assert.Single(context.Plan.Items).Id;
        var result = await context.Service.ApproveAsync(context.AuditId, context.Plan.Id,
            context.OwnerId, [itemId], default);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Extra_or_duplicate_confirm_consent_is_rejected()
    {
        var extra = Context(FixMode.Confirm);
        var error = await Assert.ThrowsAsync<FixPlanApprovalException>(() => extra.Service.ApproveAsync(
            extra.AuditId, extra.Plan.Id, extra.OwnerId, [Guid.NewGuid()], default));
        Assert.Equal("fix-plan-confirm-approval-required", error.DiagnosticCode);

        var duplicate = Context(FixMode.Confirm);
        var id = Assert.Single(duplicate.Plan.Items).Id;
        error = await Assert.ThrowsAsync<FixPlanApprovalException>(() => duplicate.Service.ApproveAsync(
            duplicate.AuditId, duplicate.Plan.Id, duplicate.OwnerId, [id, id], default));
        Assert.Equal("fix-plan-confirm-approval-invalid", error.DiagnosticCode);
    }

    [Theory]
    [InlineData(FixMode.Manual)]
    [InlineData(FixMode.Report)]
    public async Task Manual_and_report_items_fail_closed(FixMode mode)
    {
        var context = Context(mode);
        var error = await Assert.ThrowsAsync<FixPlanApprovalException>(() => context.Service.ApproveAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, [], default));
        Assert.Equal("fix-plan-approval-preview-not-ready", error.DiagnosticCode);
        Assert.Equal(FixPlanLifecycleState.Draft, context.Plan.State);
    }

    [Theory]
    [InlineData(FixPlanDraftPreviewState.PartiallyAvailable)]
    [InlineData(FixPlanDraftPreviewState.Conflict)]
    [InlineData(FixPlanDraftPreviewState.Stale)]
    [InlineData(FixPlanDraftPreviewState.Unavailable)]
    public async Task Any_non_ready_preview_blocks_approval(FixPlanDraftPreviewState state)
    {
        var context = Context(FixMode.Auto, previewState: state);
        var error = await Assert.ThrowsAsync<FixPlanApprovalException>(() => context.Service.ApproveAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, [], default));
        Assert.Equal("fix-plan-approval-preview-not-ready", error.DiagnosticCode);
    }

    [Theory]
    [InlineData(FixPlanMutationAnalysisState.Conflict, FixPlanMutationItemStatus.Conflicting)]
    [InlineData(FixPlanMutationAnalysisState.Conflict, FixPlanMutationItemStatus.DependencyCycle)]
    [InlineData(FixPlanMutationAnalysisState.Stale, FixPlanMutationItemStatus.Stale)]
    [InlineData(FixPlanMutationAnalysisState.PartiallyAvailable, FixPlanMutationItemStatus.Unavailable)]
    public async Task Conflict_cycle_stale_and_unavailable_analysis_block_approval(
        FixPlanMutationAnalysisState state, FixPlanMutationItemStatus status)
    {
        var context = Context(FixMode.Auto, analysisState: state, analysisStatus: status);
        var error = await Assert.ThrowsAsync<FixPlanApprovalException>(() => context.Service.ApproveAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, [], default));
        Assert.Equal("fix-plan-approval-mutation-analysis-not-ready", error.DiagnosticCode);
    }

    [Fact]
    public void Plan_content_hash_is_deterministic_and_excludes_actor_and_timestamp()
    {
        var context = Context(FixMode.Confirm);
        var evaluation = context.Builder.Build(context.Aggregate, context.AuditId);
        var id = Assert.Single(context.Plan.Items).Id;
        var first = FixPlanApprovalSnapshotSerializer.Create(context.Aggregate, evaluation,
            new HashSet<Guid> { id }, context.OwnerId, Now);
        var second = FixPlanApprovalSnapshotSerializer.Create(context.Aggregate, evaluation,
            new HashSet<Guid> { id }, Guid.NewGuid(), Now.AddDays(5));

        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(first.ApprovalRequestHash, second.ApprovalRequestHash);
        Assert.NotEqual(first.SnapshotJson, second.SnapshotJson);
    }

    [Fact]
    public void Immutable_snapshot_contains_execution_and_audit_facts_without_raw_provider_payload()
    {
        var context = Context(FixMode.Auto);
        var prepared = FixPlanApprovalSnapshotSerializer.Create(context.Aggregate,
            context.Builder.Build(context.Aggregate, context.AuditId), new HashSet<Guid>(), context.OwnerId, Now);
        using var json = JsonDocument.Parse(prepared.SnapshotJson);
        var root = json.RootElement;
        var item = root.GetProperty("items")[0];

        Assert.Equal(context.AuditId, root.GetProperty("auditId").GetGuid());
        Assert.Equal(new string('a', 64), root.GetProperty("sourceVersionSha256").GetString());
        Assert.Equal("BODY-JUSTIFIED", item.GetProperty("validationKey").GetString());
        Assert.Equal("paragraph.alignment", item.GetProperty("operation").GetProperty("propertyIdentifier").GetString());
        Assert.Equal(context.OwnerId, root.GetProperty("approvedByUserId").GetGuid());
        Assert.DoesNotContain("providerPayload", prepared.SnapshotJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("planId")]
    [InlineData("auditId")]
    [InlineData("sourceDocumentVersionId")]
    [InlineData("sourceVersionSha256")]
    [InlineData("resolvedRuleSetHash")]
    [InlineData("documentKindSnapshot")]
    [InlineData("previewSchemaVersion")]
    [InlineData("mutationAnalysisSchemaVersion")]
    [InlineData("planHash")]
    [InlineData("items")]
    [InlineData("mutationAnalysis")]
    [InlineData("approvedByUserId")]
    [InlineData("approvedAt")]
    [InlineData("itemId")]
    [InlineData("findingId")]
    [InlineData("ruleOrdinal")]
    [InlineData("ruleCode")]
    [InlineData("validationKey")]
    [InlineData("severity")]
    [InlineData("fixMode")]
    [InlineData("findingState")]
    [InlineData("confidence")]
    [InlineData("sourceSectionSnapshot")]
    [InlineData("pdfPageSnapshot")]
    [InlineData("printedPageSnapshot")]
    [InlineData("sourceReferenceJson")]
    [InlineData("actualValueJson")]
    [InlineData("expectedValueJson")]
    [InlineData("locationJson")]
    [InlineData("ruleSnapshotSchemaVersion")]
    [InlineData("eligibility")]
    [InlineData("eligibilityReason")]
    [InlineData("requiresExplicitApproval")]
    [InlineData("previewState")]
    [InlineData("previewReasonCode")]
    [InlineData("explicitlyApproved")]
    [InlineData("capabilityId")]
    [InlineData("capabilityVersion")]
    [InlineData("operation")]
    [InlineData("preview")]
    public void Approved_snapshot_schema_carries_every_required_authoritative_fact(string property)
    {
        var context = Context(FixMode.Auto);
        var prepared = FixPlanApprovalSnapshotSerializer.Create(context.Aggregate,
            context.Builder.Build(context.Aggregate, context.AuditId), new HashSet<Guid>(), context.OwnerId, Now);
        Assert.Contains($"\"{property}\":", prepared.SnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identical_retry_returns_the_existing_snapshot()
    {
        var context = Context(FixMode.Auto, replay: true);
        var result = await context.Service.ApproveAsync(context.AuditId, context.Plan.Id,
            context.OwnerId, [], default);
        Assert.True(result!.Replayed);
        Assert.Equal(0, context.Builder.CallCount);
    }

    [Fact]
    public async Task Repository_conflict_is_returned_as_a_stable_diagnostic()
    {
        var context = Context(FixMode.Auto, conflict: "fix-plan-approval-concurrency-conflict");
        var error = await Assert.ThrowsAsync<FixPlanApprovalException>(() => context.Service.ApproveAsync(
            context.AuditId, context.Plan.Id, context.OwnerId, [], default));
        Assert.Equal("fix-plan-approval-concurrency-conflict", error.DiagnosticCode);
    }

    [Fact]
    public void Api_and_schema_require_owner_scoped_immutable_approval_without_apply_side_effects()
    {
        var root = RepositoryRoot();
        var api = File.ReadAllText(Path.Combine(root, "backend", "services", "Ppki.Api", "Program.cs"));
        var service = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.FixEngine", "FixPlanApprovalService.cs"));
        var migration = File.ReadAllText(Path.Combine(root, "supabase", "migrations",
            "202608250003_fix_plan_approval_snapshots.sql"));

        Assert.Contains("/approval", api, StringComparison.Ordinal);
        Assert.Contains("UserId(user)", api, StringComparison.Ordinal);
        Assert.DoesNotContain("IFixExecution", service, StringComparison.Ordinal);
        Assert.Contains("Approved fix plan snapshots are append-only", migration, StringComparison.Ordinal);
        Assert.Contains("enable row level security", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth.uid()", migration, StringComparison.Ordinal);
    }

    private static TestContext Context(FixMode mode, FixPlanDraftPreviewState previewState = FixPlanDraftPreviewState.Ready,
        FixPlanMutationAnalysisState analysisState = FixPlanMutationAnalysisState.Ready,
        FixPlanMutationItemStatus analysisStatus = FixPlanMutationItemStatus.Independent,
        bool replay = false, string? conflict = null)
    {
        var owner = Id(90); var auditId = Id(10); var versionId = Id(11);
        var document = new DocumentRecord { Id = Id(12), OwnerUserId = owner, DocumentTypeId = Id(13),
            Title = "safe", CurrentVersionNo = 1, Status = DocumentStatus.Active };
        var version = new DocumentVersion { Id = versionId, DocumentId = document.Id, Document = document,
            VersionNo = 1, StorageBucket = "originals", StorageKey = "safe", OriginalFilename = "safe.docx",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", SizeBytes = 1,
            Sha256 = new string('a', 64), CreatedByUserId = owner };
        var audit = new AuditJob { Id = auditId, DocumentVersionId = versionId, DocumentVersion = version,
            ProfileVersionId = Id(14), RequestedByUserId = owner, Status = AuditJobStatus.Completed,
            ResolvedRuleSetHash = new string('b', 64), DocumentKindSnapshot = DocumentKind.Skripsi };
        var finding = new AuditFinding { Id = Id(20), AuditJobId = auditId, AuditJob = audit, RuleId = Id(21),
            Severity = RuleSeverity.Error, RuleCodeSnapshot = "BODY-JUSTIFIED", FixModeSnapshot = mode,
            Message = "safe", ActualValueJson = "{\"value\":\"left\"}",
            ExpectedValueJson = "{\"value\":\"justified\"}",
            LocationJson = "{\"bodyElementIndex\":0,\"paragraphIndex\":0}", Status = FindingStatus.Open };
        var plan = FixPlanRecord.Create(audit, owner, Id(30), new string('c', 64), Now.AddMinutes(-1));
        plan.AddItem(finding, Now.AddMinutes(-1));
        var snapshot = new FixPlanFindingSnapshot(finding.Id, 1, finding.RuleCodeSnapshot, "body", "paragraph",
            "BODY-JUSTIFIED", RuleSeverity.Error, mode, finding.Status, finding.ActualValueJson,
            finding.ExpectedValueJson, finding.LocationJson, 1);
        var source = new FixPlanDraftSource(audit, versionId, null,
            [new(finding, snapshot, FindingResolutionState.Open, FindingReviewState.NoReview)]);
        var aggregate = new FixPlanDraftAggregate(plan, source);
        var builder = new StubBuilder(previewState, analysisState, analysisStatus);
        var repository = new StubRepository(aggregate, replay, conflict);
        return new(auditId, owner, plan, aggregate, builder,
            new FixPlanApprovalService(repository, builder, new FixedTimeProvider(Now)));
    }

    private sealed class StubBuilder(FixPlanDraftPreviewState previewState,
        FixPlanMutationAnalysisState analysisState, FixPlanMutationItemStatus analysisStatus)
        : IFixPlanApprovalPreviewBuilder
    {
        public int CallCount { get; private set; }
        public FixPlanApprovalEvaluation Build(FixPlanDraftAggregate aggregate, Guid auditId)
        {
            CallCount++;
            var planItem = Assert.Single(aggregate.Plan.Items);
            var finding = Assert.Single(aggregate.Source.Findings);
            var analysisItem = new FixPlanMutationAnalysisItemDto(planItem.Id, finding.Finding.Id,
                analysisStatus, "safe", new(aggregate.Plan.SourceDocumentVersionId,
                    "main-document-paragraph", 0, null, 0, null, "paragraph.alignment"), 1, []);
            var analysis = new FixPlanMutationAnalysisDto("fix-plan-mutation-analysis/1.0", analysisState, 1,
                analysisStatus == FixPlanMutationItemStatus.Independent ? 1 : 0, 0, 0,
                analysisStatus is FixPlanMutationItemStatus.Conflicting or FixPlanMutationItemStatus.DependencyCycle ? 1 : 0,
                analysisStatus == FixPlanMutationItemStatus.Stale ? 1 : 0, [analysisItem], [], [], []);
            var eligible = finding.Snapshot.FixMode is FixMode.Auto or FixMode.Confirm;
            var previewItem = new FixPlanDraftPreviewItemDto(planItem.Id, finding.Finding.Id,
                finding.Snapshot.RuleCode, finding.Snapshot.ValidationKey, finding.Snapshot.FixMode, null,
                eligible ? FixEligibilityStatus.Eligible : FixEligibilityStatus.Ineligible,
                eligible ? FixEligibilityReasonCode.Eligible : FixEligibilityReasonCode.ManualFixMode,
                finding.Snapshot.FixMode == FixMode.Confirm, FixPlanDraftPreviewItemState.Previewable,
                "safe", "paragraph.alignment", "1.0", "paragraph.alignment",
                new("main-document-paragraph", 0, null, 0, null),
                new("enum", "Alignment", "Before", "Left", "After", "Justified", "Available"));
            var preview = new FixPlanDraftPreviewDto(FixPlanDraftPreviewService.SchemaVersion,
                aggregate.Plan.Id, auditId, aggregate.Plan.SourceDocumentVersionId,
                aggregate.Source.Audit.DocumentVersion!.Sha256, aggregate.Plan.State, previewState,
                1, 1, 0, 0, [previewItem], analysis);
            var operation = new FixPlanOperation(FixOperationKind.SetProperty,
                "paragraph.alignment", "1.0", finding.Snapshot.RuleCode, finding.Snapshot.ValidationKey,
                [finding.Finding.Id], new("main-document-paragraph", 0, null, 0, null),
                "paragraph.alignment", new("enum-code", "justified"),
                finding.Snapshot.FixMode == FixMode.Confirm, 1, "paragraph-exists", "set-alignment");
            return new(preview, [new(planItem.Id, finding.Snapshot, finding.Finding.Confidence,
                finding.Finding.SourceSectionSnapshot, finding.Finding.PdfPageSnapshot,
                finding.Finding.PrintedPageSnapshot, finding.Snapshot.FixMode == FixMode.Confirm,
                "paragraph.alignment", "1.0", operation, previewItem, previewItem.Change!, analysisItem)]);
        }
    }

    private sealed class StubRepository(FixPlanDraftAggregate aggregate, bool replay, string? conflict)
        : IFixPlanApprovalRepository
    {
        public Task<FixPlanApprovalWriteResult> ApproveAsync(Guid auditId, Guid planId, Guid ownerUserId,
            string approvalRequestHash, DateTimeOffset now, Func<FixPlanDraftAggregate, FixPlanApprovalPrepared> prepare,
            CancellationToken cancellationToken)
        {
            if (conflict is not null) return Task.FromResult(new FixPlanApprovalWriteResult(null, null, false, conflict));
            FixPlanApprovalPrepared prepared;
            if (replay)
                prepared = new(FixPlanApprovalSnapshotSerializer.SchemaVersion, new string('d', 64),
                    approvalRequestHash, new string('a', 64), "{}", aggregate.Plan.Items.Count);
            else prepared = prepare(aggregate);
            if (aggregate.Plan.State == FixPlanLifecycleState.Draft) aggregate.Plan.Approve(ownerUserId, now);
            var snapshot = FixPlanApprovalSnapshotRecord.Create(planId, prepared.SchemaVersion, prepared.PlanHash,
                prepared.ApprovalRequestHash, prepared.SourceVersionSha256, prepared.SnapshotJson, ownerUserId, now);
            return Task.FromResult(new FixPlanApprovalWriteResult(aggregate.Plan, snapshot, replay));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record TestContext(Guid AuditId, Guid OwnerId, FixPlanRecord Plan,
        FixPlanDraftAggregate Aggregate, StubBuilder Builder, FixPlanApprovalService Service);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "supabase")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
