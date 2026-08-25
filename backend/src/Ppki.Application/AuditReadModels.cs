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

public sealed record CorrectionAnalysisReadinessDto(string State);

public enum ReviewReadinessState { AuditInProgress, NeedsFix, ReadyForReview, Unknown }
public enum ReviewReadinessReason { AuditFailed, AuditCancelled, PolicyUnknown, NoApplicableRules }

public sealed record ReviewReadinessSnapshot(
    int SnapshotSchemaVersion,
    ReviewBlockingPolicy? ReviewBlockingPolicy,
    string? ReadinessPolicyVersion);

public sealed record ReviewReadinessFinding(
    ReviewBlockingPolicy? ReviewBlockingPolicy,
    FindingStatus FindingStatus,
    FindingResolutionEventType? LatestResolution,
    FindingReviewEventType? LatestReview);

public sealed record ReviewReadinessResult(
    ReviewReadinessState State,
    ReviewReadinessReason? Reason,
    int BlockingFindingCount,
    string? PolicyVersion);

public static class ReviewReadinessProjection
{
    public static ReviewReadinessResult Resolve(
        AuditJobStatus status,
        int applicableRuleCount,
        IEnumerable<ReviewReadinessSnapshot> snapshots,
        IEnumerable<ReviewReadinessFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(findings);
        if (status is AuditJobStatus.Queued or AuditJobStatus.Processing)
            return new(ReviewReadinessState.AuditInProgress, null, 0, null);
        if (status == AuditJobStatus.Failed)
            return new(ReviewReadinessState.Unknown, ReviewReadinessReason.AuditFailed, 0, null);
        if (status == AuditJobStatus.Cancelled)
            return new(ReviewReadinessState.Unknown, ReviewReadinessReason.AuditCancelled, 0, null);
        if (applicableRuleCount == 0)
            return new(ReviewReadinessState.Unknown, ReviewReadinessReason.NoApplicableRules, 0, null);

        var policies = snapshots.ToArray();
        var versions = policies.Select(value => value.ReadinessPolicyVersion)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (policies.Length != applicableRuleCount
            || policies.Any(value => value.SnapshotSchemaVersion < 2
                || value.ReviewBlockingPolicy is not (ReviewBlockingPolicy.Blocking or ReviewBlockingPolicy.NonBlocking)
                || string.IsNullOrWhiteSpace(value.ReadinessPolicyVersion))
            || versions.Length != 1)
            return new(ReviewReadinessState.Unknown, ReviewReadinessReason.PolicyUnknown, 0, null);

        var blockingCount = findings.Count(value =>
            value.ReviewBlockingPolicy == ReviewBlockingPolicy.Blocking
            && value.LatestResolution != FindingResolutionEventType.VerificationResolvedObserved);
        return blockingCount > 0
            ? new(ReviewReadinessState.NeedsFix, null, blockingCount, versions[0])
            : new(ReviewReadinessState.ReadyForReview, null, 0, versions[0]);
    }
}

public enum AuditFindingDisposition { Resolved, Ignored, RequiresReview }

public sealed record AuditFindingDispositionSummaryDto(
    int ResolvedCount,
    int AutomaticallyResolvedCount,
    int IgnoredCount,
    int RequiresReviewCount)
{
    public static AuditFindingDispositionSummaryDto Create(
        int totalCount, int resolvedCount, int automaticallyResolvedCount,
        int ignoredCount, int requiresReviewCount)
    {
        if (totalCount < 0 || resolvedCount < 0 || automaticallyResolvedCount < 0
            || ignoredCount < 0 || requiresReviewCount < 0
            || automaticallyResolvedCount > resolvedCount
            || resolvedCount + ignoredCount + requiresReviewCount != totalCount)
            throw new InvalidOperationException("Finding disposition counts are incoherent.");
        return new(resolvedCount, automaticallyResolvedCount, ignoredCount, requiresReviewCount);
    }
}

public static class AuditFindingDispositionProjection
{
    public static AuditFindingDisposition Resolve(
        FindingStatus findingState,
        FindingResolutionEventType? latestResolution,
        FindingReviewEventType? latestReview)
    {
        if (findingState == FindingStatus.Fixed
            || latestResolution == FindingResolutionEventType.VerificationResolvedObserved)
            return AuditFindingDisposition.Resolved;
        if (findingState == FindingStatus.Ignored
            || latestReview is FindingReviewEventType.Ignored or FindingReviewEventType.AcceptedRisk)
            return AuditFindingDisposition.Ignored;
        return AuditFindingDisposition.RequiresReview;
    }
}

public static class TextCorrectionAnalysisReadiness
{
    public const string AwaitingAnalysis = "AwaitingAnalysis";

    public static string Resolve(
        TextCorrectionAnalysisState? persistedState,
        AuditJobStatus auditStatus,
        bool isCurrentDocumentVersion,
        bool hasEligibleLineage)
    {
        if (persistedState is not null) return persistedState.Value.ToString();
        return auditStatus == AuditJobStatus.Completed && isCurrentDocumentVersion && hasEligibleLineage
            ? AwaitingAnalysis
            : TextCorrectionAnalysisState.Skipped.ToString();
    }
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
    string? ErrorMessage,
    AuditFindingDispositionSummaryDto FindingDispositions,
    AutomaticRemediationHistoryDto? AutomaticRemediationHistory,
    CorrectionAnalysisReadinessDto CorrectionAnalysis,
    AutomaticRemediationSummaryDto? AutomaticRemediation = null,
    DocumentRenderStateDto? DocumentRender = null,
    int ProfileVersionNo = 0,
    int BlockingFindingCount = 0,
    string ReadinessState = "Unknown",
    string? ReadinessReason = null,
    string? ReadinessPolicyVersion = null);

public sealed record AuditFindingSourceDto(
    string? SourceSection,
    int? PdfPage,
    string? PrintedPage);

public sealed record AuditFindingPresentationDto(
    string Kind,
    string PropertyLabel,
    string Problem,
    string BeforeLabel,
    string? BeforeValue,
    string ExpectedLabel,
    string? ExpectedValue,
    string EvidenceState);

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
    string Disposition,
    string ResolutionState,
    string ReviewState,
    string ReasonCode,
    string Message,
    AuditFindingPresentationDto Presentation,
    JsonElement Actual,
    JsonElement Expected,
    JsonElement Location,
    decimal? Confidence,
    AuditFindingSourceDto Source,
    string ActionAvailability,
    FindingPageLocationDto? PageLocation,
    FixEligibilityStatus Eligibility,
    FixEligibilityReasonCode EligibilityReason,
    bool RequiresExplicitApproval);

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
    string Disposition,
    string ResolutionState,
    string ReviewState,
    string ReasonCode,
    string Message,
    AuditFindingPresentationDto Presentation,
    JsonElement Actual,
    JsonElement Expected,
    JsonElement Location,
    decimal? Confidence,
    AuditFindingSourceDto Source,
    string ActionAvailability,
    FindingPageLocationDto? PageLocation,
    FixEligibilityStatus Eligibility,
    FixEligibilityReasonCode EligibilityReason,
    bool RequiresExplicitApproval);

public sealed record AuditFindingPageDto(
    Guid AuditId,
    Guid DocumentVersionId,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AuditFindingListItemDto> Items);

public sealed record AuditFindingQuery(
    RuleSeverity? Severity,
    FixMode? FixMode,
    AuditFindingDisposition? Disposition,
    bool? AutomaticallyResolved,
    string? Domain,
    string? RuleCode,
    string? ValidationKey,
    int Page,
    int PageSize,
    string? Search = null)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
    public const int MaximumPage = 10_000;
    public const int MaximumFindingCount = 10_000;

    public static bool TryCreate(
        string? severity,
        string? fixMode,
        string? disposition,
        bool? automaticallyResolved,
        string? domain,
        string? ruleCode,
        string? validationKey,
        string? search,
        string? sort,
        int? page,
        int? pageSize,
        out AuditFindingQuery query,
        out string? errorCode)
    {
        query = null!;
        errorCode = null;
        if (!TryEnum(severity, out RuleSeverity? parsedSeverity)
            || !TryEnum(fixMode, out FixMode? parsedFixMode)
            || !TryEnum(disposition, out AuditFindingDisposition? parsedDisposition))
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
            || !TryFilter(validationKey, 256, out var normalizedValidationKey)
            || !TryFilter(search, 128, out var normalizedSearch))
        {
            errorCode = "finding-filter-text-invalid";
            return false;
        }

        query = new(parsedSeverity, parsedFixMode, parsedDisposition, automaticallyResolved, normalizedDomain,
            normalizedRuleCode, normalizedValidationKey, selectedPage, selectedPageSize, normalizedSearch);
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
