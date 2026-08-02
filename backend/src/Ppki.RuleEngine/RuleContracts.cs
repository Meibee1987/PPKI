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

public interface IResolvedRuleSetSnapshotBuilder
{
    IReadOnlyList<AuditRuleSnapshot> Build(
        Guid auditJobId,
        IEnumerable<RuleDefinition> resolvedRules,
        string layer,
        int precedence);
}

public interface IResolvedRuleSetHasher
{
    string Hash(IEnumerable<AuditRuleSnapshot> snapshots);
}
