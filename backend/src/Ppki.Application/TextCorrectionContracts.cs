using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

public sealed class TextCorrectionException(string diagnosticCode) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

public sealed record TextCorrectionProposalQuery(int Page = 1, int PageSize = 25)
{
    public const int MaximumPageSize = 100;
    public static bool TryCreate(int? page, int? pageSize, out TextCorrectionProposalQuery query)
    {
        query = new(page ?? 1, pageSize ?? 25);
        return query.Page > 0 && query.PageSize is > 0 and <= MaximumPageSize;
    }
}

public sealed record TextCorrectionPageLocation(int? PageNumber, string Confidence);
public sealed record EffectiveTextCorrectionDecision(Guid Id, int Sequence, string Action, Guid ActorUserId);
public sealed record TextCorrectionProposalItem(
    Guid Id, string DetectorRule, string Category, string State, bool SuggestionAvailable,
    TextCorrectionPageLocation? PageLocation, string AnchorStatus,
    EffectiveTextCorrectionDecision? EffectiveDecision);
public sealed record TextCorrectionProposalSummary(
    int UndecidedCount, int UseSuggestionCount, int EditManualCount, int IgnoredCount,
    int EligibleDecisionCount, int HistoricalCount);
public sealed record TextCorrectionProposalPage(
    Guid AuditId, Guid DocumentVersionId, int Page, int PageSize, int TotalCount,
    IReadOnlyList<TextCorrectionProposalItem> Items, TextCorrectionProposalSummary Summary,
    TextCorrectionBatchStatus? ActiveBatch);

public sealed record TextCorrectionProposalContext(
    Guid ProposalId, Guid DocumentVersionId, string AnchorStatus, string? SafeFailureCode,
    string? TargetText, string? Context, string SuggestedReplacement,
    int? TargetOffsetInContext, bool PrefixTruncated, bool SuffixTruncated,
    TextCorrectionPageLocation? PageLocation)
{
    public override string ToString() =>
        $"TextCorrectionProposalContext(ProposalId={ProposalId:D},Content=[REDACTED])";
}

public sealed record TextCorrectionDecisionRequest(
    TextCorrectionDecisionAction Action,
    string? ManualReplacement = null);
public sealed record TextCorrectionDecisionAccepted(
    Guid Id, Guid ProposalId, int Sequence, string Action, Guid ActorUserId,
    DateTimeOffset CreatedAt, bool Replayed);

public sealed record TextCorrectionBatchRequest(IReadOnlyList<Guid>? DecisionIds = null);
public sealed record TextCorrectionBatchAccepted(
    Guid Id, Guid SourceAuditId, Guid SourceDocumentVersionId, Guid? FixExecutionId,
    string State, int DecisionCount, bool Replayed);
public sealed record TextCorrectionBatchStatus(
    Guid Id, Guid SourceAuditId, Guid SourceDocumentVersionId, Guid? FixExecutionId,
    Guid? ResultDocumentVersionId, Guid? ReauditId, string State, int DecisionCount,
    string? SafeFailureCode, IReadOnlyDictionary<string, int> VerificationCounts);

public interface ITextCorrectionService
{
    Task<TextCorrectionProposalPage?> ListAsync(Guid auditId, Guid actorUserId,
        TextCorrectionProposalQuery query, CancellationToken cancellationToken);
    Task<TextCorrectionProposalContext?> ContextAsync(Guid proposalId, Guid actorUserId,
        CancellationToken cancellationToken);
    Task<TextCorrectionDecisionAccepted?> DecideAsync(Guid proposalId, Guid actorUserId,
        Guid idempotencyKey, TextCorrectionDecisionRequest request, CancellationToken cancellationToken);
    Task<TextCorrectionBatchAccepted?> CreateBatchAsync(Guid auditId, Guid actorUserId,
        Guid idempotencyKey, TextCorrectionBatchRequest request, CancellationToken cancellationToken);
    Task<TextCorrectionBatchStatus?> GetBatchAsync(Guid batchId, Guid actorUserId,
        CancellationToken cancellationToken);
}

public sealed record TextCorrectionExecutionReference(
    int Ordinal, Guid DecisionId, string AnchorHash, string ReplacementHash);
public sealed record ApprovedTextCorrectionExecutionPlan(
    string SchemaVersion, Guid BatchId, Guid SourceAuditId, Guid SourceDocumentVersionId,
    string SourceSha256, IReadOnlyList<TextCorrectionExecutionReference> Operations);

public static class ApprovedTextCorrectionExecutionPlanSerializer
{
    public const string SchemaVersion = "text-correction-execution-plan/1.0";
    public const string PlannerVersion = "text-correction-batch/1.0";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(ApprovedTextCorrectionExecutionPlan plan)
    {
        Validate(plan);
        return JsonSerializer.Serialize(plan, Options);
    }

    public static ApprovedTextCorrectionExecutionPlan Deserialize(string json)
    {
        var plan = JsonSerializer.Deserialize<ApprovedTextCorrectionExecutionPlan>(json, Options)
            ?? throw new TextCorrectionException("correction-plan-invalid");
        Validate(plan);
        return plan;
    }

    public static string Hash(ApprovedTextCorrectionExecutionPlan plan) => HashFields(
        plan.SchemaVersion, plan.BatchId.ToString("D"), plan.SourceAuditId.ToString("D"),
        plan.SourceDocumentVersionId.ToString("D"), plan.SourceSha256,
        string.Join("\n", plan.Operations.OrderBy(value => value.Ordinal).Select(value =>
            $"{value.Ordinal.ToString(CultureInfo.InvariantCulture)}:{value.DecisionId:D}:{value.AnchorHash}:{value.ReplacementHash}")));

    public static string HashFields(params string[] values) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Concat(values.Select(value =>
            Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value)))));

    private static void Validate(ApprovedTextCorrectionExecutionPlan plan)
    {
        if (plan.SchemaVersion != SchemaVersion || plan.BatchId == Guid.Empty
            || plan.SourceAuditId == Guid.Empty || plan.SourceDocumentVersionId == Guid.Empty
            || !IsHash(plan.SourceSha256) || plan.Operations.Count is < 1 or > 100
            || plan.Operations.Select(value => value.Ordinal).Order().Where((value, index) => value != index + 1).Any()
            || plan.Operations.Any(value => value.DecisionId == Guid.Empty
                || !IsHash(value.AnchorHash) || !IsHash(value.ReplacementHash)))
            throw new TextCorrectionException("correction-plan-invalid");
    }

    private static bool IsHash(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
