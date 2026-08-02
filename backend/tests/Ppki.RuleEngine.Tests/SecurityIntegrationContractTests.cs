using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class SecurityIntegrationContractTests
{
    [Fact]
    public void Harness_is_local_only_process_based_and_has_bounded_health_waits()
    {
        var source = Harness();

        Assert.Contains("supabase\", \"status\", \"-o\", \"env", source, StringComparison.Ordinal);
        Assert.Contains("isLocalHost", source, StringComparison.Ordinal);
        Assert.Contains("Ppki.Api.dll", source, StringComparison.Ordinal);
        Assert.Contains("Ppki.Worker.dll", source, StringComparison.Ordinal);
        Assert.Contains("/health/live", source, StringComparison.Ordinal);
        Assert.Contains("/health/ready", source, StringComparison.Ordinal);
        Assert.DoesNotContain("supabase\", \"start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("supabase\", \"db\", \"reset", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_covers_identities_faults_cleanup_hygiene_and_component_regressions()
    {
        var source = Harness();

        Assert.Contains("user-a@example.invalid", source, StringComparison.Ordinal);
        Assert.Contains("user-b@example.invalid", source, StringComparison.Ordinal);
        Assert.Contains("s1t06_fail_document_insert", source, StringComparison.Ordinal);
        Assert.Contains("s1t06_fail_snapshot_insert", source, StringComparison.Ordinal);
        Assert.Contains("s1t06_fail_finding_insert", source, StringComparison.Ordinal);
        Assert.Contains("s1t06_pause_snapshot_insert", source, StringComparison.Ordinal);
        Assert.Contains("scanLogsAndResponses", source, StringComparison.Ordinal);
        Assert.Contains("cleanupSynthetic", source, StringComparison.Ordinal);
        Assert.Contains("test:rls-local", source, StringComparison.Ordinal);
        Assert.Contains("test:storage-local", source, StringComparison.Ordinal);
        Assert.Contains("test:immutability-local", source, StringComparison.Ordinal);
        Assert.Contains("test:audit-trail-local", source, StringComparison.Ordinal);
        Assert.Contains("original-storage-object-unchanged-after-runtime-mutations", source, StringComparison.Ordinal);

        var storage = File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "src", "Ppki.Infrastructure", "SupabaseFileStorage.cs"));
        Assert.Contains("\"x-upsert\", \"false\"", storage, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_summary_is_ignored_and_contains_no_identity_fields_by_contract()
    {
        var root = RepositoryRoot();
        var source = Harness();
        var ignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        var summary = source[source.IndexOf("async function writeSummary", StringComparison.Ordinal)..source.IndexOf("async function main", StringComparison.Ordinal)];

        Assert.Contains("artifacts/security-integration-summary.json", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("email:", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password:", summary, StringComparison.OrdinalIgnoreCase);
    }

    private static string Harness() => File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "security-integration-test.mjs"));

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, "supabase", "migrations"))) return candidate.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
