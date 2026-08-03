using System.Text.Json;
using Ppki.DocxEngine;
using Ppki.RuleEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class LayoutValidatorIntegrationTests
{
    [Fact]
    public async Task Compliant_fixture_has_no_supported_findings_and_invalid_fixture_has_expected_set()
    {
        var snapshots = LayoutValidatorTestData.DefaultSnapshots();
        var compliant = LayoutValidatorTestData.Engine().Validate(
            await LayoutValidatorTestData.ParseFixture("minimal-compliant-layout"), snapshots, CancellationToken.None);
        Assert.Empty(compliant.Findings);
        Assert.All(compliant.Outcomes, value => Assert.Equal(ValidationApplicability.Applicable, value.Result.Applicability));

        var invalid = LayoutValidatorTestData.Engine().Validate(
            await LayoutValidatorTestData.ParseFixture("minimal-invalid-layout"), snapshots, CancellationToken.None);
        Assert.Equal(11, invalid.Findings.Count);
        Assert.Equal([
            "pageSize", "font.ascii", "font.highAnsi", "fontSize",
            "marginLeft", "marginRight", "marginTop", "marginBottom",
            "lineSpacingValue", "firstLineIndent", "alignment"
        ], invalid.Findings.Select(value => value.Finding.Actual.Property));
    }

    [Fact]
    public void Finding_contract_is_text_and_path_safe_and_expected_is_snapshot_key_contract()
    {
        const string marker = "SENSITIVE-SYNTHETIC-LAYOUT-MARKER";
        var run = LayoutValidatorTestData.Run(ascii: "WrongFont", highAnsi: "WrongFont", size: 20);
        run = run with { TextSegments = [marker] };
        var paragraph = LayoutValidatorTestData.Paragraph(runs: [run]);
        var snapshot = LayoutValidatorTestData.Snapshot("body.font-times-new-roman-12");
        var result = LayoutValidatorTestData.Engine().Validate(new ParsedDocument([], [paragraph]), [snapshot], CancellationToken.None);
        var serialized = JsonSerializer.Serialize(result.Findings);
        Assert.DoesNotContain(marker, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(".docx", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Findings, value =>
        {
            Assert.Equal(snapshot.ValidationKey, value.Finding.Expected.ValidationKey);
            Assert.Equal("resolved-snapshot-validation-key", value.Finding.Expected.ContractSource);
            Assert.Contains("maindocument", value.Finding.Location.CompactLocation, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Ordering_deduplication_limit_repeat_and_parallel_projection_are_deterministic()
    {
        var document = await LayoutValidatorTestData.ParseFixture("minimal-invalid-layout");
        var snapshots = LayoutValidatorTestData.DefaultSnapshots().Reverse().ToArray();
        var engine = LayoutValidatorTestData.Engine();
        var first = engine.Validate(document, snapshots, CancellationToken.None);
        var second = engine.Validate(document, snapshots, CancellationToken.None);
        Assert.Equal(LayoutFindingCanonicalProjection.Serialize(first), LayoutFindingCanonicalProjection.Serialize(second));
        Assert.Equal(LayoutFindingCanonicalProjection.Sha256(first), LayoutFindingCanonicalProjection.Sha256(second));
        Assert.Equal(first.Findings.Select(value => value.Snapshot.Ordinal), first.Findings.Select(value => value.Snapshot.Ordinal).Order());

        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            engine.Validate(document, snapshots, CancellationToken.None))));
        Assert.Single(parallel.Select(LayoutFindingCanonicalProjection.Sha256).Distinct(StringComparer.Ordinal));

        var duplicateSnapshot = LayoutValidatorTestData.Snapshot("section.page-size-a4", ruleCode: "DUPLICATE-RULE");
        var duplicate = engine.Validate(document, [duplicateSnapshot, duplicateSnapshot], CancellationToken.None);
        Assert.Equal(2, duplicate.Outcomes.Count);
        Assert.Single(duplicate.Findings);

        var limited = LayoutValidatorTestData.Engine(1).Validate(document,
            LayoutValidatorTestData.DefaultSnapshots(), CancellationToken.None);
        Assert.Single(limited.Findings);
        Assert.True(limited.FindingsTruncated);
        Assert.Equal("pageSize", limited.Findings[0].Finding.Actual.Property);
    }

    [Fact]
    public async Task Validation_uses_immutable_snapshot_parameters_and_not_live_rule_objects()
    {
        var document = await LayoutValidatorTestData.ParseFixture("minimal-compliant-layout");
        var strictSnapshot = LayoutValidatorTestData.Snapshot("section.page-size-a4",
            validationJson: "{\"width\":21,\"height\":29.7,\"unit\":\"cm\"}");
        var first = LayoutValidatorTestData.Engine().Validate(document, [strictSnapshot], CancellationToken.None);

        var unrelatedLiveRule = new Ppki.Domain.RuleDefinition
        {
            RuleCode = strictSnapshot.RuleCode,
            Domain = "LAY",
            AppliesTo = "Semua",
            Element = "Changed live value",
            OfficialRequirement = "Letter",
            ExpectedValuePattern = "Letter",
            ValidationKey = "unknown.changed-live-key",
            IsImplemented = true
        };
        Assert.NotEqual(strictSnapshot.ValidationKey, unrelatedLiveRule.ValidationKey);
        var second = LayoutValidatorTestData.Engine().Validate(document, [strictSnapshot], CancellationToken.None);
        Assert.Equal(LayoutFindingCanonicalProjection.Serialize(first), LayoutFindingCanonicalProjection.Serialize(second));
    }

    [Fact]
    public async Task Fixture_original_checksum_remains_unchanged()
    {
        await using var workspace = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        var before = await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath);
        var parsed = await new OpenXmlDocxParser().ParseAsync(workspace.WorkingPath, CancellationToken.None);
        _ = LayoutValidatorTestData.Engine().Validate(parsed, LayoutValidatorTestData.DefaultSnapshots(), CancellationToken.None);
        Assert.Equal(before, await DocxFixtureWorkspace.ComputeSha256Async(workspace.OriginalPath));
    }

    [Fact]
    public async Task Audit_finding_mapper_uses_snapshot_fields_and_safe_candidate_payloads()
    {
        var document = await LayoutValidatorTestData.ParseFixture("minimal-invalid-layout");
        var snapshot = LayoutValidatorTestData.Snapshot("section.page-size-a4", ruleCode: "SNAPSHOT-RULE");
        var validation = LayoutValidatorTestData.Engine().Validate(document, [snapshot], CancellationToken.None);
        var auditId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var finding = Assert.Single(AuditFindingMapper.Map(auditId, validation));
        Assert.Equal(auditId, finding.AuditJobId);
        Assert.Equal(snapshot.RuleId, finding.RuleId);
        Assert.Equal(snapshot.RuleCode, finding.RuleCodeSnapshot);
        Assert.Equal(snapshot.Severity, finding.Severity);
        Assert.Equal(snapshot.FixMode, finding.FixModeSnapshot);
        Assert.Equal("Synthetic source", finding.SourceSectionSnapshot);
        Assert.Equal(1, finding.PdfPageSnapshot);
        Assert.Equal("1", finding.PrintedPageSnapshot);
        Assert.Contains("pageSize", finding.ActualValueJson, StringComparison.Ordinal);
        Assert.Contains(snapshot.ValidationKey, finding.ExpectedValueJson, StringComparison.Ordinal);
        Assert.Contains("maindocument", finding.LocationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Paragraf sintetis", finding.ActualValueJson, StringComparison.Ordinal);
    }
}
