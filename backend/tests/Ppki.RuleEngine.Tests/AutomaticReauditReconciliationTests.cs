using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

        var processorSource = Source("backend", "services", "Ppki.Worker",
            "AutomaticReauditRecoveryWorker.cs");
        Assert.DoesNotContain("AsEnumerable(", processorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ToList(", processorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ToListAsync(", processorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_reaudit_orders_entities_before_projection_and_translates_the_bounded_candidate_query()
    {
        using var db = OfflineContext();
        var query = AutomaticReauditRecoveryProcessor.MissingReaudit(db)
            .Select(value => new AutomaticReauditRecoveryCandidate(value.FixExecutionId, value.OwnerUserId))
            .Take(1);

        var sql = query.ToQueryString().ToLowerInvariant();
        Assert.Contains("order by f.completed_at, f.id", sql, StringComparison.Ordinal);
        Assert.Contains("limit", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relational_recovery_query_executes_deterministically_and_processor_preserves_service_routing()
    {
        var connectionString = Environment.GetEnvironmentVariable("PPKI_RECOVERY_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await CreateRecoveryTempTablesAsync(connection, transaction);
            var factory = new SharedConnectionDbFactory(connection, transaction);

            await using (var db = factory.CreateDbContext())
            {
                var selected = await AutomaticReauditRecoveryProcessor.MissingReaudit(db)
                    .Select(value => new AutomaticReauditRecoveryCandidate(
                        value.FixExecutionId, value.OwnerUserId))
                    .FirstAsync(CancellationToken.None);
                Assert.Equal(OldestLowFixExecutionId, selected.FixExecutionId);
                Assert.Equal(OwnerUserId, selected.OwnerUserId);
            }

            var reaudits = new RecordingReauditService(CreateAccepted(OldestLowFixExecutionId));
            var resolutions = new RecordingFindingResolutionService();
            var processor = new AutomaticReauditRecoveryProcessor(factory, reaudits, resolutions);
            Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
            Assert.Equal([OldestLowFixExecutionId], reaudits.FixExecutionIds);
            Assert.Equal([OldestLowFixExecutionId], resolutions.FixExecutionIds);

            await InsertReauditsAsync(connection, transaction);
            await using (var db = factory.CreateDbContext())
            {
                var selected = await AutomaticReauditRecoveryProcessor.MissingReconciliation(db)
                    .Select(value => new AutomaticReauditRecoveryCandidate(
                        value.FixExecutionId, value.OwnerUserId))
                    .FirstAsync(CancellationToken.None);
                Assert.Equal(OldestLowFixExecutionId, selected.FixExecutionId);
                Assert.Equal(OwnerUserId, selected.OwnerUserId);
            }

            reaudits.FixExecutionIds.Clear();
            resolutions.FixExecutionIds.Clear();
            Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
            Assert.Empty(reaudits.FixExecutionIds);
            Assert.Equal([OldestLowFixExecutionId], resolutions.FixExecutionIds);

            await DeleteOldestReauditAsync(connection, transaction);
            var rejectingProcessor = new AutomaticReauditRecoveryProcessor(factory,
                new RecordingReauditService(null), resolutions);
            var exception = await Assert.ThrowsAsync<ReauditException>(() =>
                rejectingProcessor.ProcessNextAsync(CancellationToken.None));
            Assert.Equal("automatic-reaudit-owner-mismatch", exception.DiagnosticCode);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Missing_reconciliation_orders_entities_before_projection_and_translates_the_bounded_candidate_query()
    {
        using var db = OfflineContext();
        var query = AutomaticReauditRecoveryProcessor.MissingReconciliation(db)
            .Select(value => new AutomaticReauditRecoveryCandidate(value.FixExecutionId, value.OwnerUserId))
            .Take(1);

        var sql = query.ToQueryString().ToLowerInvariant();
        Assert.Contains("order by a.created_at, a.source_fix_execution_id", sql, StringComparison.Ordinal);
        Assert.Contains("limit", sql, StringComparison.Ordinal);
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

    private static readonly Guid OwnerUserId = Guid.Parse("a7100000-0000-0000-0000-000000000001");
    private static readonly Guid OldestLowFixExecutionId = Guid.Parse("a7100000-0000-0000-0000-000000000010");

    private static async Task CreateRecoveryTempTablesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        const string sql = """
            create temp table documents (id uuid primary key, owner_user_id uuid not null);
            create temp table document_versions (id uuid primary key, document_id uuid not null);
            create temp table fix_execution_jobs (
                id uuid primary key,
                source_document_version_id uuid not null,
                result_document_version_id uuid,
                state text not null,
                completed_at timestamptz);
            create temp table audit_jobs (
                id uuid primary key,
                document_version_id uuid not null,
                source_fix_execution_id uuid,
                status text not null,
                created_at timestamptz not null);
            create temp table finding_resolution_events (
                source_reaudit_job_id uuid,
                event_type text not null);
            insert into documents values
                ('a7100000-0000-0000-0000-000000000002', 'a7100000-0000-0000-0000-000000000001');
            insert into document_versions values
                ('a7100000-0000-0000-0000-000000000003', 'a7100000-0000-0000-0000-000000000002');
            insert into fix_execution_jobs values
                ('a7100000-0000-0000-0000-000000000012', 'a7100000-0000-0000-0000-000000000003',
                    'a7100000-0000-0000-0000-000000000022', 'Completed', '2026-01-02T00:00:00Z'),
                ('a7100000-0000-0000-0000-000000000011', 'a7100000-0000-0000-0000-000000000003',
                    'a7100000-0000-0000-0000-000000000021', 'Completed', '2026-01-01T00:00:00Z'),
                ('a7100000-0000-0000-0000-000000000010', 'a7100000-0000-0000-0000-000000000003',
                    'a7100000-0000-0000-0000-000000000020', 'Completed', '2026-01-01T00:00:00Z');
            """;
        await ExecuteAsync(connection, transaction, sql);
    }

    private static Task InsertReauditsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        ExecuteAsync(connection, transaction, """
            insert into audit_jobs values
                ('a7100000-0000-0000-0000-000000000032', 'a7100000-0000-0000-0000-000000000003',
                    'a7100000-0000-0000-0000-000000000012', 'Queued', '2026-01-02T00:00:00Z'),
                ('a7100000-0000-0000-0000-000000000031', 'a7100000-0000-0000-0000-000000000003',
                    'a7100000-0000-0000-0000-000000000011', 'Queued', '2026-01-01T00:00:00Z'),
                ('a7100000-0000-0000-0000-000000000030', 'a7100000-0000-0000-0000-000000000003',
                    'a7100000-0000-0000-0000-000000000010', 'Queued', '2026-01-01T00:00:00Z');
            """);

    private static Task DeleteOldestReauditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        ExecuteAsync(connection, transaction,
            $"delete from audit_jobs where source_fix_execution_id = '{OldestLowFixExecutionId}'");

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static ReauditAccepted CreateAccepted(Guid fixExecutionId) => new(
        Guid.Parse("a7100000-0000-0000-0000-000000000040"), "Queued",
        Guid.Parse("a7100000-0000-0000-0000-000000000041"), fixExecutionId,
        Guid.Parse("a7100000-0000-0000-0000-000000000042"),
        Guid.Parse("a7100000-0000-0000-0000-000000000043"), new string('a', 64),
        DocumentKind.Skripsi, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), false);

    private sealed class SharedConnectionDbFactory(
        NpgsqlConnection connection, NpgsqlTransaction transaction) : IDbContextFactory<PpkiDbContext>
    {
        public PpkiDbContext CreateDbContext()
        {
            var db = new PpkiDbContext(new DbContextOptionsBuilder<PpkiDbContext>()
                .UseNpgsql(connection).Options);
            db.Database.UseTransaction(transaction);
            return db;
        }
    }

    private sealed class RecordingReauditService(ReauditAccepted? response) : IReauditService
    {
        public List<Guid> FixExecutionIds { get; } = [];

        public Task<ReauditAccepted?> CreateAsync(
            Guid sourceFixExecutionId, Guid ownerUserId, CancellationToken cancellationToken)
        {
            FixExecutionIds.Add(sourceFixExecutionId);
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingFindingResolutionService : IFindingResolutionService
    {
        public List<Guid> FixExecutionIds { get; } = [];

        public Task<FindingResolutionDto?> GetAsync(
            Guid auditId, Guid findingId, Guid ownerUserId, CancellationToken cancellationToken) =>
            Task.FromResult<FindingResolutionDto?>(null);

        public Task<FindingResolutionReconciliationResult?> ReconcileAsync(
            Guid fixExecutionId, Guid ownerUserId, CancellationToken cancellationToken)
        {
            FixExecutionIds.Add(fixExecutionId);
            return Task.FromResult<FindingResolutionReconciliationResult?>(null);
        }
    }

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
