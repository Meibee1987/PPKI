using Ppki.Domain;

namespace Ppki.Application;

public enum AutomaticRemediationPolicyOutcome { AutoApply, ApprovalRequired, ManualOnly }

public sealed record AutomaticRemediationCapabilityContract(
    string RuleCode,
    string ValidationKey,
    string CapabilityId,
    string CapabilityVersion);

public static class AutomaticRemediationPolicy
{
    public const string Version = "auto-format/1.0";
    public const string OrchestrationType = "AutoFormat";

    private static readonly IReadOnlyDictionary<string, AutomaticRemediationCapabilityContract> Allowlist =
        new[]
        {
            Contract("PPKI-LAY-005", "body.font-times-new-roman-12", "body-font-direct-run"),
            Contract("PPKI-LAY-017", "body.line-spacing-single", "body-line-spacing-direct-paragraph"),
            Contract("PPKI-LAY-018", "body.first-line-indent-1cm", "body-first-line-indent-direct-paragraph"),
            Contract("PPKI-LAY-019", "body.justified", "body-justified-direct-paragraph"),
            Contract("PPKI-ABS-011", "abstract.skripsi-single-spacing-zero-paragraph-spacing", "abstract-spacing-direct-paragraph"),
            Contract("PPKI-ABS-019", "abstract-summary-single-spacing-zero-paragraph-spacing", "abstract-spacing-direct-paragraph"),
            Contract("PPKI-HDG-006", "heading.chapter-centered", "chapter-centered-direct-paragraph")
        }.ToDictionary(value => Key(value.RuleCode, value.ValidationKey), StringComparer.Ordinal);

    public static IReadOnlyCollection<AutomaticRemediationCapabilityContract> Contracts => Allowlist.Values.ToArray();

    public static AutomaticRemediationPolicyOutcome Classify(FixPlanFindingSnapshot finding) =>
        TryGetAutoApply(finding, out _) ? AutomaticRemediationPolicyOutcome.AutoApply : AutomaticRemediationPolicyOutcome.ManualOnly;

    public static bool TryGetAutoApply(
        FixPlanFindingSnapshot finding,
        out AutomaticRemediationCapabilityContract contract)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.FindingState != FindingStatus.Open || finding.SnapshotSchemaVersion < 1)
        {
            contract = null!;
            return false;
        }
        return Allowlist.TryGetValue(Key(finding.RuleCode, finding.ValidationKey), out contract!);
    }

    public static bool IsAutoApply(string ruleCode, string validationKey, FindingStatus state, int snapshotSchemaVersion) =>
        state == FindingStatus.Open && snapshotSchemaVersion >= 1
        && Allowlist.ContainsKey(Key(ruleCode, validationKey));

    private static AutomaticRemediationCapabilityContract Contract(
        string ruleCode, string validationKey, string capabilityId) =>
        new(ruleCode, validationKey, capabilityId, "1.0");

    private static string Key(string ruleCode, string validationKey) => $"{ruleCode}\n{validationKey}";
}

public sealed record AutomaticRemediationSummaryDto(
    string State,
    string PolicyVersion,
    int EligibleFindingCount,
    int OperationCount,
    int VerifiedResolvedCount,
    int StillDetectedCount,
    string? FailureCode,
    Guid? ResultDocumentVersionId,
    Guid? ReauditJobId);

public sealed record AutomaticRemediationHistoryDto(
    Guid SourceAuditJobId,
    int OperationCount,
    int VerifiedResolvedCount,
    int StillDetectedCount);
