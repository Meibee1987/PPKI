using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixPlanPreviewSelectionTests
{
    [Fact]
    public void Empty_selection_is_invalid()
    {
        Assert.False(FixPlanSelection.TryCreate([], out _, out var code));
        Assert.Equal("fix-plan-selection-empty", code);
    }

    [Fact]
    public void Selection_over_bound_is_invalid()
    {
        var ids = Enumerable.Range(1, FixPlanSelection.MaximumFindingCount + 1)
            .Select(value => FixPlanPreviewTestData.Id(value).ToString());

        Assert.False(FixPlanSelection.TryCreate(ids, out _, out var code));
        Assert.Equal("fix-plan-selection-too-large", code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Malformed_selection_id_is_invalid(string value)
    {
        Assert.False(FixPlanSelection.TryCreate([value], out _, out var code));
        Assert.Equal("fix-plan-selection-id-invalid", code);
    }

    [Fact]
    public void Duplicate_ids_are_normalized_and_sorted()
    {
        var first = FixPlanPreviewTestData.Id(1);
        var second = FixPlanPreviewTestData.Id(2);

        Assert.True(FixPlanSelection.TryCreate(
            [second.ToString(), first.ToString(), second.ToString()], out var selection, out var code));

        Assert.Null(code);
        Assert.Equal([first, second], selection.FindingIds);
    }
}

public sealed class FixPlanPreviewCapabilityTests
{
    [Fact]
    public void Duplicate_validation_key_is_rejected_with_controlled_code()
    {
        var exception = Assert.Throws<FixPlanConfigurationException>(() =>
            new RemediationCapabilityRegistry([
                FixPlanPreviewTestData.Capability("same-key"),
                FixPlanPreviewTestData.Capability("same-key", capabilityId: "second-capability")
            ]));

        Assert.Equal("fix-capability-validation-key-duplicate", exception.DiagnosticCode);
    }

    [Fact]
    public void Empty_capability_version_is_rejected_with_controlled_code()
    {
        var exception = Assert.Throws<FixPlanConfigurationException>(() =>
            new RemediationCapabilityRegistry([
                FixPlanPreviewTestData.Capability("test-key") with { CapabilityVersion = "" }
            ]));

        Assert.Equal("fix-capability-configuration-invalid", exception.DiagnosticCode);
    }

    [Fact]
    public void Registry_iteration_is_ordinal_and_deterministic()
    {
        var registry = new RemediationCapabilityRegistry([
            FixPlanPreviewTestData.Capability("z-key", capabilityId: "z-capability"),
            FixPlanPreviewTestData.Capability("a-key", capabilityId: "a-capability")
        ]);

        Assert.Equal(["a-key", "z-key"], registry.Capabilities.Select(value => value.ValidationKey));
    }

    [Theory]
    [InlineData(FixMode.Auto)]
    [InlineData(FixMode.Confirm)]
    public void Fix_mode_without_registered_capability_is_unsupported(FixMode fixMode)
    {
        var result = FixPlanPreviewTestData.Planner().Create(
            FixPlanPreviewTestData.Source([FixPlanPreviewTestData.Finding(1, fixMode: fixMode)]));

        Assert.Equal(FixPlanState.NotAvailable, result.State);
        var item = Assert.Single(result.Items);
        Assert.Equal(FixPlanItemDisposition.Unsupported, item.Disposition);
        Assert.Equal("fix-capability-not-registered", item.DiagnosticCode);
        Assert.Empty(result.Operations);
    }

    [Fact]
    public void Unknown_validation_key_is_unsupported_even_when_registry_is_not_empty()
    {
        var planner = FixPlanPreviewTestData.Planner(
            FixPlanPreviewTestData.Capability("known-key"));

        var result = planner.Create(FixPlanPreviewTestData.Source([
            FixPlanPreviewTestData.Finding(1, validationKey: "unknown-key")
        ]));

        Assert.Equal(FixPlanState.NotAvailable, result.State);
        Assert.Equal(FixPlanItemDisposition.Unsupported, Assert.Single(result.Items).Disposition);
    }
}

public sealed class FixPlanPreviewPlannerTests
{
    [Fact]
    public void Empty_registry_produces_not_available_with_correct_counts()
    {
        var result = FixPlanPreviewTestData.Planner().Create(FixPlanPreviewTestData.Source([
            FixPlanPreviewTestData.Finding(1),
            FixPlanPreviewTestData.Finding(2, validationKey: "other-key")
        ]));

        Assert.Equal(FixPlanState.NotAvailable, result.State);
        Assert.Equal(2, result.SelectedFindingCount);
        Assert.Equal(0, result.PlannedFindingCount);
        Assert.Equal(2, result.UnsupportedFindingCount);
        Assert.Equal(0, result.ConflictFindingCount);
        Assert.Equal(0, result.InvalidFindingCount);
        Assert.All(result.Items, value => Assert.Equal(FixPlanItemDisposition.Unsupported, value.Disposition));
    }

    [Fact]
    public void All_supported_non_conflicting_findings_produce_ready_plan()
    {
        var result = FixPlanPreviewTestData.SupportedPlanner().Create(FixPlanPreviewTestData.Source([
            FixPlanPreviewTestData.Finding(2, paragraphIndex: 2),
            FixPlanPreviewTestData.Finding(1, paragraphIndex: 1)
        ]));

        Assert.Equal(FixPlanState.Ready, result.State);
        Assert.Equal(2, result.PlannedFindingCount);
        Assert.Equal(2, result.Operations.Count);
        Assert.Equal([1, 2], result.Operations.Select(value => value.Ordinal));
        Assert.All(result.Operations, operation => Assert.Single(operation.SourceFindingIds));
    }

    [Fact]
    public void Supported_and_unsupported_findings_produce_partially_ready_plan()
    {
        var result = FixPlanPreviewTestData.SupportedPlanner().Create(FixPlanPreviewTestData.Source([
            FixPlanPreviewTestData.Finding(1),
            FixPlanPreviewTestData.Finding(2, validationKey: "unknown-key", paragraphIndex: 2)
        ]));

        Assert.Equal(FixPlanState.PartiallyReady, result.State);
        Assert.Equal(1, result.PlannedFindingCount);
        Assert.Equal(1, result.UnsupportedFindingCount);
        Assert.Single(result.Operations);
    }

    [Fact]
    public void Invalid_or_oversized_snapshot_json_is_bounded_and_rejected()
    {
        var invalid = FixPlanPreviewTestData.Finding(1) with { ExpectedJson = "not-json" };
        var oversized = FixPlanPreviewTestData.Finding(2) with
        {
            ActualJson = JsonSerializer.Serialize(new string('x', 16_384))
        };

        var invalidResult = FixPlanPreviewTestData.SupportedPlanner().Create(
            FixPlanPreviewTestData.Source([invalid]));
        var oversizedResult = FixPlanPreviewTestData.SupportedPlanner().Create(
            FixPlanPreviewTestData.Source([oversized]));

        Assert.Equal(FixPlanState.InvalidSnapshot, invalidResult.State);
        Assert.Equal(FixPlanState.InvalidSnapshot, oversizedResult.State);
        Assert.Equal("fix-plan-finding-snapshot-invalid", Assert.Single(invalidResult.Items).DiagnosticCode);
        Assert.Empty(oversizedResult.Operations);
    }

    [Fact]
    public void Audit_must_be_completed()
    {
        var source = FixPlanPreviewTestData.Source([FixPlanPreviewTestData.Finding(1)]) with
        {
            AuditStatus = AuditJobStatus.Processing
        };

        var result = FixPlanPreviewTestData.SupportedPlanner().Create(source);

        Assert.Equal(FixPlanState.AuditIncomplete, result.State);
        Assert.Equal(["fix-plan-audit-incomplete"], result.Diagnostics);
        Assert.Empty(result.Operations);
    }

    [Fact]
    public void Same_rule_at_different_locations_remains_two_operations()
    {
        var result = FixPlanPreviewTestData.SupportedPlanner().Create(FixPlanPreviewTestData.Source([
            FixPlanPreviewTestData.Finding(1, paragraphIndex: 10),
            FixPlanPreviewTestData.Finding(2, paragraphIndex: 11)
        ]));

        Assert.Equal(FixPlanState.Ready, result.State);
        Assert.Equal(2, result.Operations.Count);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Identical_operations_merge_only_when_capability_explicitly_allows_it()
    {
        var capabilities = new[]
        {
            FixPlanPreviewTestData.Capability("test-key", allowsMerge: true),
            FixPlanPreviewTestData.Capability("second-key", allowsMerge: true)
        };
        var result = FixPlanPreviewTestData.Planner(capabilities).Create(FixPlanPreviewTestData.Source([
            FixPlanPreviewTestData.Finding(1, validationKey: "test-key", ruleCode: "RULE-1"),
            FixPlanPreviewTestData.Finding(2, validationKey: "second-key", ruleCode: "RULE-2")
        ]));

        var operation = Assert.Single(result.Operations);
        Assert.Equal(FixPlanState.Ready, result.State);
        Assert.Equal([FixPlanPreviewTestData.Id(1), FixPlanPreviewTestData.Id(2)], operation.SourceFindingIds);
    }

    [Fact]
    public void Identical_operations_do_not_merge_without_explicit_permission()
    {
        var planner = FixPlanPreviewTestData.Planner(
            FixPlanPreviewTestData.Capability("test-key", allowsMerge: false));
        var result = planner.Create(FixPlanPreviewTestData.Source([
            FixPlanPreviewTestData.Finding(1),
            FixPlanPreviewTestData.Finding(2)
        ]));

        Assert.Equal(FixPlanState.Ready, result.State);
        Assert.Equal(2, result.Operations.Count);
    }

    [Fact]
    public void Different_expected_values_on_same_semantic_target_conflict_without_winner()
    {
        var first = FixPlanPreviewTestData.Finding(1, expectedValue: 10, ordinal: 99, severity: RuleSeverity.Info);
        var second = FixPlanPreviewTestData.Finding(2, expectedValue: 20, ordinal: 1, severity: RuleSeverity.Error,
            validationKey: "second-key", ruleCode: "RULE-2");
        var planner = FixPlanPreviewTestData.Planner(
            FixPlanPreviewTestData.Capability("test-key"),
            FixPlanPreviewTestData.Capability("second-key"));

        var result = planner.Create(FixPlanPreviewTestData.Source([first, second]));

        Assert.Equal(FixPlanState.Conflict, result.State);
        Assert.Equal(2, result.ConflictFindingCount);
        Assert.Empty(result.Operations);
        Assert.All(result.Items, item => Assert.Equal(FixPlanItemDisposition.Conflict, item.Disposition));
        Assert.Equal([first.FindingId, second.FindingId], Assert.Single(result.Conflicts).FindingIds);
    }

    [Fact]
    public void Repeated_reordered_and_parallel_generation_has_identical_hash()
    {
        var planner = FixPlanPreviewTestData.SupportedPlanner();
        var first = FixPlanPreviewTestData.Finding(1, paragraphIndex: 1);
        var second = FixPlanPreviewTestData.Finding(2, paragraphIndex: 2);
        var expected = planner.Create(FixPlanPreviewTestData.Source([first, second])).PlanHash;
        var reordered = planner.Create(FixPlanPreviewTestData.Source([second, first])).PlanHash;
        var parallel = Enumerable.Range(0, 16)
            .AsParallel()
            .Select(_ => planner.Create(FixPlanPreviewTestData.Source([second, first])).PlanHash)
            .ToArray();

        Assert.Equal(expected, reordered);
        Assert.All(parallel, value => Assert.Equal(expected, value));
        Assert.Matches("^[0-9a-f]{64}$", expected);
    }

    [Fact]
    public void Capability_expected_source_and_rule_set_versions_are_hash_inputs()
    {
        var finding = FixPlanPreviewTestData.Finding(1);
        var baseline = FixPlanPreviewTestData.SupportedPlanner("1.0")
            .Create(FixPlanPreviewTestData.Source([finding])).PlanHash;
        var capabilityChanged = FixPlanPreviewTestData.SupportedPlanner("2.0")
            .Create(FixPlanPreviewTestData.Source([finding])).PlanHash;
        var expectedChanged = FixPlanPreviewTestData.SupportedPlanner("1.0")
            .Create(FixPlanPreviewTestData.Source([finding with { ExpectedJson = "{\"value\":2}" }])).PlanHash;
        var sourceChanged = FixPlanPreviewTestData.SupportedPlanner("1.0")
            .Create(FixPlanPreviewTestData.Source([finding]) with { SourceVersionSha256 = new string('c', 64) }).PlanHash;
        var rulesChanged = FixPlanPreviewTestData.SupportedPlanner("1.0")
            .Create(FixPlanPreviewTestData.Source([finding]) with { ResolvedRuleSetHash = new string('d', 64) }).PlanHash;

        Assert.Equal(4, new[] { capabilityChanged, expectedChanged, sourceChanged, rulesChanged }.Distinct().Count());
        Assert.DoesNotContain(baseline, new[] { capabilityChanged, expectedChanged, sourceChanged, rulesChanged });
    }

    [Fact]
    public void Culture_does_not_change_operation_or_hash()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var planner = FixPlanPreviewTestData.SupportedPlanner();
            var source = FixPlanPreviewTestData.Source([FixPlanPreviewTestData.Finding(1)]);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("id-ID");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("id-ID");
            var indonesian = planner.Create(source);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var english = planner.Create(source);

            Assert.Equal(indonesian.PlanHash, english.PlanHash);
            Assert.Equal(JsonSerializer.Serialize(indonesian.Operations),
                JsonSerializer.Serialize(english.Operations));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Historical_plan_input_is_self_contained_and_uses_document_kind_snapshot()
    {
        var source = FixPlanPreviewTestData.Source([FixPlanPreviewTestData.Finding(1)]) with
        {
            DocumentKindSnapshot = DocumentKind.Tesis
        };
        var planner = FixPlanPreviewTestData.SupportedPlanner();

        var beforeUnrelatedLiveMutation = planner.Create(source);
        var afterUnrelatedLiveMutation = planner.Create(source);

        Assert.Equal("Tesis", beforeUnrelatedLiveMutation.DocumentKindSnapshot);
        Assert.Equal(source.ResolvedRuleSetHash, beforeUnrelatedLiveMutation.ResolvedRuleSetHash);
        Assert.Equal(beforeUnrelatedLiveMutation.PlanHash, afterUnrelatedLiveMutation.PlanHash);
    }

    [Fact]
    public void Public_contract_contains_no_content_storage_or_exception_fields()
    {
        var contractTypes = new[]
        {
            typeof(FixPlanPreview), typeof(FixPlanItem), typeof(FixPlanOperation),
            typeof(FixPlanConflict), typeof(FixTargetLocation), typeof(FixExpectedValueDescriptor)
        };
        var names = contractTypes.SelectMany(type => type.GetProperties()).Select(value => value.Name).ToArray();

        foreach (var forbidden in new[] { "Text", "Filename", "Storage", "Path", "Url", "Xml", "Stack", "Exception", "Bytes" })
            Assert.DoesNotContain(names, value => value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class FixPlanPreviewServiceTests
{
    [Fact]
    public async Task Missing_foreign_audit_or_finding_is_a_safe_null_result_and_token_is_forwarded()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new NullSourceReader(cancellation.Token);
        var service = new FixPlanPreviewService(reader, FixPlanPreviewTestData.Planner());
        var selection = new FixPlanSelection([FixPlanPreviewTestData.Id(1)]);

        var result = await service.PreviewAsync(FixPlanPreviewTestData.Id(10),
            FixPlanPreviewTestData.Id(11), selection, cancellation.Token);

        Assert.Null(result);
        Assert.True(reader.Called);
    }

    private sealed class NullSourceReader(CancellationToken expectedToken) : IFixPlanSourceReader
    {
        public bool Called { get; private set; }

        public Task<FixPlanSource?> LoadAsync(Guid auditId, Guid ownerUserId,
            FixPlanSelection selection, CancellationToken cancellationToken)
        {
            Called = true;
            Assert.Equal(expectedToken, cancellationToken);
            return Task.FromResult<FixPlanSource?>(null);
        }
    }
}

public sealed class FixPlanPreviewArchitectureTests
{
    [Fact]
    public void Queries_apply_shared_resource_identity_and_selection_before_materialization()
    {
        var options = new DbContextOptionsBuilder<PpkiDbContext>()
            .UseNpgsql("Host=localhost;Database=contract_only;Username=contract;Password=contract")
            .Options;
        using var db = new PpkiDbContext(options);
        var auditSql = FixPlanSourceQueries.OwnedAudit(db,
            FixPlanPreviewTestData.Id(1), FixPlanPreviewTestData.Id(2)).ToQueryString();
        var findingsSql = FixPlanSourceQueries.OwnedSelectedFindings(db,
            FixPlanPreviewTestData.Id(1), FixPlanPreviewTestData.Id(2), [FixPlanPreviewTestData.Id(3)])
            .ToQueryString();

        Assert.DoesNotContain("owner_user_id", auditSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner_user_id", findingsSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audit_rule_snapshots", findingsSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audit_findings", findingsSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ANY", findingsSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM rules", findingsSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_reader_is_read_only_snapshot_projection_without_storage_or_live_rules()
    {
        var source = Source("backend", "src", "Ppki.Infrastructure", "FixPlanSourceReader.cs");

        Assert.Contains("AsNoTracking()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerUserId == ownerUserId", source, StringComparison.Ordinal);
        Assert.Contains("findingIds.Contains(finding.Id)", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.ValidationKey", source, StringComparison.Ordinal);
        Assert.Contains("DocumentKindSnapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuleDefinition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentType", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_is_authenticated_thin_read_only_adapter_and_production_registry_is_explicit()
    {
        var api = Source("backend", "services", "Ppki.Api", "Program.cs");
        var routeStart = api.IndexOf("api.MapPost(\"/audits/{id:guid}/fix-plan-preview\"", StringComparison.Ordinal);
        var routeEnd = api.IndexOf("api.MapGet(\"/document-versions/", routeStart, StringComparison.Ordinal);
        var endpoint = api[routeStart..routeEnd];

        Assert.True(routeStart >= 0);
        Assert.Contains("MapGroup(\"/api\").RequireAuthorization()", api, StringComparison.Ordinal);
        Assert.Contains("FixPlanSelection.TryCreate", endpoint, StringComparison.Ordinal);
        Assert.Contains("previews.PreviewAsync", endpoint, StringComparison.Ordinal);
        Assert.Contains("CancellationToken ct", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileStorage", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("DeterministicFixPlanPreviewPlanner", endpoint, StringComparison.Ordinal);
        Assert.Contains("ProductionFixCapabilities.CreatePreviewRegistry()", api, StringComparison.Ordinal);
        Assert.DoesNotContain("/fix-plan-apply", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/apply-fix", api, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Planner_has_no_time_random_storage_docx_http_or_live_rule_dependency()
    {
        var planner = Source("backend", "src", "Ppki.FixEngine", "FixPlanPreviewPlanner.cs");
        var project = Source("backend", "src", "Ppki.FixEngine", "Ppki.FixEngine.csproj");
        var applicationProject = Source("backend", "src", "Ppki.Application", "Ppki.Application.csproj");

        foreach (var forbidden in new[] { "DateTime", "Guid.NewGuid", "IFileStorage", "HttpClient", "OpenXml", "RuleDefinition", "Ppki.Api", "Ppki.Infrastructure" })
            Assert.DoesNotContain(forbidden, planner, StringComparison.Ordinal);
        Assert.DoesNotContain("Ppki.Infrastructure", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Ppki.Api", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Ppki.Infrastructure", applicationProject, StringComparison.Ordinal);
    }

    [Fact]
    public void No_frontend_parser_rule_or_migration_dependency_was_added_to_feature_projects()
    {
        var fixProject = Source("backend", "src", "Ppki.FixEngine", "Ppki.FixEngine.csproj");
        var apiProject = Source("backend", "services", "Ppki.Api", "Ppki.Api.csproj");

        Assert.Contains("Ppki.DocxEngine", fixProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Ppki.RuleEngine", fixProject, StringComparison.Ordinal);
        Assert.DoesNotContain("apps/web", fixProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ppki.DocxEngine", apiProject, StringComparison.Ordinal);
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
                if (File.Exists(Path.Combine(current.FullName, "package.json"))) return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

internal static class FixPlanPreviewTestData
{
    public static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    public static DeterministicFixPlanPreviewPlanner Planner(params RemediationCapability[] capabilities) =>
        new(new RemediationCapabilityRegistry(capabilities));

    public static DeterministicFixPlanPreviewPlanner SupportedPlanner(string version = "1.0") =>
        Planner(Capability("test-key", version));

    public static RemediationCapability Capability(
        string validationKey,
        string version = "1.0",
        string capabilityId = "test-capability",
        bool allowsMerge = true) =>
        new(capabilityId, version, validationKey, FixOperationKind.SetProperty,
            ["expected.value", "location.scope", "location.paragraph-index"],
            RequiresConfirmation: true,
            DocumentMutationImplementationExists: false,
            PreviewProviderId: "test-preview-provider",
            DescriptionCode: "fix-test-property",
            AllowsIdenticalOperationMerge: allowsMerge,
            Provider: new TypedTestPreviewProvider());

    public static FixPlanSource Source(IReadOnlyList<FixPlanFindingSnapshot> findings) =>
        new(Id(900), AuditJobStatus.Completed, Id(901), new string('a', 64),
            new string('b', 64), DocumentKind.Skripsi, findings);

    public static FixPlanFindingSnapshot Finding(
        int id,
        string validationKey = "test-key",
        string ruleCode = "RULE-1",
        int paragraphIndex = 1,
        int expectedValue = 1,
        int? ordinal = null,
        RuleSeverity severity = RuleSeverity.Warning,
        FixMode fixMode = FixMode.Auto) =>
        new(Id(id), ordinal ?? id, ruleCode, "layout", "paragraph", validationKey,
            severity, fixMode, FindingStatus.Open,
            "{\"value\":0}", $"{{\"value\":{expectedValue.ToString(CultureInfo.InvariantCulture)}}}",
            $"{{\"scope\":\"paragraph\",\"paragraphIndex\":{paragraphIndex.ToString(CultureInfo.InvariantCulture)}}}", 1);

    private sealed class TypedTestPreviewProvider : IFixPreviewProvider
    {
        public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
        {
            operation = null!;
            diagnosticCode = "fix-preview-invalid-typed-snapshot";
            try
            {
                using var expectedDocument = JsonDocument.Parse(finding.ExpectedJson);
                using var locationDocument = JsonDocument.Parse(finding.LocationJson);
                var expected = expectedDocument.RootElement;
                var location = locationDocument.RootElement;
                if (expected.ValueKind != JsonValueKind.Object || expected.EnumerateObject().Count() != 1
                    || !expected.TryGetProperty("value", out var expectedValue)
                    || expectedValue.ValueKind != JsonValueKind.Number || !expectedValue.TryGetInt64(out var integer)
                    || location.ValueKind != JsonValueKind.Object || location.EnumerateObject().Count() != 2
                    || !location.TryGetProperty("scope", out var scope)
                    || scope.GetString() != "paragraph"
                    || !location.TryGetProperty("paragraphIndex", out var paragraph)
                    || !paragraph.TryGetInt32(out var paragraphIndex) || paragraphIndex < 0)
                    return false;

                operation = new(
                    new("paragraph", null, null, paragraphIndex, null),
                    "paragraph.test-property",
                    new("integer", integer.ToString(CultureInfo.InvariantCulture)),
                    "source-version-sha256-matches",
                    "fix-test-property");
                diagnosticCode = "fix-preview-typed-snapshot-valid";
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
