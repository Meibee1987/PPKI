using Ppki.Domain;

namespace Ppki.Application;

public static class FixFailureCatalog
{
    private static readonly IReadOnlyDictionary<string, FixFailureCategory> Codes =
        new Dictionary<string, FixFailureCategory>(StringComparer.Ordinal)
        {
            ["fix-plan-stale"] = FixFailureCategory.Conflict,
            ["fix-source-version-superseded"] = FixFailureCategory.Conflict,
            ["fix-result-object-conflict"] = FixFailureCategory.Conflict,
            ["fix-version-number-conflict"] = FixFailureCategory.Conflict,
            ["fix-execution-conflict"] = FixFailureCategory.Conflict,
            ["fix-concurrent-publish-conflict"] = FixFailureCategory.Conflict,
            ["source-version-missing"] = FixFailureCategory.InvalidSource,
            ["source-storage-object-missing"] = FixFailureCategory.InvalidSource,
            ["source-size-invalid"] = FixFailureCategory.InvalidSource,
            ["source-hash-mismatch"] = FixFailureCategory.InvalidSource,
            ["source-package-invalid"] = FixFailureCategory.InvalidSource,
            ["approved-plan-invalid"] = FixFailureCategory.InvalidPlan,
            ["approved-plan-hash-invalid"] = FixFailureCategory.InvalidPlan,
            ["approved-plan-selection-invalid"] = FixFailureCategory.InvalidPlan,
            ["approved-plan-operation-invalid"] = FixFailureCategory.InvalidPlan,
            ["approved-plan-provider-mismatch"] = FixFailureCategory.InvalidPlan,
            ["fix-provider-unavailable"] = FixFailureCategory.CapabilityUnavailable,
            ["fix-provider-not-registered"] = FixFailureCategory.CapabilityUnavailable,
            ["fix-provider-version-unavailable"] = FixFailureCategory.CapabilityUnavailable,
            ["storage-download-transient"] = FixFailureCategory.TransientInfrastructure,
            ["storage-upload-transient"] = FixFailureCategory.TransientInfrastructure,
            ["database-transient"] = FixFailureCategory.TransientInfrastructure,
            ["worker-lease-lost"] = FixFailureCategory.TransientInfrastructure,
            ["worker-interrupted"] = FixFailureCategory.TransientInfrastructure,
            ["storage-upload-terminal"] = FixFailureCategory.TerminalInfrastructure,
            ["database-finalization-terminal"] = FixFailureCategory.TerminalInfrastructure,
            ["result-cleanup-failed"] = FixFailureCategory.TerminalInfrastructure
        };

    public static FixFailureCategory Classify(string code)
    {
        if (Codes.TryGetValue(code, out var category)) return category;
        if (code.Contains("capability", StringComparison.Ordinal)) return FixFailureCategory.CapabilityUnavailable;
        if (code.Contains("snapshot", StringComparison.Ordinal) || code.Contains("plan", StringComparison.Ordinal)) return FixFailureCategory.InvalidPlan;
        if (code.Contains("source", StringComparison.Ordinal) || code.Contains("package", StringComparison.Ordinal)
            || code.Contains("parser", StringComparison.Ordinal) || code.Contains("postcondition", StringComparison.Ordinal)) return FixFailureCategory.InvalidSource;
        if (code.Contains("conflict", StringComparison.Ordinal) || code.Contains("stale", StringComparison.Ordinal)) return FixFailureCategory.Conflict;
        return FixFailureCategory.TerminalInfrastructure;
    }

    public static bool IsSafe(string code) => code is { Length: > 0 and <= 128 }
        && code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}

public static class FixRetryPolicy
{
    public const int MaximumAttempts = 3;
    public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(5);
    public const string Version = "fix-retry/1.0";
    public static bool ShouldRetry(FixFailureCategory category, int attemptCount, int maxAttempts) =>
        category == FixFailureCategory.TransientInfrastructure && attemptCount < maxAttempts;
}

public readonly record struct FixExecutionClaim(Guid ExecutionId, Guid Token, int AttemptNumber, DateTimeOffset LeaseExpiresAt);
