using Ppki.Application;
using Ppki.Domain;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class RemediationFailureCatalogTests
{
    public static TheoryData<string, FixFailureCategory> MinimumCodes => new()
    {
        { "fix-plan-stale", FixFailureCategory.Conflict },
        { "fix-source-version-superseded", FixFailureCategory.Conflict },
        { "fix-result-object-conflict", FixFailureCategory.Conflict },
        { "fix-version-number-conflict", FixFailureCategory.Conflict },
        { "fix-execution-conflict", FixFailureCategory.Conflict },
        { "fix-concurrent-publish-conflict", FixFailureCategory.Conflict },
        { "source-version-missing", FixFailureCategory.InvalidSource },
        { "source-storage-object-missing", FixFailureCategory.InvalidSource },
        { "source-size-invalid", FixFailureCategory.InvalidSource },
        { "source-hash-mismatch", FixFailureCategory.InvalidSource },
        { "source-package-invalid", FixFailureCategory.InvalidSource },
        { "approved-plan-invalid", FixFailureCategory.InvalidPlan },
        { "approved-plan-hash-invalid", FixFailureCategory.InvalidPlan },
        { "approved-plan-selection-invalid", FixFailureCategory.InvalidPlan },
        { "approved-plan-operation-invalid", FixFailureCategory.InvalidPlan },
        { "approved-plan-provider-mismatch", FixFailureCategory.InvalidPlan },
        { "fix-provider-unavailable", FixFailureCategory.CapabilityUnavailable },
        { "fix-provider-not-registered", FixFailureCategory.CapabilityUnavailable },
        { "fix-provider-version-unavailable", FixFailureCategory.CapabilityUnavailable },
        { "storage-download-transient", FixFailureCategory.TransientInfrastructure },
        { "storage-upload-transient", FixFailureCategory.TransientInfrastructure },
        { "database-transient", FixFailureCategory.TransientInfrastructure },
        { "worker-lease-lost", FixFailureCategory.TransientInfrastructure },
        { "worker-interrupted", FixFailureCategory.TransientInfrastructure },
        { "storage-upload-terminal", FixFailureCategory.TerminalInfrastructure },
        { "database-finalization-terminal", FixFailureCategory.TerminalInfrastructure },
        { "result-cleanup-failed", FixFailureCategory.TerminalInfrastructure }
    };

    [Theory]
    [MemberData(nameof(MinimumCodes))]
    public void Minimum_safe_code_has_typed_category(string code, FixFailureCategory category)
    {
        Assert.Equal(category, FixFailureCatalog.Classify(code));
        Assert.True(FixFailureCatalog.IsSafe(code));
        Assert.Equal(category == FixFailureCategory.TransientInfrastructure,
            new FixExecutionException(code).Retryable);
    }

    [Fact]
    public void Retry_is_fixed_versioned_and_bounded()
    {
        Assert.Equal(3, FixRetryPolicy.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), FixRetryPolicy.Backoff);
        Assert.Equal("fix-retry/1.0", FixRetryPolicy.Version);
        Assert.True(FixRetryPolicy.ShouldRetry(FixFailureCategory.TransientInfrastructure, 1, 3));
        Assert.False(FixRetryPolicy.ShouldRetry(FixFailureCategory.TransientInfrastructure, 3, 3));
        Assert.False(FixRetryPolicy.ShouldRetry(FixFailureCategory.InvalidPlan, 1, 3));
    }
}

public sealed class RemediationFaultInjectionTests
{
    public static TheoryData<string, RemediationCheckpoint, string> Scenarios => new()
    {
        { "worker-crash-after-claim", RemediationCheckpoint.AfterClaim, "worker-interrupted" },
        { "lease-expiry-before-processing", RemediationCheckpoint.AfterClaim, "worker-lease-lost" },
        { "lease-expiry-during-processing", RemediationCheckpoint.AfterApply, "worker-lease-lost" },
        { "stale-completion-after-takeover", RemediationCheckpoint.BeforeDatabaseFinalization, "worker-lease-lost" },
        { "source-object-missing", RemediationCheckpoint.BeforeSourceDownload, "source-storage-object-missing" },
        { "source-download-timeout", RemediationCheckpoint.BeforeSourceDownload, "storage-download-transient" },
        { "source-size-overflow", RemediationCheckpoint.AfterSourceDownload, "source-size-invalid" },
        { "source-sha-mismatch", RemediationCheckpoint.AfterSourceDownload, "source-hash-mismatch" },
        { "malformed-package", RemediationCheckpoint.AfterSourceDownload, "source-package-invalid" },
        { "provider-missing", RemediationCheckpoint.BeforeApply, "fix-provider-unavailable" },
        { "provider-version-mismatch", RemediationCheckpoint.BeforeApply, "fix-provider-version-unavailable" },
        { "operation-precondition-mismatch", RemediationCheckpoint.BeforeApply, "approved-plan-operation-invalid" },
        { "output-package-invalid", RemediationCheckpoint.AfterApply, "source-package-invalid" },
        { "output-postcondition-failure", RemediationCheckpoint.AfterApply, "approved-plan-operation-invalid" },
        { "upload-timeout-before-create", RemediationCheckpoint.BeforeResultUpload, "storage-upload-transient" },
        { "upload-timeout-after-create", RemediationCheckpoint.AfterResultUpload, "storage-upload-transient" },
        { "existing-identical-result", RemediationCheckpoint.BeforeResultUpload, "database-transient" },
        { "existing-conflicting-result", RemediationCheckpoint.BeforeResultUpload, "fix-result-object-conflict" },
        { "database-deadlock", RemediationCheckpoint.BeforeDatabaseFinalization, "database-transient" },
        { "database-failure-after-upload", RemediationCheckpoint.BeforeDatabaseFinalization, "database-transient" },
        { "cleanup-success-path", RemediationCheckpoint.BeforeOrphanCleanup, "database-transient" },
        { "cleanup-failure", RemediationCheckpoint.BeforeOrphanCleanup, "result-cleanup-failed" },
        { "current-version-changed-before-start", RemediationCheckpoint.AfterClaim, "fix-source-version-superseded" },
        { "current-version-changed-before-publish", RemediationCheckpoint.BeforeDatabaseFinalization, "fix-source-version-superseded" },
        { "concurrent-two-worker-publish", RemediationCheckpoint.BeforeDatabaseFinalization, "fix-concurrent-publish-conflict" },
        { "concurrent-version-allocation", RemediationCheckpoint.BeforeDatabaseFinalization, "fix-version-number-conflict" },
        { "response-lost-after-completion", RemediationCheckpoint.AfterDatabaseFinalization, "fix-execution-conflict" },
        { "no-change-retry", RemediationCheckpoint.AfterApply, "database-transient" },
        { "provider-registry-drift", RemediationCheckpoint.BeforeApply, "fix-provider-version-unavailable" },
        { "terminal-execution-replay", RemediationCheckpoint.AfterClaim, "fix-execution-conflict" }
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Scripted_fault_is_deterministic_and_safe(string name, RemediationCheckpoint checkpoint, string code)
    {
        var injector = new ScriptedFaultInjector(checkpoint, new FixExecutionException(code));
        var first = await Assert.ThrowsAsync<FixExecutionException>(async () =>
            await injector.CheckpointAsync(checkpoint, Guid.Empty, 1, CancellationToken.None));
        var second = await Assert.ThrowsAsync<FixExecutionException>(async () =>
            await injector.CheckpointAsync(checkpoint, Guid.Empty, 1, CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal(code, first.DiagnosticCode);
        Assert.Equal(first.Category, second.Category);
        Assert.DoesNotContain("path", first.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScriptedFaultInjector(RemediationCheckpoint target, FixExecutionException failure)
        : IRemediationFaultInjector
    {
        public ValueTask CheckpointAsync(RemediationCheckpoint checkpoint, Guid executionId,
            int attemptNumber, CancellationToken cancellationToken) => checkpoint == target
                ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
    }
}

public sealed class RemediationHardeningArchitectureTests
{
    [Fact]
    public void Additive_migration_enforces_fencing_retry_terminal_and_publish_contracts()
    {
        var sql = Source("supabase", "migrations", "202608060002_remediation_failure_conflict_hardening.sql");
        Assert.Contains("add column claim_token uuid", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attempt_count between 0 and max_attempts", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max_attempts = 3", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("old.lease_expires_at < statement_timestamp()", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new.claim_token is distinct from old.claim_token", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TransientInfrastructure", sql, StringComparison.Ordinal);
        Assert.Contains("Fix execution result lineage is invalid", sql, StringComparison.Ordinal);
        Assert.Contains("for update of document", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter table public.audit_findings", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter table public.audit_rule_snapshots", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_claim_and_publish_use_exact_token_and_current_version_fences()
    {
        var worker = Source("backend", "services", "Ppki.Worker", "QueuedFixExecutionWorker.cs");
        var processor = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        Assert.Contains("for update skip locked", worker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attempt_count < max_attempts", worker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("value.AttemptCount >= value.MaxAttempts", worker, StringComparison.Ordinal);
        Assert.Contains("value.ClaimToken == claim.Token", worker, StringComparison.Ordinal);
        Assert.Contains("job.ClaimToken != claim.Token", processor, StringComparison.Ordinal);
        Assert.Contains("document.CurrentVersionNo != source.SourceVersionNo", processor, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.Serializable", processor, StringComparison.Ordinal);
        Assert.Contains("ParentVersionId = source.SourceVersionId", processor, StringComparison.Ordinal);
        Assert.Contains("var resultId = claim.ExecutionId", processor, StringComparison.Ordinal);
        Assert.Contains("ownsUploadedObject", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_status_is_additive_and_has_no_sensitive_fields()
    {
        var names = typeof(FixExecutionStatus).GetProperties().Select(value => value.Name).ToArray();
        Assert.Contains("FailureCategory", names);
        Assert.Contains("AttemptCount", names);
        Assert.Contains("MaxAttempts", names);
        Assert.Contains("RetryPending", names);
        Assert.Contains("LeaseState", names);
        Assert.DoesNotContain(names, value => value.Contains("Storage", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Filename", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Snapshot", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Operation", StringComparison.OrdinalIgnoreCase) && value != "PlannedOperationCount"
                && value != "CompletedOperationCount" && value != "FailedOperationCount");
    }

    [Fact]
    public void Downstream_requires_completed_result_and_review_does_not_mutate_execution()
    {
        var reaudit = Source("backend", "src", "Ppki.Infrastructure", "ReauditService.cs");
        var resolution = Source("backend", "src", "Ppki.Infrastructure", "FindingResolutionService.cs");
        var review = Source("backend", "src", "Ppki.Infrastructure", "FindingReviewService.cs");
        Assert.Contains("source.ExecutionState != FixExecutionState.Completed", reaudit, StringComparison.Ordinal);
        Assert.Contains("source.ExecutionState != FixExecutionState.Completed", resolution, StringComparison.Ordinal);
        Assert.DoesNotContain("FixExecutionJobs.Update", review, StringComparison.Ordinal);
        Assert.DoesNotContain("FixExecutionJobs.Add", review, StringComparison.Ordinal);
    }

    [Fact]
    public void Forbidden_rule_parser_and_frontend_inputs_are_not_part_of_hardening()
    {
        Assert.Equal("4.0", Ppki.DocxEngine.OpenXmlDocxParser.SchemaVersion);
        var processor = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        Assert.Contains("ApprovedPlanSnapshotJson", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleDefinitions", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileRules", processor, StringComparison.Ordinal);
    }

    private static string Source(params string[] segments) => File.ReadAllText(Path.Combine([Data.RepositoryRoot(), .. segments]));
}
