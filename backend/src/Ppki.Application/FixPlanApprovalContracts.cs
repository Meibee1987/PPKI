using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

public sealed record FixPlanApprovalRequest(string[]? ApprovedConfirmItemIds);

public sealed record FixPlanApprovalDto(
    Guid PlanId,
    Guid AuditId,
    Guid SourceDocumentVersionId,
    FixPlanLifecycleState State,
    string SnapshotSchemaVersion,
    string PlanHash,
    string SourceVersionSha256,
    Guid ApprovedByUserId,
    DateTimeOffset ApprovedAt,
    int ItemCount,
    bool Replayed);

public sealed class FixPlanApprovalException(string diagnosticCode) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

public sealed record FixPlanApprovalItemMaterial(
    Guid ItemId,
    FixPlanFindingSnapshot Finding,
    decimal? Confidence,
    string? SourceSectionSnapshot,
    int? PdfPageSnapshot,
    string? PrintedPageSnapshot,
    bool RequiresExplicitApproval,
    string CapabilityId,
    string CapabilityVersion,
    FixPlanOperation Operation,
    FixPlanDraftPreviewItemDto PreviewItem,
    FixPlanDraftBeforeAfterDto Change,
    FixPlanMutationAnalysisItemDto Analysis);

public sealed record FixPlanApprovalEvaluation(
    FixPlanDraftPreviewDto Preview,
    IReadOnlyList<FixPlanApprovalItemMaterial> Items);

public interface IFixPlanApprovalPreviewBuilder
{
    FixPlanApprovalEvaluation Build(FixPlanDraftAggregate aggregate, Guid auditId);
}

public sealed record FixPlanApprovalPrepared(
    string SchemaVersion,
    string PlanHash,
    string ApprovalRequestHash,
    string SourceVersionSha256,
    string SnapshotJson,
    int ItemCount);

public sealed record FixPlanApprovalWriteResult(
    FixPlanRecord? Plan,
    FixPlanApprovalSnapshotRecord? Snapshot,
    bool Replayed,
    string? ConflictCode = null);

public interface IFixPlanApprovalRepository
{
    Task<FixPlanApprovalWriteResult> ApproveAsync(Guid auditId, Guid planId, Guid ownerUserId,
        string approvalRequestHash, DateTimeOffset now,
        Func<FixPlanDraftAggregate, FixPlanApprovalPrepared> prepare,
        CancellationToken cancellationToken);
}

public interface IFixPlanApprovalService
{
    Task<FixPlanApprovalDto?> ApproveAsync(Guid auditId, Guid planId, Guid ownerUserId,
        IReadOnlyList<Guid> approvedConfirmItemIds, CancellationToken cancellationToken);
}

public sealed record ApprovedFixPlanSnapshot(
    string SchemaVersion,
    Guid PlanId,
    Guid AuditId,
    Guid SourceDocumentVersionId,
    string SourceVersionSha256,
    string ResolvedRuleSetHash,
    string DocumentKindSnapshot,
    string PreviewSchemaVersion,
    string MutationAnalysisSchemaVersion,
    string PlanHash,
    IReadOnlyList<ApprovedFixPlanItemSnapshot> Items,
    FixPlanMutationAnalysisDto MutationAnalysis,
    Guid ApprovedByUserId,
    DateTimeOffset ApprovedAt);

public sealed record ApprovedFixPlanItemSnapshot(
    Guid ItemId,
    Guid FindingId,
    int RuleOrdinal,
    string RuleCode,
    string ValidationKey,
    RuleSeverity Severity,
    FixMode FixMode,
    FindingStatus FindingState,
    decimal? Confidence,
    string? SourceSectionSnapshot,
    int? PdfPageSnapshot,
    string? PrintedPageSnapshot,
    string? SourceReferenceJson,
    string ActualValueJson,
    string ExpectedValueJson,
    string LocationJson,
    int RuleSnapshotSchemaVersion,
    FixEligibilityStatus Eligibility,
    FixEligibilityReasonCode EligibilityReason,
    bool RequiresExplicitApproval,
    FixPlanDraftPreviewItemState PreviewState,
    string PreviewReasonCode,
    bool ExplicitlyApproved,
    string CapabilityId,
    string CapabilityVersion,
    FixPlanOperation Operation,
    FixPlanDraftBeforeAfterDto Preview,
    FixPlanMutationAnalysisItemDto MutationAnalysis);

public static class FixPlanApprovalSnapshotSerializer
{
    public const string SchemaVersion = "fix-plan-approved-snapshot/1.0";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static FixPlanApprovalPrepared Create(FixPlanDraftAggregate aggregate,
        FixPlanApprovalEvaluation evaluation, IReadOnlySet<Guid> approvedConfirmIds,
        Guid approverUserId, DateTimeOffset approvedAt)
    {
        var preview = evaluation.Preview;
        var ordered = evaluation.Items.OrderBy(value => value.Analysis.ExecutionOrdinal ?? int.MaxValue)
            .ThenBy(value => value.ItemId).Select(value => new ApprovedFixPlanItemSnapshot(
                value.ItemId, value.Finding.FindingId, value.Finding.RuleOrdinal, value.Finding.RuleCode,
                value.Finding.ValidationKey, value.Finding.Severity, value.Finding.FixMode,
                value.Finding.FindingState, value.Confidence, value.SourceSectionSnapshot, value.PdfPageSnapshot,
                value.PrintedPageSnapshot, value.Finding.SourceReferenceJson,
                value.Finding.ActualJson, value.Finding.ExpectedJson, value.Finding.LocationJson,
                value.Finding.SnapshotSchemaVersion, value.PreviewItem.Eligibility,
                value.PreviewItem.EligibilityReason, value.RequiresExplicitApproval,
                value.PreviewItem.PreviewState, value.PreviewItem.ReasonCode,
                value.Finding.FixMode == FixMode.Auto
                    || approvedConfirmIds.Contains(value.ItemId), value.CapabilityId, value.CapabilityVersion,
                value.Operation, value.Change, value.Analysis)).ToArray();
        var analysis = preview.MutationAnalysis ?? throw new FixPlanApprovalException("fix-plan-approval-analysis-missing");
        var content = new
        {
            schemaVersion = SchemaVersion,
            planId = preview.PlanId,
            auditId = preview.AuditId,
            sourceDocumentVersionId = preview.SourceDocumentVersionId,
            sourceVersionSha256 = preview.SourceVersionSha256,
            resolvedRuleSetHash = aggregate.Source.Audit.ResolvedRuleSetHash,
            documentKindSnapshot = aggregate.Source.Audit.DocumentKindSnapshot!.Value.ToString(),
            previewSchemaVersion = preview.SchemaVersion,
            mutationAnalysisSchemaVersion = analysis.SchemaVersion,
            items = ordered,
            mutationAnalysis = analysis
        };
        var planHash = Sha(JsonSerializer.Serialize(content, Options));
        var snapshot = new ApprovedFixPlanSnapshot(SchemaVersion, preview.PlanId, preview.AuditId,
            preview.SourceDocumentVersionId, preview.SourceVersionSha256,
            aggregate.Source.Audit.ResolvedRuleSetHash!, aggregate.Source.Audit.DocumentKindSnapshot.Value.ToString(),
            preview.SchemaVersion, analysis.SchemaVersion, planHash, ordered, analysis, approverUserId, approvedAt);
        return new(SchemaVersion, planHash, ApprovalRequestHash(approvedConfirmIds),
            preview.SourceVersionSha256, JsonSerializer.Serialize(snapshot, Options), ordered.Length);
    }

    public static string ApprovalRequestHash(IEnumerable<Guid> ids) => Sha(string.Join('\n',
        ids.Distinct().Order().Select(value => value.ToString("D"))));

    private static string Sha(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
