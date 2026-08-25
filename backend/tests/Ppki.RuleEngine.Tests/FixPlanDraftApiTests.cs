using System.Reflection;
using Ppki.Application;
using Ppki.Domain;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixPlanDraftServiceTests
{
    [Fact]
    public async Task Eligible_auto_finding_creates_canonical_draft()
    {
        var repository = new FakeRepository(Source());
        var eligibility = new FakeEligibility();
        var service = Service(repository, eligibility);

        var result = await service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);

        Assert.NotNull(result);
        Assert.Equal(FixPlanLifecycleState.Draft, result.State);
        Assert.Equal(Id(1), result.AuditId);
        Assert.Equal(Id(2), result.SourceDocumentVersionId);
        Assert.Equal(Id(9), result.OwnerUserId);
        Assert.Equal(FixMode.Auto, Assert.Single(result.Items).FixMode);
        Assert.Equal(1, eligibility.CallCount);
    }

    [Fact]
    public async Task Eligible_confirm_is_retained_and_not_approved()
    {
        var source = Source(FixMode.Confirm);
        var repository = new FakeRepository(source);
        var result = await Service(repository).CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);

        var item = Assert.Single(result!.Items);
        Assert.Equal(FixMode.Confirm, item.FixMode);
        Assert.True(item.RequiresExplicitApproval);
        Assert.Equal(FixPlanLifecycleState.Draft, result.State);
        Assert.Null(repository.Plan!.ApproverUserId);
        Assert.Null(repository.Plan.ApprovedAt);
    }

    [Theory]
    [InlineData(FixMode.Manual, FixEligibilityReasonCode.ManualFixMode)]
    [InlineData(FixMode.Report, FixEligibilityReasonCode.ReportFixMode)]
    public async Task Manual_and_report_are_rejected_with_authoritative_reason(
        FixMode mode, FixEligibilityReasonCode reason)
    {
        var exception = await Assert.ThrowsAsync<FixPlanDraftException>(() =>
            Service(new FakeRepository(Source(mode))).CreateAsync(Id(1), Id(9), Id(8), Selection(3), default));

        Assert.Equal("fix-plan-item-ineligible", exception.DiagnosticCode);
        Assert.Equal(reason, exception.EligibilityReason);
    }

    [Theory]
    [InlineData(FixEligibilityReasonCode.FixerNotRegistered)]
    [InlineData(FixEligibilityReasonCode.FixerVersionIncompatible)]
    [InlineData(FixEligibilityReasonCode.FindingContractUnsupported)]
    [InlineData(FixEligibilityReasonCode.FindingNotOpen)]
    public async Task Technical_or_state_ineligibility_is_never_silently_discarded(
        FixEligibilityReasonCode reason)
    {
        var eligibility = new FakeEligibility(reason);
        var repository = new FakeRepository(Source());

        var exception = await Assert.ThrowsAsync<FixPlanDraftException>(() =>
            Service(repository, eligibility).CreateAsync(Id(1), Id(9), Id(8), Selection(3), default));

        Assert.Equal(reason, exception.EligibilityReason);
        Assert.Null(repository.Plan);
    }

    [Fact]
    public async Task Confirm_with_unsupported_provider_is_rejected_without_auto_promotion()
    {
        var eligibility = new FakeEligibility(FixEligibilityReasonCode.FindingContractUnsupported);
        var exception = await Assert.ThrowsAsync<FixPlanDraftException>(() =>
            Service(new FakeRepository(Source(FixMode.Confirm)), eligibility)
                .CreateAsync(Id(1), Id(9), Id(8), Selection(3), default));

        Assert.Equal(FixEligibilityReasonCode.FindingContractUnsupported, exception.EligibilityReason);
        Assert.Equal(FixMode.Confirm, eligibility.LastInput!.Finding.FixMode);
    }

    [Fact]
    public async Task Missing_audit_or_finding_returns_not_found_without_partial_plan()
    {
        var repository = new FakeRepository(null);
        var result = await Service(repository).CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);

        Assert.Null(result);
        Assert.Null(repository.Plan);
    }

    [Fact]
    public async Task Mixed_audit_or_missing_finding_is_rejected_without_partial_plan_or_items()
    {
        var repository = new FakeRepository(Source());

        var result = await Service(repository).CreateAsync(Id(1), Id(9), Id(8), Selection(3, 4), default);

        Assert.Null(result);
        Assert.Null(repository.Plan);
        Assert.Equal(0, repository.ExecutionsQueued);
        Assert.Equal(0, repository.DocumentVersionsCreated);
    }

    [Theory]
    [InlineData("fix-plan-source-version-unavailable")]
    [InlineData("fix-plan-source-version-superseded")]
    public async Task Non_current_or_unavailable_source_is_rejected(string staleCode)
    {
        var repository = new FakeRepository(Source() with { StaleReasonCode = staleCode });

        var exception = await Assert.ThrowsAsync<FixPlanDraftException>(() =>
            Service(repository).CreateAsync(Id(1), Id(9), Id(8), Selection(3), default));

        Assert.Equal(staleCode, exception.DiagnosticCode);
        Assert.Null(repository.Plan);
    }

    [Fact]
    public async Task Incomplete_audit_is_rejected_through_eligibility_service()
    {
        var source = Source() with { Audit = Audit(AuditJobStatus.Processing) };
        var exception = await Assert.ThrowsAsync<FixPlanDraftException>(() =>
            Service(new FakeRepository(source)).CreateAsync(Id(1), Id(9), Id(8), Selection(3), default));

        Assert.Equal(FixEligibilityReasonCode.AuditNotCompleted, exception.EligibilityReason);
    }

    [Fact]
    public async Task Duplicate_request_ids_are_normalized_before_persistence()
    {
        Assert.True(FixPlanSelection.TryCreate([Id(3).ToString(), Id(3).ToString()],
            out var selection, out _));
        var repository = new FakeRepository(Source());

        var result = await Service(repository).CreateAsync(Id(1), Id(9), Id(8), selection, default);

        Assert.Single(selection.FindingIds);
        Assert.Single(result!.Items);
        Assert.Single(repository.Plan!.Items);
    }

    [Fact]
    public async Task Create_retry_replays_same_plan_and_conflicting_key_is_safe()
    {
        var repository = new FakeRepository(Source());
        var service = Service(repository);
        var first = await service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);
        repository.ReplayCreate = true;
        var replay = await service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);

        Assert.Equal(first!.Id, replay!.Id);
        Assert.True(replay.Replayed);

        repository.CreateConflict = "fix-plan-idempotency-conflict";
        var exception = await Assert.ThrowsAsync<FixPlanDraftException>(() =>
            service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default));
        Assert.Equal("fix-plan-idempotency-conflict", exception.DiagnosticCode);
    }

    [Fact]
    public async Task Update_replaces_only_draft_membership_and_safe_retry_is_replayed()
    {
        var initial = Source();
        var replacement = Source(findingId: 4);
        var repository = new FakeRepository(initial);
        var service = Service(repository);
        await service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);
        repository.Source = replacement;

        var result = await service.UpdateAsync(Id(1), repository.Plan!.Id, Id(9), Selection(4), default);

        Assert.Equal(Id(4), Assert.Single(result!.Items).FindingId);
        repository.ReplayReplace = true;
        var replay = await service.UpdateAsync(Id(1), repository.Plan.Id, Id(9), Selection(4), default);
        Assert.True(replay!.Replayed);
    }

    [Theory]
    [InlineData(FixMode.Manual, FixEligibilityReasonCode.ManualFixMode)]
    [InlineData(FixMode.Report, FixEligibilityReasonCode.ReportFixMode)]
    public async Task Update_cannot_introduce_manual_or_report_items(FixMode mode,
        FixEligibilityReasonCode expectedReason)
    {
        var repository = new FakeRepository(Source());
        var service = Service(repository);
        await service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);
        repository.Source = Source(mode, findingId: 4);

        var error = await Assert.ThrowsAsync<FixPlanDraftException>(() => service.UpdateAsync(
            Id(1), repository.Plan!.Id, Id(9), Selection(4), default));

        Assert.Equal("fix-plan-item-ineligible", error.DiagnosticCode);
        Assert.Equal(expectedReason, error.EligibilityReason);
        Assert.Equal(Id(3), Assert.Single(repository.Plan!.Items).FindingId);
    }

    [Fact]
    public async Task Another_owner_or_wrong_audit_cannot_read_or_modify_plan()
    {
        var repository = new FakeRepository(Source());
        var service = Service(repository);
        await service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);
        var planId = repository.Plan!.Id;

        Assert.Null(await service.GetAsync(Id(1), planId, Id(10), default));
        Assert.Null(await service.GetAsync(Id(99), planId, Id(9), default));
        Assert.Null(await service.UpdateAsync(Id(1), planId, Id(10), Selection(3), default));
        Assert.False(await service.DeleteAsync(Id(1), planId, Id(10), default));
    }

    [Fact]
    public async Task Get_detects_source_and_eligibility_staleness()
    {
        var repository = new FakeRepository(Source());
        var eligibility = new FakeEligibility();
        var service = Service(repository, eligibility);
        await service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);

        repository.Source = Source() with { StaleReasonCode = "fix-plan-source-version-superseded" };
        var sourceStale = await service.GetAsync(Id(1), repository.Plan!.Id, Id(9), default);
        Assert.True(sourceStale!.IsStale);
        Assert.Equal("fix-plan-source-version-superseded", sourceStale.StaleReasonCode);

        repository.Source = Source();
        eligibility.ForcedReason = FixEligibilityReasonCode.FixerNotRegistered;
        var capabilityStale = await service.GetAsync(Id(1), repository.Plan.Id, Id(9), default);
        Assert.True(capabilityStale!.IsStale);
        Assert.Equal("fix-plan-eligibility-changed", capabilityStale.StaleReasonCode);
    }

    [Fact]
    public async Task Draft_delete_is_isolated_and_non_draft_conflicts()
    {
        var repository = new FakeRepository(Source());
        var service = Service(repository);
        await service.CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);
        Assert.True(await service.DeleteAsync(Id(1), repository.Plan!.Id, Id(9), default));
        Assert.Null(repository.Plan);

        repository.DeleteConflict = "fix-plan-not-draft";
        var exception = await Assert.ThrowsAsync<FixPlanDraftException>(() =>
            service.DeleteAsync(Id(1), Id(7), Id(9), default));
        Assert.Equal("fix-plan-not-draft", exception.DiagnosticCode);
    }

    [Fact]
    public async Task Creation_does_not_mutate_finding_audit_or_create_downstream_state()
    {
        var source = Source();
        var findingStatus = source.Findings[0].Finding.Status;
        var auditStatus = source.Audit.Status;
        var repository = new FakeRepository(source);

        await Service(repository).CreateAsync(Id(1), Id(9), Id(8), Selection(3), default);

        Assert.Equal(findingStatus, source.Findings[0].Finding.Status);
        Assert.Equal(auditStatus, source.Audit.Status);
        Assert.Equal(0, repository.DocumentVersionsCreated);
        Assert.Equal(0, repository.ExecutionsQueued);
        Assert.False(repository.DocxMutated);
    }

    [Fact]
    public void Selection_size_is_bounded_by_existing_authoritative_limit()
    {
        var ids = Enumerable.Range(1, FixPlanSelection.MaximumFindingCount + 1)
            .Select(value => Id(value).ToString());

        Assert.False(FixPlanSelection.TryCreate(ids, out _, out var code));
        Assert.Equal("fix-plan-selection-too-large", code);
    }

    [Fact]
    public void Response_contract_excludes_provider_document_and_security_payloads()
    {
        var names = typeof(FixPlanDraftDto).GetProperties().Select(value => value.Name)
            .Concat(typeof(FixPlanDraftItemDto).GetProperties().Select(value => value.Name)).ToArray();

        foreach (var forbidden in new[] { "Provider", "Storage", "Path", "Url", "Actual", "Expected", "Location", "Message", "Secret" })
            Assert.DoesNotContain(names, value => value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    private static FixPlanDraftService Service(FakeRepository repository, FakeEligibility? eligibility = null) =>
        new(repository, eligibility ?? new FakeEligibility(), new FixedTimeProvider(Now));

    private static readonly DateTimeOffset Now = new(2026, 8, 25, 3, 4, 5, TimeSpan.Zero);

    private static FixPlanDraftSource Source(FixMode mode = FixMode.Auto, int findingId = 3)
    {
        var audit = Audit();
        var finding = new AuditFinding
        {
            Id = Id(findingId), AuditJobId = audit.Id, AuditJob = audit, RuleId = Id(30),
            RuleCodeSnapshot = "RULE-1", FixModeSnapshot = mode, Status = FindingStatus.Open,
            Message = "safe-code", ActualValueJson = "{}", ExpectedValueJson = "{}", LocationJson = "{}",
            Confidence = 0.75m
        };
        var snapshot = new FixPlanFindingSnapshot(finding.Id, 1, "RULE-1", "Layout", "Paragraph",
            "test.validation", RuleSeverity.Error, mode, finding.Status, "{}", "{}", "{}", 1);
        return new(audit, audit.DocumentVersionId, null,
            [new(finding, snapshot, FindingResolutionState.Open, FindingReviewState.NoReview)]);
    }

    private static AuditJob Audit(AuditJobStatus status = AuditJobStatus.Completed) => new()
    {
        Id = Id(1), DocumentVersionId = Id(2), ProfileVersionId = Id(20), Status = status
    };

    private static FixPlanSelection Selection(params int[] ids) =>
        new(ids.Select(Id).Order().ToArray());

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed class FakeEligibility(FixEligibilityReasonCode? forcedReason = null) : IFixEligibilityService
    {
        public int CallCount { get; private set; }
        public FixEligibilityReasonCode? ForcedReason { get; set; } = forcedReason;
        public FixEligibilityInput? LastInput { get; private set; }

        public FixEligibilityResult Evaluate(FixEligibilityInput input)
        {
            CallCount++;
            LastInput = input;
            var reason = ForcedReason ?? (input.AuditStatus != AuditJobStatus.Completed
                ? FixEligibilityReasonCode.AuditNotCompleted
                : input.Finding.FindingState != FindingStatus.Open
                    ? FixEligibilityReasonCode.FindingNotOpen
                    : input.Finding.FixMode == FixMode.Manual
                        ? FixEligibilityReasonCode.ManualFixMode
                        : input.Finding.FixMode == FixMode.Report
                            ? FixEligibilityReasonCode.ReportFixMode
                            : FixEligibilityReasonCode.Eligible);
            return new(input.Finding.FindingId, input.Finding.FixMode, input.Confidence,
                reason == FixEligibilityReasonCode.Eligible ? FixEligibilityStatus.Eligible : FixEligibilityStatus.Ineligible,
                reason, input.Finding.FixMode == FixMode.Confirm);
        }
    }

    private sealed class FakeRepository(FixPlanDraftSource? source) : IFixPlanDraftRepository
    {
        public FixPlanDraftSource? Source { get; set; } = source;
        public FixPlanRecord? Plan { get; set; }
        public bool ReplayCreate { get; set; }
        public bool ReplayReplace { get; set; }
        public string? CreateConflict { get; set; }
        public string? DeleteConflict { get; set; }
        public int DocumentVersionsCreated { get; private set; }
        public int ExecutionsQueued { get; private set; }
        public bool DocxMutated { get; private set; }

        public Task<FixPlanDraftSource?> LoadSourceAsync(Guid auditId, FixPlanSelection selection,
            CancellationToken cancellationToken) => Task.FromResult(Source is not null && Source.Audit.Id == auditId
            && selection.FindingIds.All(id => Source.Findings.Any(value => value.Finding.Id == id)) ? Source : null);

        public Task<FixPlanDraftAggregate?> LoadOwnedAsync(Guid auditId, Guid planId, Guid ownerUserId,
            CancellationToken cancellationToken) => Task.FromResult(Plan is not null && Source is not null
                && Plan.Id == planId && Plan.SourceAuditJobId == auditId && Plan.OwnerUserId == ownerUserId
                ? new FixPlanDraftAggregate(Plan, Source) : null);

        public Task<FixPlanDraftWriteResult> CreateAsync(FixPlanDraftSource value, Guid ownerUserId,
            Guid idempotencyKey, string requestHash, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (CreateConflict is not null) return Task.FromResult(new FixPlanDraftWriteResult(null, false, CreateConflict));
            if (!ReplayCreate || Plan is null)
            {
                Plan = FixPlanRecord.Create(value.Audit, ownerUserId, idempotencyKey, requestHash, now);
                Plan.ReplaceItems(value.Findings.Select(item => item.Finding), now);
            }
            return Task.FromResult(new FixPlanDraftWriteResult(Plan, ReplayCreate));
        }

        public Task<FixPlanDraftWriteResult> ReplaceAsync(Guid auditId, Guid planId, Guid ownerUserId,
            FixPlanDraftSource value, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (Plan is null || Plan.Id != planId || Plan.OwnerUserId != ownerUserId || Plan.SourceAuditJobId != auditId)
                return Task.FromResult(new FixPlanDraftWriteResult(null, false));
            if (!ReplayReplace) Plan.ReplaceItems(value.Findings.Select(item => item.Finding), now);
            return Task.FromResult(new FixPlanDraftWriteResult(Plan, ReplayReplace));
        }

        public Task<string?> DeleteAsync(Guid auditId, Guid planId, Guid ownerUserId,
            CancellationToken cancellationToken)
        {
            if (DeleteConflict is not null) return Task.FromResult<string?>(DeleteConflict);
            if (Plan is null || Plan.Id != planId || Plan.SourceAuditJobId != auditId || Plan.OwnerUserId != ownerUserId)
                return Task.FromResult<string?>("fix-plan-not-found");
            Plan = null;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class FixPlanDraftApiArchitectureTests
{
    [Fact]
    public void Api_exposes_draft_routes_and_the_separate_explicit_approval_command()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");

        Assert.Contains("MapPost(\"/audits/{id:guid}/fix-plans\"", api, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/audits/{id:guid}/fix-plans/{planId:guid}\"", api, StringComparison.Ordinal);
        Assert.Contains("MapPut(\"/audits/{id:guid}/fix-plans/{planId:guid}\"", api, StringComparison.Ordinal);
        Assert.Contains("MapDelete(\"/audits/{id:guid}/fix-plans/{planId:guid}\"", api, StringComparison.Ordinal);
        Assert.Contains("FixPlanDraftProblem", api, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status400BadRequest", api, StringComparison.Ordinal);
        Assert.Contains("exception.DiagnosticCode", api, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", api, StringComparison.Ordinal);
        Assert.Contains("ApproveAuditFixPlan", api, StringComparison.Ordinal);
        Assert.Contains("FixPlanApprovalProblem", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_and_authoritative_admin_filter_preserve_safe_401_and_403_behavior()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        var filter = Source("backend", "services", "Ppki.Api", "InternalAdminEndpointFilter.cs");

        Assert.Contains("MapGroup(\"/api\").RequireAuthorization().AddEndpointFilter<InternalAdminEndpointFilter>()", api,
            StringComparison.Ordinal);
        Assert.Contains("Results.Unauthorized()", filter, StringComparison.Ordinal);
        Assert.Contains("Results.Forbid()", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_filters_plan_mutations_by_owner_and_uses_serializable_update()
    {
        var repository = Source("backend", "src", "Ppki.Infrastructure", "FixPlanDraftRepository.cs");

        Assert.Contains("value.OwnerUserId == ownerUserId", repository, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.Serializable", repository, StringComparison.Ordinal);
        Assert.Contains("SourceCurrentAsync", repository, StringComparison.Ordinal);
        Assert.Contains("value.AuditJobId == auditId", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileStorage", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("FixExecutionJobs.Add", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentVersions.Add", repository, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_adds_only_retry_identity_and_preserves_rls()
    {
        var migration = Source("supabase", "migrations", "202608250002_fix_plan_draft_idempotency.sql");
        var baseline = Source("supabase", "migrations", "202608250001_fix_plan_records.sql");

        Assert.Contains("unique (owner_user_id, idempotency_key)", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fix plan idempotency identity is immutable.", migration, StringComparison.Ordinal);
        Assert.Contains("alter table public.fix_plans enable row level security", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("disable row level security", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant ", migration, StringComparison.OrdinalIgnoreCase);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(RepositoryRoot(), Path.Combine(parts)));

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var candidate = new DirectoryInfo(start); candidate is not null; candidate = candidate.Parent)
                if (Directory.Exists(Path.Combine(candidate.FullName, "supabase", "migrations"))) return candidate.FullName;
        throw new DirectoryNotFoundException();
    }
}
