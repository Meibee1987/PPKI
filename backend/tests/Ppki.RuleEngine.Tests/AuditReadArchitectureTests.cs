using Ppki.Application;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class AuditReadArchitectureTests
{
    [Fact]
    public void Endpoints_are_thin_authenticated_read_service_adapters()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");

        Assert.Contains("var api = app.MapGroup(\"/api\").RequireAuthorization()", api,
            StringComparison.Ordinal);
        Assert.Contains("IAuditReadService audits", api, StringComparison.Ordinal);
        Assert.Contains("AuditFindingQuery.TryCreate", api, StringComparison.Ordinal);
        Assert.Contains("/audits/{id:guid}/findings/{findingId:guid}", api,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AuditScoreCalculator(", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_service_has_no_live_rule_storage_or_document_parser_dependency()
    {
        var service = Source("backend", "src", "Ppki.Infrastructure", "AuditReadService.cs");

        Assert.DoesNotContain("RuleDefinition", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileStorage", service, StringComparison.Ordinal);
        Assert.DoesNotContain("IDocxParser", service, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerUserId == ownerUserId", service, StringComparison.Ordinal);
        Assert.Contains("AddEndpointFilter<InternalAdminEndpointFilter>()", Source("backend", "services", "Ppki.Api", "Program.cs"), StringComparison.Ordinal);
        Assert.Contains("AsNoTracking()", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyDefaultOrdering(boundedRows)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Take(AuditFindingQuery.MaximumFindingCount)\n            .ToListAsync",
            service.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("ApplyDatabaseOrdering(filtered)", service, StringComparison.Ordinal);
        Assert.Contains(".Skip(offset)", service, StringComparison.Ordinal);
        Assert.Contains(".Take(query.PageSize)", service, StringComparison.Ordinal);
        Assert.Contains("EF.Functions.ILike(value.RuleCode", service, StringComparison.Ordinal);
        Assert.Contains("EF.Functions.ILike(value.Element", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualJson, pattern", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedJson, pattern", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Calculator_has_no_time_random_or_implicit_production_policy()
    {
        var scoring = Source("backend", "src", "Ppki.Application", "AuditScoring.cs");
        var runner = Source("backend", "src", "Ppki.RuleEngine", "AuditRunner.cs");

        Assert.DoesNotContain("DateTime", scoring, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid.NewGuid", scoring, StringComparison.Ordinal);
        Assert.DoesNotContain("CalculateScore", runner, StringComparison.Ordinal);
        Assert.Contains("audit.Score = null", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_summary_keeps_score_not_configured_and_null()
    {
        var service = Source("backend", "src", "Ppki.Infrastructure", "AuditReadService.cs");
        var calculator = new AuditScoreCalculator();

        var result = calculator.Calculate(
            new(Ppki.Domain.AuditJobStatus.Completed, 1, []), policy: null);

        Assert.Contains("policy: null", service, StringComparison.Ordinal);
        Assert.Equal(AuditScoreState.NotConfigured, result.State);
        Assert.Null(result.Score);
    }

    [Fact]
    public void Public_read_contract_has_no_content_or_storage_fields()
    {
        var names = typeof(AuditSummaryDto).GetProperties()
            .Concat(typeof(AuditFindingListItemDto).GetProperties())
            .Concat(typeof(AuditFindingDetailDto).GetProperties())
            .Select(value => value.Name)
            .ToArray();

        Assert.DoesNotContain(names, value => value.Contains("Text", StringComparison.Ordinal));
        Assert.DoesNotContain(names, value => value.Contains("Filename", StringComparison.Ordinal));
        Assert.DoesNotContain(names, value => value.Contains("Storage", StringComparison.Ordinal));
        Assert.DoesNotContain(names, value => value.Contains("Url", StringComparison.Ordinal));
    }

    private static string Source(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. segments]));

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "package.json")))
                    return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
