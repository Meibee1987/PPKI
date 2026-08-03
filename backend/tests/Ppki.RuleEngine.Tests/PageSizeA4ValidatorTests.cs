using Ppki.RuleEngine;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class PageSizeA4ValidatorTests
{
    [Fact]
    public void Returns_no_finding_for_a4_effective_section()
    {
        var document = new Ppki.DocxEngine.ParsedDocument(
            [LayoutValidatorTestData.Section(11906, 16838, 1701, 1701, 1701, 2268)], []);
        var result = new PageSizeA4Validator().Validate(new(
            LayoutValidatorTestData.Snapshot("section.page-size-a4"),
            document,
            new LayoutValidatorOptions(),
            CancellationToken.None));

        Assert.Equal(ValidationApplicability.Applicable, result.Applicability);
        Assert.Empty(result.Findings);
    }
}
