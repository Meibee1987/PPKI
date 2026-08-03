using Ppki.DocxEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class Wave1ValidatorArchitectureTests
{
    [Fact]
    public void Structural_validators_are_rule_engine_local_private_and_dependency_free()
    {
        var root = RepositoryRoot();
        foreach (var file in new[]
        {
            "StructuralValidationSupport.cs", "HeadingValidators.cs", "AbstractValidators.cs"
        })
        {
            var source = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.RuleEngine", file));
            foreach (var forbidden in new[]
            {
                "HttpClient", "WebRequest", "Supabase", "Microsoft.EntityFrameworkCore", "PpkiDbContext",
                "WordprocessingDocument", "LibreOffice", "InstalledFontCollection", "System.Drawing",
                "DateTime.Now", "DateTimeOffset.Now", "Guid.NewGuid", "Random.Shared", "Console.Write",
                "ILogger", "OpenAI", "NLP", "FixPlan"
            })
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("paragraph.Text", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Console", source, StringComparison.Ordinal);
        }

        var parserProject = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "Ppki.DocxEngine.csproj"));
        Assert.DoesNotContain("Ppki.RuleEngine", parserProject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("4.0", OpenXmlDocxParser.SchemaVersion);
    }

    [Fact]
    public void Audit_runner_uses_persisted_snapshots_document_context_and_existing_mapper_lifecycle()
    {
        var root = RepositoryRoot();
        var runner = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.RuleEngine", "AuditRunner.cs"));
        Assert.Contains("EnsureRuleSnapshotsAsync", runner, StringComparison.Ordinal);
        Assert.Contains("validationEngine.Validate(parsed, snapshots, documentKind, cancellationToken)", runner, StringComparison.Ordinal);
        Assert.Contains("AuditFindingMapper.Map(audit.Id, validation)", runner, StringComparison.Ordinal);
        Assert.Contains("audit.DocumentKindSnapshot", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentType", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("rules.json", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ParsedDocumentCanonicalProjection", runner, StringComparison.Ordinal);

        var mapper = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.RuleEngine", "AuditFindingMapper.cs"));
        Assert.DoesNotContain("Paragraph.Text", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("TextSegments", mapper, StringComparison.Ordinal);
        var auditTrail = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Infrastructure", "AuditTrailWriter.cs"));
        Assert.DoesNotContain("ActualValueJson", auditTrail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExpectedValueJson", auditTrail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_structural_validator_is_placed_in_api_or_fix_engine()
    {
        var root = RepositoryRoot();
        foreach (var directory in new[]
        {
            Path.Combine(root, "backend", "services", "Ppki.Api"),
            Path.Combine(root, "backend", "src", "Ppki.FixEngine")
        })
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                var source = File.ReadAllText(file);
                Assert.DoesNotContain("IDocumentRuleValidator", source, StringComparison.Ordinal);
                Assert.DoesNotContain("StructuralValidatorLimits", source, StringComparison.Ordinal);
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
