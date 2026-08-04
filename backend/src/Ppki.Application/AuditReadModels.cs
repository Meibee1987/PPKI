using System.Text.Json;
using Ppki.Domain;

namespace Ppki.Application;

public sealed record AuditSeveritySummary(int Error, int Warning, int Info);
public sealed record AuditDomainSummary(string Domain, int FindingCount);
public sealed record AuditFixModeSummary(int Auto, int Confirm, int Manual, int Report);
public sealed record AuditFindingSummaryBucket(
    string Domain,
    RuleSeverity Severity,
    FixMode FixMode,
    int Count);

public sealed record AuditSummaryCounts(
    int FindingCount,
    AuditSeveritySummary Severity,
    IReadOnlyList<AuditDomainSummary> Domains,
    AuditFixModeSummary FixModes)
{
    public static AuditSummaryCounts FromBuckets(
        IEnumerable<AuditFindingSummaryBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var values = buckets.ToArray();
        if (values.Any(value => value.Count < 0 || string.IsNullOrWhiteSpace(value.Domain)))
            throw new ArgumentException("Summary buckets must be valid.", nameof(buckets));

        var severity = new AuditSeveritySummary(
            Count(values, RuleSeverity.Error),
            Count(values, RuleSeverity.Warning),
            Count(values, RuleSeverity.Info));
        var fixModes = new AuditFixModeSummary(
            Count(values, FixMode.Auto),
            Count(values, FixMode.Confirm),
            Count(values, FixMode.Manual),
            Count(values, FixMode.Report));
        var domains = values
            .GroupBy(value => value.Domain, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AuditDomainSummary(group.Key, group.Sum(value => value.Count)))
            .ToArray();

        return new(values.Sum(value => value.Count), severity, domains, fixModes);
    }

    private static int Count(
        IEnumerable<AuditFindingSummaryBucket> values,
        RuleSeverity severity) => values
            .Where(value => value.Severity == severity)
            .Sum(value => value.Count);

    private static int Count(
        IEnumerable<AuditFindingSummaryBucket> values,
        FixMode fixMode) => values
            .Where(value => value.FixMode == fixMode)
            .Sum(value => value.Count);
}

public sealed record AuditFailureSummary(string Code, string Message)
{
    public static AuditFailureSummary? FromStatus(AuditJobStatus status) =>
        status == AuditJobStatus.Failed
            ? new("audit-processing-failed", "Audit processing failed.")
            : null;
}

public sealed record AuditSummaryDto(
    Guid Id,
    string Status,
    Guid DocumentVersionId,
    Guid ProfileVersionId,
    string? DocumentKindSnapshot,
    string? ResolvedRuleSetHash,
    int ApplicableRuleCount,
    int TotalRules,
    int PersistedFindingCount,
    int FindingCount,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    AuditSeveritySummary Severity,
    IReadOnlyList<AuditDomainSummary> Domains,
    AuditFixModeSummary FixModes,
    AuditScoreState ScoreState,
    decimal? Score,
    string? ScorePolicyVersion,
    AuditScoreBreakdown? ScoreBreakdown,
    string? ScoreDiagnosticCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? ErrorMessage);

public sealed record AuditFindingSourceDto(
    string? SourceSection,
    int? PdfPage,
    string? PrintedPage);

public sealed record AuditFindingListItemDto(
    Guid Id,
    Guid AuditId,
    int RuleOrdinal,
    string RuleCode,
    string Domain,
    string ValidationKey,
    string Element,
    string Severity,
    string FixMode,
    string FindingState,
    string ReasonCode,
    string Message,
    JsonElement Actual,
    JsonElement Expected,
    JsonElement Location,
    decimal? Confidence,
    AuditFindingSourceDto Source,
    string ActionAvailability);

public sealed record AuditFindingDetailDto(
    Guid Id,
    Guid AuditId,
    Guid DocumentVersionId,
    int RuleOrdinal,
    string RuleCode,
    string Domain,
    string ValidationKey,
    string Element,
    string Severity,
    string FixMode,
    string FindingState,
    string ReasonCode,
    string Message,
    JsonElement Actual,
    JsonElement Expected,
    JsonElement Location,
    decimal? Confidence,
    AuditFindingSourceDto Source,
    string ActionAvailability);

public sealed record AuditFindingPageDto(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AuditFindingListItemDto> Items);

public sealed record AuditFindingQuery(
    RuleSeverity? Severity,
    FixMode? FixMode,
    string? Domain,
    string? RuleCode,
    string? ValidationKey,
    int Page,
    int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
    public const int MaximumPage = 10_000;
    public const int MaximumFindingCount = 10_000;

    public static bool TryCreate(
        string? severity,
        string? fixMode,
        string? domain,
        string? ruleCode,
        string? validationKey,
        string? sort,
        int? page,
        int? pageSize,
        out AuditFindingQuery query,
        out string? errorCode)
    {
        query = null!;
        errorCode = null;
        if (!TryEnum(severity, out RuleSeverity? parsedSeverity)
            || !TryEnum(fixMode, out FixMode? parsedFixMode))
        {
            errorCode = "finding-filter-enum-invalid";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(sort)
            && !sort.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "finding-sort-invalid";
            return false;
        }

        var selectedPage = page ?? 1;
        var selectedPageSize = pageSize ?? DefaultPageSize;
        if (selectedPage is < 1 or > MaximumPage
            || selectedPageSize is < 1 or > MaximumPageSize
            || (long)(selectedPage - 1) * selectedPageSize >= MaximumFindingCount)
        {
            errorCode = "finding-pagination-invalid";
            return false;
        }

        if (!TryFilter(domain, 128, out var normalizedDomain)
            || !TryFilter(ruleCode, 128, out var normalizedRuleCode)
            || !TryFilter(validationKey, 256, out var normalizedValidationKey))
        {
            errorCode = "finding-filter-text-invalid";
            return false;
        }

        query = new(parsedSeverity, parsedFixMode, normalizedDomain,
            normalizedRuleCode, normalizedValidationKey, selectedPage, selectedPageSize);
        return true;
    }

    private static bool TryEnum<T>(string? value, out T? parsed) where T : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        var name = Enum.GetNames<T>()
            .SingleOrDefault(candidate => candidate.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (name is null) return false;
        parsed = Enum.Parse<T>(name);
        return true;
    }

    private static bool TryFilter(string? value, int maximumLength, out string? normalized)
    {
        normalized = null;
        if (value is null) return true;
        var trimmed = value.Trim();
        if (trimmed.Length is 0 || trimmed.Length > maximumLength) return false;
        normalized = trimmed;
        return true;
    }
}

public interface IAuditReadService
{
    Task<AuditSummaryDto?> GetSummaryAsync(Guid auditId, Guid ownerUserId, CancellationToken cancellationToken);
    Task<AuditFindingPageDto?> GetFindingsAsync(Guid auditId, Guid ownerUserId, AuditFindingQuery query, CancellationToken cancellationToken);
    Task<AuditFindingDetailDto?> GetFindingAsync(Guid auditId, Guid findingId, Guid ownerUserId, CancellationToken cancellationToken);
}
