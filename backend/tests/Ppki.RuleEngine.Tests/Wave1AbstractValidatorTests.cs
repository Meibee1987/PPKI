using Ppki.DocxEngine;
using Ppki.Domain;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class Wave1AbstractValidatorTests
{
    [Fact]
    public async Task Existing_semantic_fixture_satisfies_skripsi_language_pair_and_paragraph_count()
    {
        var document = await LayoutValidatorTestData.ParseFixture("minimal-document-sections-layout");
        var snapshots = new[]
        {
            Wave1ValidatorTestData.Snapshot("abstract.skripsi-language-pair", 1, "Skripsi", domain: "ABS"),
            Wave1ValidatorTestData.Snapshot("abstract.skripsi-narrative-paragraph-count-one", 2, "Skripsi", domain: "ABS")
        };

        var result = Wave1ValidatorTestData.Engine().Validate(document, snapshots, DocumentKind.Skripsi, CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.All(result.Outcomes, value => Assert.Equal(ValidationApplicability.Applicable, value.Result.Applicability));
    }

    [Fact]
    public void Missing_language_uses_document_location_and_never_invents_paragraph_index()
    {
        var document = Wave1ValidatorTestData.AbstractDocument(
            SemanticSectionKind.AbstractIndonesian, "Synthetic narrative");
        var snapshot = Wave1ValidatorTestData.Snapshot("abstract.skripsi-language-pair",
            appliesTo: "Skripsi", domain: "ABS");

        var result = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        var finding = Assert.Single(result.Findings).Finding;

        Assert.Equal("sectionPresence.AbstractEnglish", finding.Actual.Property);
        Assert.Equal("maindocument", finding.Location.CompactLocation);
        Assert.Null(finding.Location.ParagraphIndex);
        Assert.Equal("absent", finding.Actual.NormalizedValue);
    }

    [Fact]
    public void Document_type_applicability_is_explicit_and_unknown_context_is_invalid()
    {
        var document = Wave1ValidatorTestData.AbstractDocument(
            SemanticSectionKind.AbstractIndonesian, "Synthetic narrative");
        var snapshot = Wave1ValidatorTestData.Snapshot("abstract.skripsi-language-pair",
            appliesTo: "Skripsi", domain: "ABS");

        var thesis = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Tesis, CancellationToken.None);
        var unknown = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], CancellationToken.None);

        Assert.Equal(ValidationApplicability.NotApplicable, thesis.Outcomes[0].Result.Applicability);
        Assert.Equal(ValidationApplicability.InvalidRuleConfiguration, unknown.Outcomes[0].Result.Applicability);
        Assert.Empty(thesis.Findings);
        Assert.Empty(unknown.Findings);
    }

    [Fact]
    public void Thesis_summary_pair_uses_summary_kinds_without_language_detection()
    {
        var document = Wave1ValidatorTestData.AbstractDocument(
            SemanticSectionKind.SummaryIndonesian, "Synthetic summary narrative");
        var snapshot = Wave1ValidatorTestData.Snapshot("summary.thesis-dissertation-language-pair",
            appliesTo: "Tesis/Disertasi", domain: "ABS");

        var thesis = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Tesis, CancellationToken.None);
        var skripsi = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.Equal("sectionPresence.SummaryEnglish", Assert.Single(thesis.Findings).Finding.Actual.Property);
        Assert.Equal(ValidationApplicability.NotApplicable, skripsi.Outcomes[0].Result.Applicability);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public void Narrative_count_excludes_hidden_and_deleted_only_content(bool hidden, bool deleted, int visibleCount)
    {
        var document = Wave1ValidatorTestData.AbstractDocument(
            SemanticSectionKind.AbstractIndonesian, "SENSITIVE-ABSTRACT-MARKER", hidden, deleted);
        var snapshot = Wave1ValidatorTestData.Snapshot("abstract.skripsi-narrative-paragraph-count-one",
            appliesTo: "Skripsi", domain: "ABS");

        var result = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.Equal(visibleCount == 1 ? 0 : 1, result.Findings.Count);
        Assert.DoesNotContain("SENSITIVE-ABSTRACT-MARKER", LayoutFindingCanonicalProjection.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Word_count_is_unicode_aware_whitespace_stable_and_text_safe()
    {
        const string marker = "SENSITIVE-ABSTRACT-WORD-MARKER";
        var compact = Wave1ValidatorTestData.AbstractDocument(
            SemanticSectionKind.AbstractIndonesian, $"alpha beta {marker}");
        var spaced = Wave1ValidatorTestData.AbstractDocument(
            SemanticSectionKind.AbstractIndonesian, $"alpha\t  beta\r\n{marker}");
        var snapshot = Wave1ValidatorTestData.Snapshot("abstract.skripsi-word-count-max-200",
            appliesTo: "Skripsi", validationJson: "{\"maximumWords\":2}", domain: "ABS");

        var first = Wave1ValidatorTestData.Engine().Validate(compact, [snapshot], DocumentKind.Skripsi, CancellationToken.None);
        var second = Wave1ValidatorTestData.Engine().Validate(spaced, [snapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.Equal("3", Assert.Single(first.Findings).Finding.Actual.NormalizedValue);
        Assert.Equal(LayoutFindingCanonicalProjection.Serialize(first), LayoutFindingCanonicalProjection.Serialize(second));
        Assert.DoesNotContain(marker, LayoutFindingCanonicalProjection.Serialize(first), StringComparison.Ordinal);
    }

    [Fact]
    public void Keyword_paragraph_is_excluded_from_narrative_count_and_word_count()
    {
        var document = Wave1ValidatorTestData.AbstractDocument(
            SemanticSectionKind.AbstractIndonesian, "Kata kunci synthetic values", keyword: true);
        var snapshots = new[]
        {
            Wave1ValidatorTestData.Snapshot("abstract.skripsi-narrative-paragraph-count-one", 1,
                "Skripsi", "{\"paragraphCount\":0}", domain: "ABS"),
            Wave1ValidatorTestData.Snapshot("abstract.skripsi-word-count-max-200", 2,
                "Skripsi", "{\"maximumWords\":1}", domain: "ABS")
        };

        var result = Wave1ValidatorTestData.Engine().Validate(document, snapshots, DocumentKind.Skripsi, CancellationToken.None);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Abstract_spacing_uses_effective_values_and_property_level_provenance()
    {
        var compliant = Wave1ValidatorTestData.AbstractDocument(
            SemanticSectionKind.AbstractIndonesian, "Synthetic narrative");
        var body = compliant.Paragraphs[1];
        var invalidFormatting = body.EffectiveFormatting! with
        {
            SpacingAfterTwips = LayoutValidatorTestData.Value<long?>(120, "spacingAfterTwips",
                FormattingSourceKind.DirectFormatting)
        };
        var invalid = compliant with { Paragraphs = [compliant.Paragraphs[0], body with { EffectiveFormatting = invalidFormatting }] };
        var snapshot = Wave1ValidatorTestData.Snapshot(
            "abstract.skripsi-single-spacing-zero-paragraph-spacing", appliesTo: "Skripsi", domain: "ABS");

        Assert.Empty(Wave1ValidatorTestData.Engine().Validate(compliant, [snapshot], DocumentKind.Skripsi, CancellationToken.None).Findings);
        var finding = Assert.Single(Wave1ValidatorTestData.Engine().Validate(invalid, [snapshot], DocumentKind.Skripsi, CancellationToken.None).Findings);
        Assert.Equal("spacingAfterTwips", finding.Finding.Actual.Property);
        Assert.Equal(FormattingSourceKind.DirectFormatting, finding.Finding.Actual.SourceKind);
    }

    [Fact]
    public void Narrative_character_limit_returns_safe_unsupported_outcome()
    {
        var marker = new string('Z', StructuralValidatorLimits.MaximumNarrativeCharacters + 1);
        var document = Wave1ValidatorTestData.AbstractDocument(SemanticSectionKind.AbstractIndonesian, marker);
        var snapshot = Wave1ValidatorTestData.Snapshot("abstract.skripsi-word-count-max-200",
            appliesTo: "Skripsi", domain: "ABS");

        var result = Wave1ValidatorTestData.Engine().Validate(document, [snapshot], DocumentKind.Skripsi, CancellationToken.None);

        Assert.Equal(ValidationApplicability.Unsupported, result.Outcomes[0].Result.Applicability);
        Assert.Equal("narrative-text-limit-exceeded", result.Outcomes[0].Result.DiagnosticCode);
        Assert.DoesNotContain(marker, LayoutFindingCanonicalProjection.Serialize(result), StringComparison.Ordinal);
    }
}
