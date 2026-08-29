using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.Infrastructure;
using Ppki.Worker;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class AutomaticReauditReconciliationTests
{
    [Theory]
    [InlineData(AuditComparisonStatus.NoLongerDetected, AutomaticFindingReconciliationOutcome.Fixed)]
    [InlineData(AuditComparisonStatus.StillDetected, AutomaticFindingReconciliationOutcome.StillFailing)]
    [InlineData(AuditComparisonStatus.Changed, AutomaticFindingReconciliationOutcome.PartiallyFixed)]
    public void Only_authoritative_comparison_semantics_create_an_outcome(
        AuditComparisonStatus comparison, AutomaticFindingReconciliationOutcome expected) =>
        Assert.Equal(expected, FixExecutionStatusChainService.Outcome(comparison));

    [Fact]
    public void Applied_or_newly_detected_never_implies_fixed()
    {
        Assert.Null(FixExecutionStatusChainService.Outcome(null));
        Assert.Null(FixExecutionStatusChainService.Outcome(AuditComparisonStatus.NewlyDetected));
        Assert.Equal(FindingResolutionState.Applied,
            FindingResolutionProjection.State(FindingResolutionEventType.FixAppliedObserved));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AuditJobStatus.Queued)]
    [InlineData(AuditJobStatus.Processing)]
    [InlineData(AuditJobStatus.Failed)]
    [InlineData(AuditJobStatus.Cancelled)]
    public void Non_completed_reaudit_cannot_finalize_reconciliation(AuditJobStatus? status) =>
        Assert.Equal(FindingResolutionReconciliationState.Pending,
            FixExecutionStatusChainService.ReconciliationState(status,
                [AutomaticFindingReconciliationOutcome.Fixed]));

    [Fact]
    public void Completed_reaudit_requires_an_authoritative_outcome_for_every_selected_finding()
    {
        Assert.Equal(FindingResolutionReconciliationState.Pending,
            FixExecutionStatusChainService.ReconciliationState(AuditJobStatus.Completed,
                [AutomaticFindingReconciliationOutcome.Fixed, null]));
        Assert.Equal(FindingResolutionReconciliationState.Pending,
            FixExecutionStatusChainService.ReconciliationState(AuditJobStatus.Completed, []));
        Assert.Equal(FindingResolutionReconciliationState.Completed,
            FixExecutionStatusChainService.ReconciliationState(AuditJobStatus.Completed,
                [AutomaticFindingReconciliationOutcome.Fixed,
                    AutomaticFindingReconciliationOutcome.PartiallyFixed,
                    AutomaticFindingReconciliationOutcome.StillFailing]));
    }

    [Fact]
    public void Durable_recovery_selects_only_canonical_completed_publications_and_owned_exact_versions()
    {
        using var db = OfflineContext();
        var enqueueSql = AutomaticReauditRecoveryProcessor.MissingReaudit(db).ToQueryString().ToLowerInvariant();
        var reconcileSql = AutomaticReauditRecoveryProcessor.MissingReconciliation(db).ToQueryString().ToLowerInvariant();

        Assert.Contains("fix_execution_jobs", enqueueSql, StringComparison.Ordinal);
        Assert.Contains("result_document_version_id", enqueueSql, StringComparison.Ordinal);
        Assert.Contains("source_fix_execution_id", enqueueSql, StringComparison.Ordinal);
        Assert.Contains("owner_user_id", enqueueSql, StringComparison.Ordinal);
        Assert.Contains("audit_jobs", reconcileSql, StringComparison.Ordinal);
        Assert.Contains("finding_resolution_events", reconcileSql, StringComparison.Ordinal);
        Assert.Contains("owner_user_id", reconcileSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_chain_query_is_owner_scoped_and_bound_to_persisted_execution_and_reaudit_ids()
    {
        var source = Source("backend", "src", "Ppki.Infrastructure", "FixExecutionStatusChainService.cs");
        Assert.Contains("OwnerUserId == ownerUserId", source, StringComparison.Ordinal);
        Assert.Contains("SourceFixExecutionId == source.Id", source, StringComparison.Ordinal);
        Assert.Contains("value.DocumentVersionId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FixItemResult", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditFindings.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentVersions.Add", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Automatic_worker_and_safe_authenticated_status_endpoint_are_registered()
    {
        var worker = Source("backend", "services", "Ppki.Worker", "Program.cs");
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        Assert.Contains("AddHostedService<AutomaticReauditRecoveryWorker>()", worker, StringComparison.Ordinal);
        Assert.Contains("/fix-executions/{executionId:guid}/status-chain", api, StringComparison.Ordinal);
        Assert.Contains("IFixExecutionStatusChainService", api, StringComparison.Ordinal);
        Assert.Contains("MapGroup(\"/api\").RequireAuthorization()", api, StringComparison.Ordinal);
        Assert.Contains("Results.NotFound()", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_contract_exposes_only_bounded_lineage_and_outcomes()
    {
        var properties = new[] { typeof(FixExecutionStatusChain), typeof(AutomaticReauditChainStatus),
            typeof(AutomaticFindingReconciliationStatus) }
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        var forbidden = new[] { "Token", "Secret", "Path", "Url", "Filename", "Text", "Xml",
            "Payload", "Snapshot", "Actual", "Expected", "Fingerprint" };
        Assert.DoesNotContain(properties, property => forbidden.Any(value =>
            property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private static PpkiDbContext OfflineContext() => new(new DbContextOptionsBuilder<PpkiDbContext>()
        .UseNpgsql("Host=localhost;Database=automatic_reaudit_offline_test").Options);

    private static string Source(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. segments]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "backend")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
