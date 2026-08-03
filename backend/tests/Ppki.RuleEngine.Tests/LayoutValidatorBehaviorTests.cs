using Ppki.DocxEngine;
using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class LayoutValidatorBehaviorTests
{
    [Fact]
    public async Task Section_validators_accept_compliant_fixture_and_find_every_invalid_property()
    {
        var snapshots = LayoutValidatorTestData.DefaultSnapshots()
            .Where(value => value.ValidationKey.StartsWith("section.", StringComparison.Ordinal)).ToArray();
        var compliant = LayoutValidatorTestData.Engine().Validate(
            await LayoutValidatorTestData.ParseFixture("minimal-compliant-layout"), snapshots, CancellationToken.None);
        Assert.Empty(compliant.Findings);

        var invalid = LayoutValidatorTestData.Engine().Validate(
            await LayoutValidatorTestData.ParseFixture("minimal-invalid-layout"), snapshots, CancellationToken.None);
        Assert.Equal(["pageSize", "marginLeft", "marginRight", "marginTop", "marginBottom"],
            invalid.Findings.Select(value => value.Finding.Actual.Property));
        Assert.All(invalid.Findings, value => Assert.Equal(0, value.Finding.Location.SectionIndex));
    }

    [Fact]
    public void Missing_section_values_and_multiple_sections_have_stable_locations()
    {
        var missing = new ParsedDocument([new ParsedSection(0, null, null, null, null, null, null)], []);
        var missingResult = LayoutValidatorTestData.Engine().Validate(missing,
            [LayoutValidatorTestData.Snapshot("section.page-size-a4")], CancellationToken.None);
        Assert.Single(missingResult.Findings);
        Assert.Equal(FormattingResolutionState.Unspecified, missingResult.Findings[0].Finding.Actual.ResolutionState);

        var multiple = new ParsedDocument([
            LayoutValidatorTestData.Section(12240, 15840, 1440, 1440, 1440, 1440, 0),
            LayoutValidatorTestData.Section(12240, 15840, 1440, 1440, 1440, 1440, 1)
        ], []);
        var result = LayoutValidatorTestData.Engine().Validate(multiple,
            [LayoutValidatorTestData.Snapshot("section.margin-left-4cm")], CancellationToken.None);
        Assert.Equal([0, 1], result.Findings.Select(value => value.Finding.Location.SectionIndex));
        Assert.Equal(2, result.Findings.Select(value => value.Finding.Location.CompactLocation).Distinct().Count());
    }

    [Fact]
    public async Task Paragraph_validators_accept_compliant_fixture_and_find_invalid_layout()
    {
        var snapshots = LayoutValidatorTestData.DefaultSnapshots().Where(value => value.ValidationKey is
            "body.line-spacing-single" or "body.first-line-indent-1cm" or "body.justified").ToArray();
        var compliant = LayoutValidatorTestData.Engine().Validate(
            await LayoutValidatorTestData.ParseFixture("minimal-compliant-layout"), snapshots, CancellationToken.None);
        Assert.Empty(compliant.Findings);
        var invalid = LayoutValidatorTestData.Engine().Validate(
            await LayoutValidatorTestData.ParseFixture("minimal-invalid-layout"), snapshots, CancellationToken.None);
        Assert.Equal(["lineSpacingValue", "firstLineIndent", "alignment"],
            invalid.Findings.Select(value => value.Finding.Actual.Property));
    }

    [Fact]
    public void Effective_inherited_values_can_pass_and_direct_override_can_fail()
    {
        var inherited = LayoutValidatorTestData.Paragraph(formattingSource: FormattingSourceKind.ParagraphStyle);
        var inheritedDocument = new ParsedDocument([], [inherited]);
        var snapshots = new[]
        {
            LayoutValidatorTestData.Snapshot("body.line-spacing-single", 1),
            LayoutValidatorTestData.Snapshot("body.first-line-indent-1cm", 2),
            LayoutValidatorTestData.Snapshot("body.justified", 3)
        };
        Assert.Empty(LayoutValidatorTestData.Engine().Validate(inheritedDocument, snapshots, CancellationToken.None).Findings);

        var direct = LayoutValidatorTestData.Paragraph(alignment: ParsedAlignment.Left,
            formattingSource: FormattingSourceKind.DirectFormatting);
        var finding = Assert.Single(LayoutValidatorTestData.Engine().Validate(new ParsedDocument([], [direct]),
            [snapshots[2]], CancellationToken.None).Findings).Finding;
        Assert.Equal(FormattingSourceKind.DirectFormatting, finding.Actual.SourceKind);
        Assert.False(finding.Actual.Inherited);
    }

    [Fact]
    public void Hanging_indent_is_not_first_line_indent_and_zero_differs_from_missing()
    {
        var hangingOnly = LayoutValidatorTestData.Paragraph(firstLine: null, hanging: 567);
        var defaultSnapshot = LayoutValidatorTestData.Snapshot("body.first-line-indent-1cm");
        Assert.Single(LayoutValidatorTestData.Engine().Validate(new ParsedDocument([], [hangingOnly]),
            [defaultSnapshot], CancellationToken.None).Findings);

        var zero = LayoutValidatorTestData.Paragraph(firstLine: 0);
        var zeroSnapshot = LayoutValidatorTestData.Snapshot("body.first-line-indent-1cm",
            validationJson: "{\"value\":0,\"unit\":\"twip\"}");
        Assert.Empty(LayoutValidatorTestData.Engine().Validate(new ParsedDocument([], [zero]),
            [zeroSnapshot], CancellationToken.None).Findings);
        var missing = LayoutValidatorTestData.Paragraph(firstLine: null);
        Assert.Single(LayoutValidatorTestData.Engine().Validate(new ParsedDocument([], [missing]),
            [zeroSnapshot], CancellationToken.None).Findings);
    }

    [Fact]
    public void Heading_and_table_paragraphs_are_excluded_from_normal_selector()
    {
        var heading = LayoutValidatorTestData.Paragraph(ParsedAlignment.Left, firstLine: null,
            heading: true, index: 0);
        var table = LayoutValidatorTestData.Paragraph(ParsedAlignment.Left, firstLine: null,
            inTable: true, index: 1);
        var result = LayoutValidatorTestData.Engine().Validate(new ParsedDocument([], [heading, table]),
            [LayoutValidatorTestData.Snapshot("body.justified")], CancellationToken.None);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Font_validator_uses_effective_theme_and_character_style_values_without_mixing_slots()
    {
        var document = await LayoutValidatorTestData.ParseFixture("minimal-style-inheritance-layout");
        var snapshot = LayoutValidatorTestData.Snapshot("body.font-times-new-roman-12", validationJson:
            "{\"fontFamily\":\"Major Latin Synthetic\",\"fontSize\":15,\"fontSizeUnit\":\"pt\",\"fontSlots\":[\"highAnsi\"]}");
        var result = LayoutValidatorTestData.Engine().Validate(document, [snapshot], CancellationToken.None);
        Assert.DoesNotContain(result.Findings, value => value.Finding.Location.ParagraphIndex == 0);
        var defaultRunFinding = Assert.Single(result.Findings);
        Assert.Equal("fontSize", defaultRunFinding.Finding.Actual.Property);
        Assert.Equal(1, defaultRunFinding.Finding.Location.ParagraphIndex);
        Assert.Equal(FormattingSourceKind.DocumentDefault, defaultRunFinding.Finding.Actual.SourceKind);
    }

    [Fact]
    public void Font_mismatch_missing_hidden_deleted_and_empty_runs_follow_bounded_scope()
    {
        var visible = LayoutValidatorTestData.Run(ascii: "Calibri", highAnsi: null, size: 22, index: 0);
        var hidden = LayoutValidatorTestData.Run(ascii: "Calibri", highAnsi: "Calibri", size: 22, hidden: true, index: 1);
        var deleted = LayoutValidatorTestData.Run(ascii: "Calibri", highAnsi: "Calibri", size: 22, deleted: true, index: 2);
        var empty = LayoutValidatorTestData.Run(ascii: "Calibri", highAnsi: "Calibri", size: 22, empty: true, index: 3);
        var paragraph = LayoutValidatorTestData.Paragraph(runs: [visible, hidden, deleted, empty]);
        var result = LayoutValidatorTestData.Engine().Validate(new ParsedDocument([], [paragraph]),
            [LayoutValidatorTestData.Snapshot("body.font-times-new-roman-12")], CancellationToken.None);
        Assert.Equal(["font.ascii", "font.highAnsi", "fontSize"],
            result.Findings.Select(value => value.Finding.Actual.Property));
        Assert.All(result.Findings, value => Assert.Equal(0, value.Finding.Location.RunIndex));
        Assert.Contains(result.Findings, value => value.Finding.Actual.ResolutionState == FormattingResolutionState.Unspecified);
    }
}
