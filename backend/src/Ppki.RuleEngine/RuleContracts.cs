using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.RuleEngine;

public sealed record RuleFinding(
    string Message,
    object Actual,
    object Expected,
    object Location,
    decimal Confidence = 1m);

public interface IRuleValidator
{
    string ValidationKey { get; }
    IReadOnlyList<RuleFinding> Validate(ParsedDocument document, RuleDefinition rule);
}
