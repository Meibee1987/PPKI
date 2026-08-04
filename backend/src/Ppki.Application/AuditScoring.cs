using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

[JsonConverter(typeof(JsonStringEnumConverter<AuditScoreState>))]
public enum AuditScoreState
{
    Calculated,
    NotConfigured,
    InvalidConfiguration,
    NotApplicable,
    AuditIncomplete
}

public sealed record AuditScoringPolicy(
    string Version,
    decimal MinimumScore,
    decimal MaximumScore,
    decimal MaximumPenalty,
    decimal ErrorWeight,
    decimal WarningWeight,
    decimal InfoWeight,
    int DecimalPlaces,
    MidpointRounding Rounding);

public sealed record AuditScoreFinding(string RuleCode, RuleSeverity Severity);

public sealed record AuditScoreInput(
    AuditJobStatus Status,
    int ApplicableRuleCount,
    IReadOnlyList<AuditScoreFinding> Findings);

public sealed record AuditScoreBreakdown(
    int ScoredFindingCount,
    int DistinctViolatedRules,
    decimal TotalPenalty,
    decimal MaximumPenalty,
    decimal MinimumScore,
    decimal MaximumScore);

public sealed record AuditScoreResult(
    AuditScoreState State,
    decimal? Score,
    string? PolicyVersion,
    AuditScoreBreakdown? Breakdown,
    string? DiagnosticCode);

public interface IAuditScoreCalculator
{
    AuditScoreResult Calculate(AuditScoreInput input, AuditScoringPolicy? policy);
}

public sealed class AuditScoreCalculator : IAuditScoreCalculator
{
    public AuditScoreResult Calculate(AuditScoreInput input, AuditScoringPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Findings);

        if (input.Status != AuditJobStatus.Completed)
            return State(AuditScoreState.AuditIncomplete, "audit-incomplete");
        if (input.ApplicableRuleCount < 0)
            return State(AuditScoreState.InvalidConfiguration, "applicable-rule-count-invalid");
        if (input.ApplicableRuleCount == 0)
            return State(AuditScoreState.NotApplicable, "no-applicable-rules");
        if (policy is null)
            return State(AuditScoreState.NotConfigured, "scoring-policy-not-configured");
        if (!Valid(policy) || input.Findings.Any(value =>
                string.IsNullOrWhiteSpace(value.RuleCode) || !Enum.IsDefined(value.Severity)))
            return State(AuditScoreState.InvalidConfiguration, "scoring-configuration-invalid", policy.Version);

        try
        {
            // Findings are already semantically deduplicated before persistence.
            // Every persisted finding is therefore a separate scoring input;
            // RuleCode is descriptive and is not an aggregation key.
            var penalties = input.Findings
                .Select(value => Weight(policy, value.Severity))
                .ToArray();
            var totalPenalty = penalties.Sum();
            var normalizedPenalty = Math.Min(totalPenalty, policy.MaximumPenalty) / policy.MaximumPenalty;
            var rawScore = policy.MaximumScore
                - normalizedPenalty * (policy.MaximumScore - policy.MinimumScore);
            var score = decimal.Round(rawScore, policy.DecimalPlaces, policy.Rounding);
            score = Math.Clamp(score, policy.MinimumScore, policy.MaximumScore);

            return new(
                AuditScoreState.Calculated,
                score,
                policy.Version,
                new(penalties.Length,
                    input.Findings.Select(value => value.RuleCode)
                        .Distinct(StringComparer.Ordinal).Count(),
                    totalPenalty, policy.MaximumPenalty,
                    policy.MinimumScore, policy.MaximumScore),
                null);
        }
        catch (OverflowException)
        {
            return State(AuditScoreState.InvalidConfiguration,
                "scoring-configuration-invalid", policy.Version);
        }
    }

    private static bool Valid(AuditScoringPolicy policy) =>
        !string.IsNullOrWhiteSpace(policy.Version)
        && policy.Version.Length <= 64
        && policy.MinimumScore < policy.MaximumScore
        && policy.MaximumPenalty > 0
        && policy.ErrorWeight >= 0
        && policy.WarningWeight >= 0
        && policy.InfoWeight >= 0
        && policy.DecimalPlaces is >= 0 and <= 4
        && policy.Rounding is MidpointRounding.ToEven or MidpointRounding.AwayFromZero;

    private static decimal Weight(AuditScoringPolicy policy, RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => policy.ErrorWeight,
        RuleSeverity.Warning => policy.WarningWeight,
        RuleSeverity.Info => policy.InfoWeight,
        _ => throw new ArgumentOutOfRangeException(nameof(severity))
    };

    private static AuditScoreResult State(
        AuditScoreState state,
        string diagnosticCode,
        string? version = null) => new(state, null, version, null, diagnosticCode);
}
