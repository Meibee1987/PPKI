using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.RuleEngine;

namespace Ppki.RuleEngine.Tests;

public sealed class PageSizeA4ValidatorTests
{
    [Fact]
    public void Returns_no_finding_for_a4()
    {
        var document = new ParsedDocument(
            [new ParsedSection(0, 21m, 29.7m, 3m, 3m, 3m, 4m)],
            []);
        var rule = new RuleDefinition
        {
            RuleCode = "PPKI-LAY-003",
            Domain = "LAY",
            AppliesTo = "Semua",
            Element = "Ukuran halaman",
            OfficialRequirement = "Ukuran kertas A4 21,0 cm x 29,7 cm.",
            ExpectedValuePattern = "A4",
            Severity = RuleSeverity.Error,
            FixMode = FixMode.Auto,
            ValidationKey = "section.page-size-a4",
            IsImplemented = true
        };

        var result = new PageSizeA4Validator().Validate(document, rule);

        Assert.Empty(result);
    }
}
