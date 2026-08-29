using Ppki.DocxEngine;
using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class LayoutValidatorArchitectureTests
{
    [Fact]
    public void Validators_are_engine_local_infrastructure_free_and_audit_runner_uses_snapshots()
    {
        var root = RepositoryRoot();
        var paths = new[]
        {
            "RuleContracts.cs", "LayoutValidationSupport.cs", "SectionValidators.cs",
            "ParagraphValidators.cs", "RunValidators.cs"
        };
        foreach (var path in paths)
        {
            var source = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.RuleEngine", path));
            foreach (var forbidden in new[]
            {
                "HttpClient", "WebRequest", "Supabase", "Ppki.Api", "Ppki.Worker", "Microsoft.EntityFrameworkCore",
                "WordprocessingDocument", "LibreOffice", "InstalledFontCollection", "System.Drawing",
                "DateTime.Now", "DateTimeOffset.Now", "Guid.NewGuid", "Random.Shared", "Console.Write", "ILogger"
            })
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }

        var runner = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.RuleEngine", "AuditRunner.cs"));
        Assert.Contains("validationEngine.Validate(parsed, snapshots, documentKind, cancellationToken)", runner, StringComparison.Ordinal);
        Assert.Contains("AuditFindingMapper.Map(audit.Id, validation)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleFromSnapshot", runner, StringComparison.Ordinal);
        Assert.Contains("Resolved rule validation is unsupported or invalid.", runner, StringComparison.Ordinal);
        var auditTrail = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Infrastructure", "AuditTrailWriter.cs"));
        Assert.DoesNotContain("ActualValueJson", auditTrail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExpectedValueJson", auditTrail, StringComparison.OrdinalIgnoreCase);

        var parserProject = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "Ppki.DocxEngine.csproj"));
        Assert.DoesNotContain("Ppki.RuleEngine", parserProject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("4.0", OpenXmlDocxParser.SchemaVersion);

        var worker = File.ReadAllText(Path.Combine(root, "backend", "services", "Ppki.Worker", "Program.cs"));
        Assert.Contains("ProductionDocumentValidators.Create()", worker, StringComparison.Ordinal);
        Assert.Equal(25, ProductionDocumentValidators.Create().Count);
        Assert.Contains("AddSingleton<DocumentRuleValidatorRegistry>()", worker, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<DocumentLayoutValidationEngine>()", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave1_does_not_add_deferred_validator_types()
    {
        var names = typeof(DocumentLayoutValidationEngine).Assembly.GetTypes().Select(value => value.Name).ToArray();
        foreach (var deferred in new[] { "SystematicsValidator", "TableOfContentsValidator", "AutoFixValidator" })
            Assert.DoesNotContain(deferred, names, StringComparer.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

}
