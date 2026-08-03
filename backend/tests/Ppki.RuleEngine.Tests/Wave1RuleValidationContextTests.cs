using Ppki.DocxEngine;
using Ppki.Domain;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class Wave1RuleValidationContextTests
{
    [Fact]
    public void Layout_rule_with_null_document_kind_remains_applicable_through_legacy_overload()
    {
        var document = new ParsedDocument(
            [LayoutValidatorTestData.Section(11906, 16838, 1701, 1701, 1701, 2268)], []);
        var snapshot = LayoutValidatorTestData.Snapshot("section.page-size-a4");

        var result = LayoutValidatorTestData.Engine().Validate(document, [snapshot], CancellationToken.None);

        Assert.Equal(ValidationApplicability.Applicable, result.Outcomes[0].Result.Applicability);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Structural_rule_with_null_document_kind_is_safe_invalid_configuration()
    {
        var snapshot = Wave1ValidatorTestData.Snapshot(
            "abstract.skripsi-language-pair", appliesTo: "Skripsi", domain: "ABS");

        var exception = Record.Exception(() => Wave1ValidatorTestData.Engine().Validate(
            new ParsedDocument([], []), [snapshot], CancellationToken.None));
        var result = Wave1ValidatorTestData.Engine().Validate(
            new ParsedDocument([], []), [snapshot], CancellationToken.None);

        Assert.Null(exception);
        Assert.Equal(ValidationApplicability.InvalidRuleConfiguration, result.Outcomes[0].Result.Applicability);
        Assert.Equal("document-kind-required", result.Outcomes[0].Result.DiagnosticCode);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Explicit_and_legacy_overloads_forward_document_kind_without_defaulting_null()
    {
        var probe = new DocumentKindProbeValidator();
        var engine = new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry([probe]));
        var snapshot = Wave1ValidatorTestData.Snapshot(probe.ValidationKey);
        var document = new ParsedDocument([], []);

        engine.Validate(document, [snapshot], DocumentKind.Disertasi, CancellationToken.None);
        Assert.Equal(DocumentKind.Disertasi, probe.ObservedKind);

        engine.Validate(document, [snapshot], CancellationToken.None);
        Assert.Null(probe.ObservedKind);
    }

    [Fact]
    public void Audit_runner_does_not_throw_for_missing_document_type_before_validation()
    {
        var runner = File.ReadAllText(Path.Combine(RepositoryRoot(),
            "backend", "src", "Ppki.RuleEngine", "AuditRunner.cs"));
        var nullableContext = runner.IndexOf(
            "DocumentKind? documentKind = audit.DocumentKindSnapshot;",
            StringComparison.Ordinal);
        var validation = runner.IndexOf(
            "validationEngine.Validate(parsed, snapshots, documentKind, cancellationToken)",
            StringComparison.Ordinal);

        Assert.True(nullableContext >= 0);
        Assert.True(validation > nullableContext);
        Assert.DoesNotContain("Document type context is unavailable.", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentType", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Finding_order_is_stable_for_global_body_order_across_location_levels_and_sections()
    {
        var validator = new LocationOrderingValidator();
        var engine = new DocumentLayoutValidationEngine(new DocumentRuleValidatorRegistry([validator]));
        var snapshot = Wave1ValidatorTestData.Snapshot(validator.ValidationKey);
        var document = new ParsedDocument([], []);

        var first = engine.Validate(document, [snapshot], CancellationToken.None);
        var second = engine.Validate(document, [snapshot], CancellationToken.None);

        Assert.Equal([
            "document",
            "section0-paragraph",
            "section0",
            "section1-paragraph",
            "section1-run",
            "section1"
        ], first.Findings.Select(value => value.Finding.Actual.Property));
        Assert.Equal(LayoutFindingCanonicalProjection.Serialize(first),
            LayoutFindingCanonicalProjection.Serialize(second));
        Assert.Equal(LayoutFindingCanonicalProjection.Sha256(first),
            LayoutFindingCanonicalProjection.Sha256(second));
    }

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
            if (File.Exists(Path.Combine(candidate.FullName, "AGENTS.md"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class DocumentKindProbeValidator : IDocumentRuleValidator
    {
        public string ValidationKey => "test.document-kind-probe";
        public DocumentKind? ObservedKind { get; private set; }

        public RuleValidationResult Validate(RuleValidationContext context)
        {
            ObservedKind = context.DocumentKind;
            return RuleValidationResult.Applicable();
        }
    }

    private sealed class LocationOrderingValidator : IDocumentRuleValidator
    {
        public string ValidationKey => "test.location-ordering";

        public RuleValidationResult Validate(RuleValidationContext context) => RuleValidationResult.Applicable(
            Finding(context, "section1", new("maindocument/s:1/b:5/kind:section", 1, 5, null, null)),
            Finding(context, "section1-run", new("maindocument/s:1/b:4/p:4/r:0/kind:run", 1, 4, 4, 0)),
            Finding(context, "document", new("maindocument", null, null, null, null)),
            Finding(context, "section0", new("maindocument/s:0/b:2/kind:section", 0, 2, null, null)),
            Finding(context, "section1-paragraph", new("maindocument/s:1/b:4/p:4/kind:paragraph", 1, 4, 4, null)),
            Finding(context, "section0-paragraph", new("maindocument/s:0/b:1/p:1/kind:paragraph", 0, 1, 1, null)));

        private static RuleFindingCandidate Finding(
            RuleValidationContext context,
            string property,
            LayoutFindingLocation location) => new(
                "test.location-ordering",
                new(property, property, property, "category", FormattingResolutionState.Resolved,
                    FormattingSourceKind.Unspecified, null, false, null,
                    location.SectionIndex, location.ParagraphIndex, location.RunIndex),
                new(property, ["expected"], "category", null,
                    "resolved-snapshot-validation-key", context.Snapshot.ValidationKey),
                location,
                0);
    }
}
