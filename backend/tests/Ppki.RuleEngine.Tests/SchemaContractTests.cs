using Microsoft.EntityFrameworkCore;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class SchemaContractTests
{
    private const string MigrationName = "202608020001_ownership_integrity.sql";
    private const string RlsMigrationName = "202608020002_row_level_security.sql";

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

    private static string MigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations", MigrationName);

    private static string InitialMigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations", "202608010001_initial_schema.sql");

    private static string RlsMigrationPath() => Path.Combine(RepositoryRoot(), "supabase", "migrations", RlsMigrationName);

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
