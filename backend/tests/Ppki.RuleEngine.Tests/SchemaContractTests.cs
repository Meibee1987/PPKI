using Microsoft.EntityFrameworkCore;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class SchemaContractTests
{
    private const string MigrationName = "202608020001_ownership_integrity.sql";
    private const string RlsMigrationName = "202608020002_row_level_security.sql";
    private const string StorageMigrationName = "202608020003_storage_security.sql";
    private const string ImmutabilityMigrationName = "202608020004_audit_immutability.sql";

    [Fact]
    public void Ownership_migration_is_present_and_contains_no_hosted_credentials()
    {
        var path = MigrationPath();
        var sql = File.ReadAllText(path);

        Assert.EndsWith(MigrationName, path, StringComparison.Ordinal);
        Assert.NotEmpty(sql.Trim());
        Assert.DoesNotContain("supabase.co", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service_role", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sb_secret_", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ownership_migration_declares_required_postgresql_guards()
    {
        var sql = File.ReadAllText(MigrationPath());

        Assert.Contains("requested_by_user_id uuid", sql, StringComparison.Ordinal);
        Assert.Contains("unique(document_id,version_no)", File.ReadAllText(InitialMigrationPath()), StringComparison.Ordinal);
        Assert.Contains("ck_document_versions_sha256_lowercase", sql, StringComparison.Ordinal);
        Assert.Contains("^[0-9a-f]{64}$", sql, StringComparison.Ordinal);
        Assert.Contains("ck_document_versions_size_bytes_positive", sql, StringComparison.Ordinal);
        Assert.Contains("ck_document_versions_storage_key_safe", sql, StringComparison.Ordinal);
        Assert.Contains("storage_key !~ '^/'", sql, StringComparison.Ordinal);
        Assert.Contains("storage_key !~ '(^|/)\\.\\.(/|$)'", sql, StringComparison.Ordinal);
        Assert.Contains("position('://' in storage_key) = 0", sql, StringComparison.Ordinal);
        Assert.Contains("ck_audit_jobs_status", sql, StringComparison.Ordinal);
        Assert.Contains("ck_audit_findings_severity", sql, StringComparison.Ordinal);
        Assert.Contains("fix_mode_snapshot in ('Auto', 'Confirm', 'Manual', 'Report')", sql, StringComparison.Ordinal);
        Assert.Contains("unique(profile_version_id, rule_id)", sql, StringComparison.Ordinal);
        Assert.Contains("enforce_document_version_parent_document", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Ef_model_matches_the_primary_ownership_chain_without_connecting_to_a_database()
    {
        var options = new DbContextOptionsBuilder<PpkiDbContext>()
            .UseNpgsql("Host=localhost;Database=ppki_contract")
            .Options;
        using var db = new PpkiDbContext(options);

        var document = db.Model.FindEntityType(typeof(DocumentRecord))!;
        Assert.False(document.FindProperty(nameof(DocumentRecord.OwnerUserId))!.IsNullable);
        Assert.Contains(document.GetIndexes(), index => index.Properties.Select(x => x.Name).SequenceEqual([nameof(DocumentRecord.OwnerUserId)]));

        var version = db.Model.FindEntityType(typeof(DocumentVersion))!;
        Assert.Contains(version.GetIndexes(), index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual([nameof(DocumentVersion.DocumentId), nameof(DocumentVersion.VersionNo)]));
        Assert.Contains(version.GetForeignKeys(), key => key.Properties.Select(x => x.Name).SequenceEqual([nameof(DocumentVersion.DocumentId)]) && key.IsRequired);
        Assert.Contains(version.GetForeignKeys(), key => key.Properties.Select(x => x.Name).SequenceEqual([nameof(DocumentVersion.DocumentId)]) && key.DeleteBehavior == DeleteBehavior.Restrict);

        var audit = db.Model.FindEntityType(typeof(AuditJob))!;
        Assert.False(audit.FindProperty(nameof(AuditJob.DocumentVersionId))!.IsNullable);
        Assert.False(audit.FindProperty(nameof(AuditJob.ProfileVersionId))!.IsNullable);
        Assert.NotNull(audit.FindProperty(nameof(AuditJob.RequestedByUserId)));

        var finding = db.Model.FindEntityType(typeof(AuditFinding))!;
        Assert.False(finding.FindProperty(nameof(AuditFinding.AuditJobId))!.IsNullable);
        Assert.NotNull(finding.FindProperty(nameof(AuditFinding.RuleCodeSnapshot)));
        Assert.NotNull(finding.FindProperty(nameof(AuditFinding.FixModeSnapshot)));
        Assert.NotNull(finding.FindProperty(nameof(AuditFinding.PrintedPageSnapshot)));

        var profileRule = db.Model.FindEntityType(typeof(ProfileRule))!;
        Assert.Contains(profileRule.GetIndexes(), index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual([nameof(ProfileRule.ProfileVersionId), nameof(ProfileRule.RuleId)]));

        var snapshot = db.Model.FindEntityType(typeof(AuditRuleSnapshot))!;
        Assert.Contains(snapshot.GetIndexes(), index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual([nameof(AuditRuleSnapshot.AuditJobId), nameof(AuditRuleSnapshot.RuleCode)]));
        Assert.Contains(snapshot.GetForeignKeys(), key => key.Properties.Select(x => x.Name).SequenceEqual([nameof(AuditRuleSnapshot.AuditJobId)]) && key.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(finding.GetForeignKeys(), key => key.Properties.Select(x => x.Name).SequenceEqual([nameof(AuditFinding.AuditJobId)]) && key.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Fact]
    public void Rls_migration_declares_least_privilege_policies_without_storage_or_hosted_access()
    {
        var sql = File.ReadAllText(RlsMigrationPath());
        var applicationTables = new[]
        {
            "user_profiles", "document_types", "formatting_profiles", "profile_versions", "profile_rules",
            "rules", "documents", "document_versions", "audit_jobs", "audit_findings"
        };

        Assert.NotEmpty(sql.Trim());
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"create\s+policy\s+\w*storage", System.Text.RegularExpressions.RegexOptions.IgnoreCase), sql);
        Assert.DoesNotContain("supabase.co", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service_role", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sb_secret_", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("force row level security", sql, StringComparison.OrdinalIgnoreCase);

        foreach (var table in applicationTables)
        {
            Assert.Contains($"alter table public.{table} enable row level security", sql, StringComparison.Ordinal);
            Assert.Contains($"revoke all on table public.{table} from anon, authenticated", sql, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("to anon", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("using (true)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("documents_select_own", sql, StringComparison.Ordinal);
        Assert.Contains("owner_user_id = (select auth.uid())", sql, StringComparison.Ordinal);
        Assert.Contains("document_versions_select_owned_document", sql, StringComparison.Ordinal);
        Assert.Contains("audit_jobs_select_owned_document", sql, StringComparison.Ordinal);
        Assert.Contains("audit_findings_select_owned_document", sql, StringComparison.Ordinal);
        Assert.Contains("grant select on table public.document_types to authenticated", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("grant select on table public.rules to authenticated", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("grant insert", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant update", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant delete", sql, StringComparison.OrdinalIgnoreCase);

        var policyNames = System.Text.RegularExpressions.Regex.Matches(sql, @"create policy\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Equal(policyNames.Length, policyNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Storage_migration_keeps_exact_buckets_private_and_browser_roles_denied()
    {
        var sql = File.ReadAllText(StorageMigrationPath());

        Assert.NotEmpty(sql.Trim());
        Assert.Contains("'documents-original'", sql, StringComparison.Ordinal);
        Assert.Contains("'documents-versions'", sql, StringComparison.Ordinal);
        Assert.Contains("'audit-reports'", sql, StringComparison.Ordinal);
        Assert.Contains("public = false", sql, StringComparison.Ordinal);
        Assert.Contains("52428800", sql, StringComparison.Ordinal);
        Assert.Contains("application/vnd.openxmlformats-officedocument.wordprocessingml.document", sql, StringComparison.Ordinal);
        Assert.Contains("application/pdf", sql, StringComparison.Ordinal);
        Assert.Contains("application/json", sql, StringComparison.Ordinal);
        Assert.Contains("revoke all on table storage.objects from anon, authenticated", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("create policy", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supabase.co", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sb_secret_", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Immutability_migration_enforces_history_and_state_without_runtime_bypass()
    {
        var sql = File.ReadAllText(ImmutabilityMigrationPath());

        Assert.Contains("trg_document_versions_reject_update", sql, StringComparison.Ordinal);
        Assert.Contains("trg_document_versions_reject_delete", sql, StringComparison.Ordinal);
        Assert.Contains("reject_document_version_mutation", sql, StringComparison.Ordinal);
        Assert.Contains("current_user = relation_owner", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("current_setting", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request.jwt", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Terminal audit job is immutable", sql, StringComparison.Ordinal);
        Assert.Contains("Invalid audit job state transition", sql, StringComparison.Ordinal);
        Assert.Contains("old.status = 'Queued' and new.status in ('Processing', 'Cancelled')", sql, StringComparison.Ordinal);
        Assert.Contains("old.status = 'Processing' and new.status in ('Completed', 'Failed', 'Cancelled')", sql, StringComparison.Ordinal);
        Assert.Contains("trg_audit_jobs_reject_delete", sql, StringComparison.Ordinal);
        Assert.Contains("trg_audit_findings_enforce_insert", sql, StringComparison.Ordinal);
        Assert.Contains("terminal audit finding", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("on delete restrict", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Immutability_migration_defines_insert_only_owned_rule_snapshots()
    {
        var sql = File.ReadAllText(ImmutabilityMigrationPath());

        Assert.Contains("create table public.audit_rule_snapshots", sql, StringComparison.Ordinal);
        Assert.Contains("unique (audit_job_id, rule_code)", sql, StringComparison.Ordinal);
        Assert.Contains("unique (audit_job_id, ordinal)", sql, StringComparison.Ordinal);
        Assert.Contains("ix_audit_rule_snapshots_audit_job", sql, StringComparison.Ordinal);
        Assert.Contains("snapshot_schema_version", sql, StringComparison.Ordinal);
        Assert.Contains("requirement_json jsonb not null", sql, StringComparison.Ordinal);
        Assert.Contains("validation_json jsonb not null", sql, StringComparison.Ordinal);
        Assert.Contains("source_reference_json jsonb not null", sql, StringComparison.Ordinal);
        Assert.Contains("trg_audit_rule_snapshots_reject_update", sql, StringComparison.Ordinal);
        Assert.Contains("trg_audit_rule_snapshots_reject_delete", sql, StringComparison.Ordinal);
        Assert.Contains("alter table public.audit_rule_snapshots enable row level security", sql, StringComparison.Ordinal);
        Assert.Contains("audit_rule_snapshots_select_owned_document", sql, StringComparison.Ordinal);
        Assert.Contains("document.owner_user_id = (select auth.uid())", sql, StringComparison.Ordinal);
        Assert.Contains("grant select on table public.audit_rule_snapshots to authenticated", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("grant insert on table public.audit_rule_snapshots to authenticated", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage.objects", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage.buckets", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supabase.co", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Immutability_migration_requires_snapshot_count_and_lowercase_sha256_hash()
    {
        var sql = File.ReadAllText(ImmutabilityMigrationPath());

        Assert.Contains("applicable_rule_count", sql, StringComparison.Ordinal);
        Assert.Contains("^[0-9a-f]{64}$", sql, StringComparison.Ordinal);
        Assert.Contains("snapshot_count <> new.applicable_rule_count", sql, StringComparison.Ordinal);
        Assert.Contains("Completed audit job requires a rule snapshot hash", sql, StringComparison.Ordinal);
        Assert.Contains("existing non-queued audits require offline remediation", sql, StringComparison.Ordinal);
    }

    private static string MigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations", MigrationName);

    private static string InitialMigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations", "202608010001_initial_schema.sql");

    private static string RlsMigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations", RlsMigrationName);

    private static string StorageMigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations", StorageMigrationName);

    private static string ImmutabilityMigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations", ImmutabilityMigrationName);

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var candidate = new DirectoryInfo(start); candidate is not null; candidate = candidate.Parent)
            {
                if (Directory.Exists(Path.Combine(candidate.FullName, "supabase", "migrations")))
                {
                    return candidate.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Repository root with Supabase migrations was not found.");
    }
}
