using System.Reflection;
using Ppki.DocxEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class DocxParserArchitectureTests
{
    [Fact]
    public void Docx_engine_has_no_database_or_supabase_dependency()
    {
        var references = typeof(OpenXmlDocxParser).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        Assert.DoesNotContain(references, name => name?.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(references, name => name?.Contains("Supabase", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(references, name => name?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Parsed_model_exposes_no_runtime_or_infrastructure_identifiers()
    {
        var forbidden = new[] { "Credential", "Password", "Secret", "Token", "SignedUrl", "StoragePath", "OwnerId", "UserId", "AuditJobId", "Timestamp", "CreatedAt", "Database" };
        var modelTypes = typeof(ParsedDocument).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(ParsedDocument).Namespace && type.Name.StartsWith("Parsed", StringComparison.Ordinal));
        foreach (var property in modelTypes.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)))
        {
            Assert.DoesNotContain(forbidden, value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Parser_and_worker_contract_remain_read_only_private_and_text_safe()
    {
        var root = RepositoryRoot();
        var parser = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "OpenXmlDocxParser.cs"));
        var runner = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.RuleEngine", "AuditRunner.cs"));
        var worker = File.ReadAllText(Path.Combine(root, "backend", "services", "Ppki.Worker", "QueuedAuditWorker.cs"));

        Assert.Contains("WordprocessingDocument.Open(filePath, false", parser, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Write", parser, StringComparison.Ordinal);
        Assert.Contains("docxParser.ParseAsync(filePath, cancellationToken)", runner, StringComparison.Ordinal);
        Assert.Contains("File.Delete(filePath)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("ParsedDocument parsedDocument", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("{DocumentText}", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_task_does_not_change_migrations_policies_or_endpoints()
    {
        var root = RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "Ppki.DocxEngine.csproj"));
        Assert.DoesNotContain("EntityFrameworkCore", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Supabase", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Formatting_resolver_has_no_network_database_worker_or_installed_font_dependency()
    {
        var root = RepositoryRoot();
        var resolver = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "OpenXmlFormattingResolver.cs"));
        var model = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "FormattingModels.cs"));
        var auditTrail = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Infrastructure", "AuditTrailWriter.cs"));

        foreach (var forbidden in new[]
        {
            "HttpClient", "WebRequest", "EntityFrameworkCore", "Supabase", "Ppki.Api", "Ppki.Worker",
            "InstalledFontCollection", "System.Drawing", "Console.Write", "DateTime.Now", "DateTimeOffset.Now",
            "Guid.NewGuid", "Random.Shared"
        })
        {
            Assert.DoesNotContain(forbidden, resolver, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, model, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("EffectiveFormatting", auditTrail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ParsedDocument", auditTrail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Numbering_resolver_and_outline_builder_are_engine_local_and_infrastructure_free()
    {
        var root = RepositoryRoot();
        var numbering = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "OpenXmlNumberingResolver.cs"));
        var outline = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "DocumentOutlineBuilder.cs"));
        var models = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "NumberingModels.cs"));
        var auditTrail = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Infrastructure", "AuditTrailWriter.cs"));

        foreach (var source in new[] { numbering, outline, models })
        {
            foreach (var forbidden in new[]
            {
                "HttpClient", "WebRequest", "EntityFrameworkCore", "Supabase", "Ppki.Api", "Ppki.Worker",
                "InstalledFontCollection", "System.Drawing", "Console.Write", "DateTime.Now", "DateTimeOffset.Now",
                "Guid.NewGuid", "Random.Shared"
            })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.DoesNotContain("DocumentOutline", auditTrail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ParsedHeading", auditTrail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NumberingLabel", auditTrail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Semantic_detector_is_engine_local_text_safe_and_infrastructure_free()
    {
        var root = RepositoryRoot();
        var detector = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "SemanticDocumentStructureDetector.cs"));
        var models = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.DocxEngine", "SemanticSectionModels.cs"));
        var auditTrail = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.Infrastructure", "AuditTrailWriter.cs"));

        foreach (var source in new[] { detector, models })
        {
            foreach (var forbidden in new[]
            {
                "HttpClient", "WebRequest", "EntityFrameworkCore", "Supabase", "Ppki.Api", "Ppki.Worker",
                "System.Net", "MachineLearning", "OpenAI", "Console.Write", "DateTime.Now", "DateTimeOffset.Now",
                "Guid.NewGuid", "Random.Shared", "ILogger"
            })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.DoesNotContain("SemanticDocumentStructure", auditTrail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DocumentSystematics", auditTrail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AbstractSectionDescriptor", auditTrail, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
