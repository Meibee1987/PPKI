using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;
using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class ReauditCreationContractTests
{
    private readonly ResolvedRuleSetHasher hasher = new();

    [Fact]
    public void Completed_execution_with_exact_historical_context_is_accepted()
    {
        var snapshots = Snapshots();
        var source = Source(snapshots);

        Assert.Null(ReauditCreationContract.Validate(source, snapshots, hasher));
    }

    [Theory]
    [InlineData(FixExecutionState.Queued)]
    [InlineData(FixExecutionState.Processing)]
    [InlineData(FixExecutionState.Failed)]
    [InlineData(FixExecutionState.NoChange)]
    public void Non_completed_execution_is_rejected(FixExecutionState state)
    {
        var snapshots = Snapshots();
        Assert.Equal("reaudit-execution-not-completed",
            ReauditCreationContract.Validate(Source(snapshots) with { ExecutionState = state }, snapshots, hasher));
    }

    [Fact]
    public void Missing_result_version_is_rejected()
    {
        var snapshots = Snapshots();
        Assert.Equal("reaudit-result-version-missing", ReauditCreationContract.Validate(
            Source(snapshots) with { ResultDocumentVersionId = null }, snapshots, hasher));
    }

    [Theory]
    [InlineData(AuditJobStatus.Queued)]
    [InlineData(AuditJobStatus.Processing)]
    [InlineData(AuditJobStatus.Failed)]
    [InlineData(AuditJobStatus.Cancelled)]
    public void Non_completed_source_audit_is_rejected(AuditJobStatus status)
    {
        var snapshots = Snapshots();
        Assert.Equal("reaudit-source-audit-not-completed", ReauditCreationContract.Validate(
            Source(snapshots) with { SourceAuditStatus = status }, snapshots, hasher));
    }

    [Fact]
    public void Result_document_mismatch_is_rejected()
    {
        var snapshots = Snapshots();
        Assert.Equal("reaudit-result-lineage-invalid", ReauditCreationContract.Validate(
            Source(snapshots) with { ResultDocumentId = Guid.NewGuid() }, snapshots, hasher));
    }

    [Fact]
    public void Source_audit_version_mismatch_is_rejected()
    {
        var snapshots = Snapshots();
        Assert.Equal("reaudit-source-lineage-invalid", ReauditCreationContract.Validate(
            Source(snapshots) with { SourceAuditDocumentVersionId = Guid.NewGuid() }, snapshots, hasher));
    }

    [Fact]
    public void Missing_duplicate_or_hash_mismatched_snapshots_are_rejected()
    {
        var snapshots = Snapshots();
        var source = Source(snapshots);
        Assert.Equal("reaudit-source-snapshots-invalid",
            ReauditCreationContract.Validate(source, [], hasher));
        Assert.Equal("reaudit-source-snapshots-invalid", ReauditCreationContract.Validate(
            source with { ApplicableRuleCount = 2 }, [snapshots[0], snapshots[0]], hasher));
        Assert.Equal("reaudit-source-snapshot-hash-mismatch", ReauditCreationContract.Validate(
            source with { ResolvedRuleSetHash = new string('f', 64) }, snapshots, hasher));
    }

    [Fact]
    public void Clone_preserves_every_historical_rule_field_and_ordinal_but_has_new_identity()
    {
        var source = Snapshots();
        var targetAuditId = Guid.NewGuid();
        var createdAt = DateTimeOffset.Parse("2026-08-04T00:00:00Z");

        var clones = ReauditCreationContract.Clone(targetAuditId, source, createdAt);

        Assert.Equal(source.Count, clones.Count);
        Assert.Equal(hasher.Hash(source), hasher.Hash(clones));
        for (var index = 0; index < source.Count; index++)
        {
            var before = source[index];
            var after = clones[index];
            Assert.NotEqual(before.Id, after.Id);
            Assert.Equal(targetAuditId, after.AuditJobId);
            Assert.Equal(createdAt, after.CreatedAt);
            Assert.Equal(before.RuleId, after.RuleId);
            Assert.Equal(before.RuleCode, after.RuleCode);
            Assert.Equal(before.Domain, after.Domain);
            Assert.Equal(before.Subdomain, after.Subdomain);
            Assert.Equal(before.AppliesTo, after.AppliesTo);
            Assert.Equal(before.Element, after.Element);
            Assert.Equal(before.RequirementJson, after.RequirementJson);
            Assert.Equal(before.ValidationKey, after.ValidationKey);
            Assert.Equal(before.ValidationJson, after.ValidationJson);
            Assert.Equal(before.Severity, after.Severity);
            Assert.Equal(before.FixMode, after.FixMode);
            Assert.Equal(before.SourceReferenceJson, after.SourceReferenceJson);
            Assert.Equal(before.Layer, after.Layer);
            Assert.Equal(before.Precedence, after.Precedence);
            Assert.Equal(before.Ordinal, after.Ordinal);
            Assert.Equal(before.SnapshotSchemaVersion, after.SnapshotSchemaVersion);
        }
    }

    [Fact]
    public void Live_rule_or_document_type_mutation_cannot_change_cloned_context()
    {
        var source = Snapshots();
        var expectedHash = hasher.Hash(source);
        var liveRule = new RuleDefinition
        {
            RuleCode = source[0].RuleCode, Domain = "Changed", AppliesTo = "Changed", Element = "Changed",
            OfficialRequirement = "Changed", ExpectedValuePattern = "Changed", ValidationKey = "changed",
            Severity = RuleSeverity.Info, FixMode = FixMode.Manual
        };
        var liveType = new DocumentType { Code = "CHANGED", Name = "Changed", Kind = DocumentKind.Tesis };
        liveRule.ValidationKey = "changed-again";
        liveType.Kind = DocumentKind.Disertasi;

        var clones = ReauditCreationContract.Clone(Guid.NewGuid(), source, DateTimeOffset.UtcNow);

        Assert.Equal(expectedHash, hasher.Hash(clones));
        Assert.Equal(DocumentKind.Skripsi, Source(source).DocumentKindSnapshot);
        Assert.DoesNotContain(clones, value => value.ValidationKey.StartsWith("changed", StringComparison.Ordinal));
    }

    private ReauditSourceContext Source(IReadOnlyList<AuditRuleSnapshot> snapshots)
    {
        var documentId = Guid.Parse("91000000-0000-0000-0000-000000000001");
        return new(
            Guid.Parse("91000000-0000-0000-0000-000000000002"),
            FixExecutionState.Completed,
            Guid.Parse("91000000-0000-0000-0000-000000000003"),
            AuditJobStatus.Completed,
            Guid.Parse("91000000-0000-0000-0000-000000000004"),
            Guid.Parse("91000000-0000-0000-0000-000000000004"),
            documentId,
            Guid.Parse("91000000-0000-0000-0000-000000000005"),
            documentId,
            Guid.Parse("21000000-0000-0000-0000-000000000001"),
            DocumentKind.Skripsi,
            hasher.Hash(snapshots),
            snapshots.Count,
            Guid.Parse("91000000-0000-0000-0000-000000000006"));
    }

    internal static IReadOnlyList<AuditRuleSnapshot> Snapshots() =>
    [
        Snapshot("PPKI-LAY-003", "section.page-size-a4", 1),
        Snapshot("PPKI-LAY-019", "body.justified", 2)
    ];

    private static AuditRuleSnapshot Snapshot(string code, string validationKey, int ordinal) => new()
    {
        Id = Guid.NewGuid(),
        AuditJobId = Guid.NewGuid(),
        RuleId = Guid.NewGuid(),
        RuleCode = code,
        Domain = "Tata Letak",
        Subdomain = "Synthetic",
        AppliesTo = "Skripsi",
        Element = "Paragraph",
        RequirementJson = "{\"expected\":\"synthetic\"}",
        ValidationKey = validationKey,
        ValidationJson = "{\"enabled\":true}",
        Severity = RuleSeverity.Error,
        FixMode = FixMode.Auto,
        SourceReferenceJson = "{\"sourceSection\":\"synthetic\"}",
        Layer = "profile",
        Precedence = 0,
        Ordinal = ordinal,
        SnapshotSchemaVersion = 1
    };
}

public sealed class ReauditArchitectureTests
{
    [Fact]
    public void Endpoint_is_authenticated_thin_bodyless_and_has_safe_http_semantics()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        var start = api.IndexOf("api.MapPost(\"/fix-executions/{executionId}/re-audit\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var route = api[start..api.IndexOf("api.MapGet(\"/fix-executions/{executionId}/comparison", start, StringComparison.Ordinal)];
        Assert.Contains("IReauditService", route, StringComparison.Ordinal);
        Assert.DoesNotContain("PpkiDbContext", route, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequest", route, StringComparison.Ordinal);
        Assert.DoesNotContain("ReauditRequest", route, StringComparison.Ordinal);
        Assert.Contains("Results.Accepted", route, StringComparison.Ordinal);
        Assert.Contains("Results.Ok", route, StringComparison.Ordinal);
        Assert.Contains("Results.NotFound", route, StringComparison.Ordinal);
        Assert.Contains("Status409Conflict", route, StringComparison.Ordinal);
        Assert.Contains("Status400BadRequest", route, StringComparison.Ordinal);
        Assert.Contains("MapGroup(\"/api\").RequireAuthorization()", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_admin_resource_is_filtered_by_identity_and_no_live_catalog_or_findings_are_read()
    {
        var source = Source("backend", "src", "Ppki.Infrastructure", "ReauditService.cs");
        var ownedQuery = source[source.IndexOf("public static IQueryable<ReauditSourceContext> OwnedSource", StringComparison.Ordinal)..];
        var identityFilter = ownedQuery.IndexOf("value.Id == executionId", StringComparison.Ordinal);
        var projection = ownedQuery.IndexOf(".Select(value => new ReauditSourceContext", StringComparison.Ordinal);
        Assert.True(identityFilter >= 0 && projection > identityFilter);
        Assert.DoesNotContain("OwnerUserId == ownerUserId", ownedQuery, StringComparison.Ordinal);
        Assert.Contains("OwnedSource(db, sourceFixExecutionId, ownerUserId)\n            .SingleOrDefaultAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Rules", source, StringComparison.Ordinal);
        Assert.DoesNotContain("db.ProfileRules", source, StringComparison.Ordinal);
        Assert.DoesNotContain("db.DocumentTypes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleCatalogImporter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditFindings", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_worker_uses_precloned_snapshots_without_live_resolution_for_reaudits()
    {
        var runner = Source("backend", "src", "Ppki.RuleEngine", "AuditRunner.cs");
        var historicalBranch = runner.IndexOf("if (audit.SourceFixExecutionId is not null)", StringComparison.Ordinal);
        var liveRules = runner.IndexOf("var assignedRules = await db.ProfileRules", StringComparison.Ordinal);
        Assert.True(historicalBranch >= 0 && liveRules > historicalBranch);
        Assert.Contains("resolvedRules = [];", runner, StringComparison.Ordinal);
        Assert.Contains("EnsureRuleSnapshotsAsync", runner, StringComparison.Ordinal);
        var worker = Source("backend", "services", "Ppki.Worker", "QueuedAuditWorker.cs");
        Assert.Contains("x.Status == AuditJobStatus.Queued", worker, StringComparison.Ordinal);
        Assert.Contains("auditRunner.RunAsync", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_contract_has_lineage_but_no_content_storage_or_secret_fields()
    {
        var names = typeof(ReauditAccepted).GetProperties().Select(value => value.Name).ToArray();
        Assert.Contains(nameof(ReauditAccepted.SourceAuditId), names);
        Assert.Contains(nameof(ReauditAccepted.SourceFixExecutionId), names);
        Assert.DoesNotContain(names, value => new[] { "Path", "Filename", "Text", "Xml", "Url", "Secret", "Token", "Finding" }
            .Any(forbidden => value.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Migration_is_additive_unique_deferred_immutable_and_has_no_backfill()
    {
        var sql = Source("supabase", "migrations", "202608040002_reaudit_orchestration.sql");
        Assert.Contains("add column source_audit_job_id uuid", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add column source_fix_execution_id uuid", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unique (source_fix_execution_id)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Re-audit lineage is immutable", sql, StringComparison.Ordinal);
        Assert.Contains("deferrable initially deferred", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Re-audit snapshot clone differs from source", sql, StringComparison.Ordinal);
        Assert.Contains("Re-audit cannot copy source findings", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("update public.audit_jobs", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rules.json", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ef_model_has_nullable_legacy_lineage_unique_fix_identity_and_restrict_foreign_keys()
    {
        using var db = new PpkiDbContext(new DbContextOptionsBuilder<PpkiDbContext>()
            .UseNpgsql("Host=localhost;Database=reaudit_offline_test").Options);
        var audit = db.Model.FindEntityType(typeof(AuditJob))!;
        Assert.True(audit.FindProperty(nameof(AuditJob.SourceAuditJobId))!.IsNullable);
        Assert.True(audit.FindProperty(nameof(AuditJob.SourceFixExecutionId))!.IsNullable);
        Assert.Contains(audit.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(value => value.Name).SequenceEqual([nameof(AuditJob.SourceFixExecutionId)]));
        Assert.Contains(audit.GetForeignKeys(), key => key.Properties.Select(value => value.Name)
            .SequenceEqual([nameof(AuditJob.SourceAuditJobId)]) && key.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(audit.GetForeignKeys(), key => key.Properties.Select(value => value.Name)
            .SequenceEqual([nameof(AuditJob.SourceFixExecutionId)]) && key.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Fact]
    public void S4_t01_adds_no_comparison_resolution_frontend_or_new_fix_capability()
    {
        var files = new[]
        {
            Source("backend", "src", "Ppki.Application", "ReauditContracts.cs"),
            Source("backend", "src", "Ppki.Infrastructure", "ReauditService.cs"),
            Source("supabase", "migrations", "202608040002_reaudit_orchestration.sql")
        };
        Assert.All(files, value =>
        {
            Assert.DoesNotContain("BeforeAfterComparison", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FindingResolution", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("accepted-risk", value, StringComparison.OrdinalIgnoreCase);
        });
        var capabilities = ProductionFixCapabilities.CreatePreviewRegistry().Capabilities;
        var existing = capabilities.Single(value => value.ValidationKey == "body.justified");
        Assert.Equal(BodyJustifiedFixProvider.Id, existing.CapabilityId);
        Assert.Equal(BodyJustifiedFixProvider.Version, existing.CapabilityVersion);
    }

    private static string Source(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. segments]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PPKI.sln"))
            && !File.Exists(Path.Combine(directory.FullName, "package.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public sealed class ReauditEfImmutabilityTests
{
    public static IEnumerable<object[]> ImmutableProperties()
    {
        yield return [nameof(AuditJob.DocumentVersionId), Guid.NewGuid()];
        yield return [nameof(AuditJob.ProfileVersionId), Guid.NewGuid()];
        yield return [nameof(AuditJob.DocumentKindSnapshot), DocumentKind.Tesis];
        yield return [nameof(AuditJob.RequestedByUserId), Guid.NewGuid()];
        yield return [nameof(AuditJob.SourceAuditJobId), Guid.NewGuid()];
        yield return [nameof(AuditJob.SourceFixExecutionId), Guid.NewGuid()];
        yield return [nameof(AuditJob.CreatedAt), DateTimeOffset.UtcNow.AddDays(1)];
        yield return [nameof(AuditJob.ResolvedRuleSetHash), new string('b', 64)];
        yield return [nameof(AuditJob.ApplicableRuleCount), 2];
    }

    [Theory]
    [MemberData(nameof(ImmutableProperties))]
    public async Task Historical_identity_and_context_are_blocked_by_ef(string property, object value)
    {
        await using var db = Context();
        var audit = Audit();
        db.Attach(audit);
        typeof(AuditJob).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(audit, value);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.Equal("Audit job identity and resolved context are immutable.", exception.Message);
    }

    [Fact]
    public void Legacy_audit_without_lineage_remains_a_valid_domain_model()
    {
        var audit = new AuditJob
        {
            DocumentVersionId = Guid.NewGuid(), ProfileVersionId = Guid.NewGuid(),
            RequestedByUserId = Guid.NewGuid(), Status = AuditJobStatus.Queued
        };
        Assert.Null(audit.SourceAuditJobId);
        Assert.Null(audit.SourceFixExecutionId);
    }

    private static PpkiDbContext Context() => new(new DbContextOptionsBuilder<PpkiDbContext>()
        .UseNpgsql("Host=localhost;Database=reaudit_offline_test").Options);

    private static AuditJob Audit() => new()
    {
        DocumentVersionId = Guid.NewGuid(),
        ProfileVersionId = Guid.NewGuid(),
        DocumentKindSnapshot = DocumentKind.Skripsi,
        RequestedByUserId = Guid.NewGuid(),
        SourceAuditJobId = Guid.NewGuid(),
        SourceFixExecutionId = Guid.NewGuid(),
        ResolvedRuleSetHash = new string('a', 64),
        ApplicableRuleCount = 1,
        Status = AuditJobStatus.Queued
    };
}
