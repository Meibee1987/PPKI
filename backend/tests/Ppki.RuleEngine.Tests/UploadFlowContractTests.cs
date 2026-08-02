using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class UploadFlowContractTests
{
    [Fact]
    public void Upload_builds_one_complete_version_after_storage_returns_metadata()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "services", "Ppki.Api", "Program.cs"));
        var upload = source.IndexOf("stored=await storage.SaveAsync", StringComparison.Ordinal);
        var completeVersion = source.IndexOf("var version=new DocumentVersion{Id=versionId", StringComparison.Ordinal);
        var insert = source.IndexOf("db.DocumentVersions.Add(version)", StringComparison.Ordinal);

        Assert.True(upload >= 0 && completeVersion > upload && insert > completeVersion);
        Assert.DoesNotContain("db.DocumentVersions.Update", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Sha256=string.Empty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageKey=string.Empty", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, "supabase", "migrations"))) return candidate.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
