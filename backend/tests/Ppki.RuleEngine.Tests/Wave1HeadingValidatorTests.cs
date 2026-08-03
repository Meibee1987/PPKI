using System.Text.Json;
using Ppki.DocxEngine;
using Ppki.Domain;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class Wave1HeadingValidatorTests
{
    [Fact]
    public void Confirmed_chapter_uses_semantic_and_structural_evidence_and_can_be_compliant()
    {
        var document = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, "BAB I PENDAHULUAN",
            SemanticSectionKind.Chapter, ParsedNumberingFormat.UpperRoman, "I"));
        var snapshots = new[]
        {
            Wave1ValidatorTestData.Snapshot("heading.chapter-number-upper-roman-no-period", 1),
            Wave1ValidatorTestData.Snapshot("heading.chapter-uppercase", 2),
            Wave1ValidatorTestData.Snapshot("heading.chapter-bold", 3),
            Wave1ValidatorTestData.Snapshot("heading.chapter-no-period-no-underline", 4),
            Wave1ValidatorTestData.Snapshot("heading.chapter-centered", 5)
        };

        var result = Wave1ValidatorTestData.Engine().Validate(document, snapshots, DocumentKind.Skripsi, CancellationToken.None);

        Assert.All(result.Outcomes, value => Assert.Equal(ValidationApplicability.Applicable, value.Result.Applicability));
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Numbered_list_and_formatting_only_candidate_are_not_heading_targets()
    {
        var fixture = await LayoutValidatorTestData.ParseFixture("minimal-numbered-heading-layout");
        Assert.DoesNotContain(fixture.Headings, value => value.ParagraphIndex == 2);
        var candidate = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(4, "SYNTHETIC CANDIDATE",
            Classification: HeadingClassification.Candidate));
        var snapshot = Wave1ValidatorTestData.Snapshot("heading.maximum-depth-3");

        var fixtureResult = Wave1ValidatorTestData.Engine().Validate(fixture, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        var candidateResult = Wave1ValidatorTestData.Engine().Validate(candidate, [snapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.DoesNotContain(fixtureResult.Findings, value => value.Finding.Location.ParagraphIndex == 2);
        Assert.Empty(candidateResult.Findings);
    }

    [Fact]
    public void Depth_limit_and_explicit_candidate_parameter_are_deterministic()
    {
        var document = Wave1ValidatorTestData.HeadingDocument(
            new HeadingSpec(4, "LEVEL FOUR"),
            new HeadingSpec(5, "LEVEL FIVE", Classification: HeadingClassification.Candidate));
        var defaultSnapshot = Wave1ValidatorTestData.Snapshot("heading.maximum-depth-3");
        var candidateSnapshot = Wave1ValidatorTestData.Snapshot("heading.maximum-depth-3",
            validationJson: "{\"includeCandidates\":true,\"maximumLevel\":3}");

        var first = Wave1ValidatorTestData.Engine().Validate(document, [defaultSnapshot], DocumentKind.Skripsi, CancellationToken.None);
        var second = Wave1ValidatorTestData.Engine().Validate(document, [candidateSnapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.Single(first.Findings);
        Assert.Equal(2, second.Findings.Count);
        Assert.Equal([0, 1], second.Findings.Select(value => value.Finding.Location.ParagraphIndex));
    }

    [Theory]
    [InlineData(ParsedNumberingFormat.Decimal, "1", "numberingFormat")]
    [InlineData(ParsedNumberingFormat.UpperRoman, "I.", "numberingTrailingPeriod")]
    public void Invalid_chapter_numbering_is_categorized_without_label_text(
        ParsedNumberingFormat format,
        string label,
        string property)
    {
        const string marker = "SENSITIVE-HEADING-NUMBER-MARKER";
        var document = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, marker,
            SemanticSectionKind.Chapter, format, label));
        var snapshot = Wave1ValidatorTestData.Snapshot("heading.chapter-number-upper-roman-no-period");

        var result = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        var finding = result.Findings.Single(value => value.Finding.Actual.Property == property);

        Assert.DoesNotContain(marker, LayoutFindingCanonicalProjection.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("SENSITIVE", JsonSerializer.Serialize(finding), StringComparison.Ordinal);
    }

    [Fact]
    public void Subheading_numbering_and_alignment_use_parsed_label_components()
    {
        var valid = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(2, "Subheading",
            NumberingFormat: ParsedNumberingFormat.Decimal, Label: "1.2", Alignment: ParsedAlignment.Left));
        var invalid = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(2, "Subheading",
            NumberingFormat: ParsedNumberingFormat.Decimal, Label: "I.2.", Alignment: ParsedAlignment.Center));
        var snapshot = Wave1ValidatorTestData.Snapshot("heading.subheading-decimal-left");

        Assert.Empty(Wave1ValidatorTestData.Engine().Validate(valid, [snapshot], DocumentKind.Skripsi, CancellationToken.None).Findings);
        var findings = Wave1ValidatorTestData.Engine().Validate(invalid, [snapshot], DocumentKind.Skripsi, CancellationToken.None).Findings;
        Assert.Equal(["numberingPattern", "alignment"], findings.Select(value => value.Finding.Actual.Property));
    }

    [Fact]
    public void Inherited_bold_passes_while_direct_override_and_mixed_runs_fail_safely()
    {
        var inherited = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, "BAB I",
            SemanticSectionKind.Chapter, Bold: true, Source: FormattingSourceKind.ParagraphStyle));
        var direct = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, "BAB I",
            SemanticSectionKind.Chapter, Bold: false, Source: FormattingSourceKind.DirectFormatting));
        var mixed = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, "BAB I",
            SemanticSectionKind.Chapter, Bold: true, AddMixedBoldRun: true));
        var snapshot = Wave1ValidatorTestData.Snapshot("heading.chapter-bold");

        Assert.Empty(Wave1ValidatorTestData.Engine().Validate(inherited, [snapshot], DocumentKind.Skripsi, CancellationToken.None).Findings);
        var finding = Assert.Single(Wave1ValidatorTestData.Engine().Validate(direct, [snapshot], DocumentKind.Skripsi, CancellationToken.None).Findings);
        Assert.Equal(FormattingSourceKind.DirectFormatting, finding.Finding.Actual.SourceKind);
        Assert.False(finding.Finding.Actual.Inherited);
        Assert.Equal("mixed", Assert.Single(Wave1ValidatorTestData.Engine().Validate(
            mixed, [snapshot], DocumentKind.Skripsi, CancellationToken.None).Findings).Finding.Actual.NormalizedValue);
    }

    [Fact]
    public void Underline_period_and_empty_heading_each_produce_safe_property_findings()
    {
        const string marker = "SENSITIVE-HEADING-DECORATION-MARKER.";
        var decorated = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, marker,
            SemanticSectionKind.Chapter, Underline: "single"));
        var empty = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, string.Empty,
            SemanticSectionKind.Chapter, Empty: true));
        var snapshot = Wave1ValidatorTestData.Snapshot("heading.chapter-no-period-no-underline");

        var first = Wave1ValidatorTestData.Engine().Validate(decorated, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        var second = Wave1ValidatorTestData.Engine().Validate(empty, [snapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.Equal(["underline", "trailingPunctuation"], first.Findings.Select(value => value.Finding.Actual.Property));
        Assert.Equal("visibleText", Assert.Single(second.Findings).Finding.Actual.Property);
        Assert.DoesNotContain(marker, LayoutFindingCanonicalProjection.Serialize(first), StringComparison.Ordinal);
    }

    [Fact]
    public void Header_footer_table_and_nonsemantic_level_one_headings_are_excluded_from_chapter_rules()
    {
        var table = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, "not uppercase.",
            SemanticSectionKind.Chapter, InTable: true, Bold: false));
        var nonChapter = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, "not uppercase.", Bold: false));
        var snapshots = new[]
        {
            Wave1ValidatorTestData.Snapshot("heading.chapter-uppercase", 1),
            Wave1ValidatorTestData.Snapshot("heading.chapter-bold", 2)
        };

        Assert.Empty(Wave1ValidatorTestData.Engine().Validate(table, snapshots, DocumentKind.Skripsi, CancellationToken.None).Findings);
        Assert.Empty(Wave1ValidatorTestData.Engine().Validate(nonChapter, snapshots, DocumentKind.Skripsi, CancellationToken.None).Findings);
    }

    [Fact]
    public void Heading_text_limit_is_safe_and_not_treated_as_compliant()
    {
        var marker = new string('X', StructuralValidatorLimits.MaximumHeadingCharacters + 1);
        var document = Wave1ValidatorTestData.HeadingDocument(new HeadingSpec(1, marker, SemanticSectionKind.Chapter));
        var snapshot = Wave1ValidatorTestData.Snapshot("heading.chapter-uppercase");

        var result = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.Equal(ValidationApplicability.Unsupported, result.Outcomes[0].Result.Applicability);
        Assert.Equal("heading-text-limit-exceeded", result.Outcomes[0].Result.DiagnosticCode);
        Assert.DoesNotContain(marker, LayoutFindingCanonicalProjection.Serialize(result), StringComparison.Ordinal);
    }
}
