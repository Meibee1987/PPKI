using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

public sealed class FixPlanConfigurationException(string diagnosticCode) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanState>))]
public enum FixPlanState
{
    Ready,
    PartiallyReady,
    NotAvailable,
    InvalidSelection,
    InvalidSnapshot,
    Conflict,
    AuditIncomplete,
    InvalidConfiguration
}

[JsonConverter(typeof(JsonStringEnumConverter<FixPlanItemDisposition>))]
public enum FixPlanItemDisposition
{
    Planned,
    Unsupported,
    Conflict,
    InvalidSnapshot
}

[JsonConverter(typeof(JsonStringEnumConverter<FixOperationKind>))]
public enum FixOperationKind
{
    SetProperty,
    ReplaceStructuralValue
}

public sealed record FixPlanPreviewRequest(string[]? FindingIds);

public sealed record FixPlanSelection(IReadOnlyList<Guid> FindingIds)
{
    public const int MaximumFindingCount = 100;

    public static bool TryCreate(
        IEnumerable<string>? findingIds,
        out FixPlanSelection selection,
        out string? errorCode)
    {
        selection = null!;
        errorCode = null;
        if (findingIds is null)
        {
            errorCode = "fix-plan-selection-empty";
            return false;
        }

        var values = findingIds.ToArray();
        if (values.Length is 0)
        {
            errorCode = "fix-plan-selection-empty";
            return false;
        }
        if (values.Length > MaximumFindingCount)
        {
            errorCode = "fix-plan-selection-too-large";
            return false;
        }

        var parsed = new List<Guid>(values.Length);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var id) || id == Guid.Empty)
            {
                errorCode = "fix-plan-selection-id-invalid";
                return false;
            }
            parsed.Add(id);
        }

        selection = new(parsed.Distinct().Order().ToArray());
        return true;
    }
}

public sealed record FixPlanSource(
    Guid AuditId,
    AuditJobStatus AuditStatus,
    Guid DocumentVersionId,
    string SourceVersionSha256,
    string? ResolvedRuleSetHash,
    DocumentKind? DocumentKindSnapshot,
    IReadOnlyList<FixPlanFindingSnapshot> Findings);

public sealed record FixPlanFindingSnapshot(
    Guid FindingId,
    int RuleOrdinal,
    string RuleCode,
    string Domain,
    string Element,
    string ValidationKey,
    RuleSeverity Severity,
    FixMode FixMode,
    FindingStatus FindingState,
    string ActualJson,
    string ExpectedJson,
    string LocationJson,
    int SnapshotSchemaVersion,
    string? SourceReferenceJson = null);

public sealed record FixPlanItem(
    Guid FindingId,
    string RuleCode,
    string ValidationKey,
    int RuleOrdinal,
    FixPlanItemDisposition Disposition,
    string DiagnosticCode);

public sealed record FixTargetLocation(
    string Scope,
    int? BodyElementIndex,
    int? SectionIndex,
    int? ParagraphIndex,
    int? RunIndex);

public sealed record FixExpectedValueDescriptor(string Type, string Value);

public sealed record FixPlanOperation(
    FixOperationKind OperationKind,
    string CapabilityId,
    string CapabilityVersion,
    string RuleCode,
    string ValidationKey,
    IReadOnlyList<Guid> SourceFindingIds,
    FixTargetLocation Target,
    string PropertyIdentifier,
    FixExpectedValueDescriptor Expected,
    bool RequiresConfirmation,
    int Ordinal,
    string PreconditionCode,
    string SummaryCode);

public sealed record FixPlanConflict(
    string TargetKey,
    IReadOnlyList<Guid> FindingIds,
    string DiagnosticCode);

public sealed record FixPlanPreview(
    Guid AuditId,
    Guid SourceDocumentVersionId,
    string SourceDocumentVersionSha256,
    string ResolvedRuleSetHash,
    string DocumentKindSnapshot,
    string PlannerVersion,
    int SelectedFindingCount,
    int PlannedFindingCount,
    int UnsupportedFindingCount,
    int ConflictFindingCount,
    int InvalidFindingCount,
    IReadOnlyList<FixPlanItem> Items,
    IReadOnlyList<FixPlanOperation> Operations,
    IReadOnlyList<FixPlanConflict> Conflicts,
    string PlanHash,
    FixPlanState State,
    IReadOnlyList<string> Diagnostics);

public interface IFixPlanSourceReader
{
    Task<FixPlanSource?> LoadAsync(
        Guid auditId,
        Guid ownerUserId,
        FixPlanSelection selection,
        CancellationToken cancellationToken);
}

public interface IFixPlanPreviewPlanner
{
    FixPlanPreview Create(FixPlanSource source);
}

public interface IFixPlanPreviewService
{
    Task<FixPlanPreview?> PreviewAsync(
        Guid auditId,
        Guid ownerUserId,
        FixPlanSelection selection,
        CancellationToken cancellationToken);
}

public sealed class FixPlanPreviewService(
    IFixPlanSourceReader sourceReader,
    IFixPlanPreviewPlanner planner) : IFixPlanPreviewService
{
    public async Task<FixPlanPreview?> PreviewAsync(
        Guid auditId,
        Guid ownerUserId,
        FixPlanSelection selection,
        CancellationToken cancellationToken)
    {
        var source = await sourceReader.LoadAsync(auditId, ownerUserId, selection, cancellationToken);
        return source is null ? null : planner.Create(source);
    }
}
