using Ppki.DocxEngine;
using Ppki.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ppki.RuleEngine;

public enum ValidationApplicability
{
    Applicable,
    NotApplicable,
    Unsupported,
    InvalidRuleConfiguration
}

public sealed record LayoutValidatorOptions
{
    public const int DefaultMaximumFindings = 10_000;
    public int MaximumFindings { get; init; } = DefaultMaximumFindings;

    public void Validate()
    {
        if (MaximumFindings <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumFindings), "Finding limit must be positive.");
    }
}

public sealed record RuleValidationContext(
    AuditRuleSnapshot Snapshot,
    ParsedDocument Document,
    LayoutValidatorOptions Options,
    CancellationToken CancellationToken);

public sealed record LayoutFindingActual(
    string Property,
    string? RawValue,
    string? NormalizedValue,
    string Unit,
    FormattingResolutionState ResolutionState,
    FormattingSourceKind SourceKind,
    string? SourceStyleId,
    bool Inherited,
    string? DiagnosticCode,
    int? SectionIndex,
    int? ParagraphIndex,
    int? RunIndex);

public sealed record LayoutFindingExpected(
    string Property,
    IReadOnlyList<string> AcceptedValues,
    string Unit,
    string? Tolerance,
    string ContractSource,
    string ValidationKey);

public sealed record LayoutFindingLocation(
    string CompactLocation,
    int? SectionIndex,
    int? BodyElementIndex,
    int? ParagraphIndex,
    int? RunIndex);

public sealed record RuleFindingCandidate(
    string MessageKey,
    LayoutFindingActual Actual,
    LayoutFindingExpected Expected,
    LayoutFindingLocation Location,
    int PropertyOrder,
    decimal Confidence = 1m)
{
    public string SemanticKey(string ruleCode) => string.Join('|',
        ruleCode,
        Location.CompactLocation,
        Actual.Property,
        Actual.NormalizedValue ?? "<missing>");
}

public sealed record RuleValidationResult(
    ValidationApplicability Applicability,
    IReadOnlyList<RuleFindingCandidate> Findings,
    string? DiagnosticCode = null)
{
    public static RuleValidationResult Applicable(params RuleFindingCandidate[] findings) =>
        new(ValidationApplicability.Applicable, findings);

    public static RuleValidationResult Unsupported(string code) =>
        new(ValidationApplicability.Unsupported, [], code);

    public static RuleValidationResult Invalid(string code) =>
        new(ValidationApplicability.InvalidRuleConfiguration, [], code);
}

public interface IDocumentRuleValidator
{
    string ValidationKey { get; }
    RuleValidationResult Validate(RuleValidationContext context);
}

public sealed class DocumentRuleValidatorRegistry
{
    private readonly IReadOnlyDictionary<string, IDocumentRuleValidator> _validators;

    public DocumentRuleValidatorRegistry(IEnumerable<IDocumentRuleValidator> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        var ordered = validators.OrderBy(value => value.ValidationKey, StringComparer.Ordinal).ToArray();
        var duplicate = ordered.GroupBy(value => value.ValidationKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException("Duplicate document validator key is not allowed.");
        _validators = ordered.ToDictionary(value => value.ValidationKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> ValidationKeys => _validators.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    public bool TryResolve(string validationKey, out IDocumentRuleValidator validator)
    {
        if (string.IsNullOrWhiteSpace(validationKey))
        {
            validator = null!;
            return false;
        }
        return _validators.TryGetValue(validationKey, out validator!);
    }
}

public sealed record RuleValidationOutcome(AuditRuleSnapshot Snapshot, RuleValidationResult Result);
public sealed record ResolvedRuleFinding(AuditRuleSnapshot Snapshot, RuleFindingCandidate Finding);
public sealed record DocumentValidationResult(
    IReadOnlyList<RuleValidationOutcome> Outcomes,
    IReadOnlyList<ResolvedRuleFinding> Findings,
    bool FindingsTruncated);

public static class LayoutFindingCanonicalProjection
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(DocumentValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(new
        {
            Outcomes = result.Outcomes.Select(value => new
            {
                value.Snapshot.Ordinal,
                value.Snapshot.RuleCode,
                value.Snapshot.ValidationKey,
                value.Result.Applicability,
                value.Result.DiagnosticCode
            }),
            Findings = result.Findings.Select(value => new
            {
                value.Snapshot.Ordinal,
                value.Snapshot.RuleCode,
                value.Snapshot.ValidationKey,
                value.Finding.MessageKey,
                value.Finding.Actual,
                value.Finding.Expected,
                value.Finding.Location,
                value.Finding.PropertyOrder,
                value.Finding.Confidence
            }),
            result.FindingsTruncated
        }, Options);
    }

    public static string Sha256(DocumentValidationResult result) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(result))));
}

public sealed class DocumentLayoutValidationEngine
{
    private readonly DocumentRuleValidatorRegistry _registry;
    private readonly LayoutValidatorOptions _options;

    public DocumentLayoutValidationEngine(DocumentRuleValidatorRegistry registry)
        : this(registry, new LayoutValidatorOptions()) { }

    public DocumentLayoutValidationEngine(DocumentRuleValidatorRegistry registry, LayoutValidatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _registry = registry;
        _options = options;
    }

    public DocumentValidationResult Validate(
        ParsedDocument document,
        IEnumerable<AuditRuleSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(snapshots);
        var outcomes = new List<RuleValidationOutcome>();
        var candidates = new List<ResolvedRuleFinding>();

        foreach (var snapshot in snapshots.OrderBy(value => value.Ordinal).ThenBy(value => value.RuleCode, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuleValidationResult result;
            if (!_registry.TryResolve(snapshot.ValidationKey, out var validator))
            {
                result = RuleValidationResult.Unsupported("validator-key-unsupported");
            }
            else
            {
                result = validator.Validate(new(snapshot, document, _options, cancellationToken));
            }
            outcomes.Add(new(snapshot, result));
            if (result.Applicability == ValidationApplicability.Applicable)
                candidates.AddRange(result.Findings.Select(finding => new ResolvedRuleFinding(snapshot, finding)));
        }

        var ordered = candidates
            .OrderBy(value => value.Snapshot.Ordinal)
            .ThenBy(value => value.Finding.Location.CompactLocation, StringComparer.Ordinal)
            .ThenBy(value => value.Finding.PropertyOrder)
            .ThenBy(value => value.Finding.Actual.NormalizedValue, StringComparer.Ordinal)
            .GroupBy(value => value.Finding.SemanticKey(value.Snapshot.RuleCode), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var limited = ordered.Take(_options.MaximumFindings).ToArray();
        return new(outcomes.ToArray(), limited, ordered.Length > limited.Length);
    }
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
