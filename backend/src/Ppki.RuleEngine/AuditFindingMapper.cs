using System.Text.Json;
using Ppki.Domain;

namespace Ppki.RuleEngine;

public static class AuditFindingMapper
{
    public static IReadOnlyList<AuditFinding> Map(Guid auditJobId, DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        return validation.Findings.Select(resolvedFinding =>
        {
            var snapshot = resolvedFinding.Snapshot;
            var result = resolvedFinding.Finding;
            var source = SourceReference(snapshot);
            return new AuditFinding
            {
                AuditJobId = auditJobId,
                RuleId = snapshot.RuleId,
                Severity = snapshot.Severity,
                RuleCodeSnapshot = snapshot.RuleCode,
                FixModeSnapshot = snapshot.FixMode,
                SourceSectionSnapshot = source.SourceSection,
                PdfPageSnapshot = source.PdfPage,
                PrintedPageSnapshot = source.PrintedPage,
                Message = result.MessageKey,
                ActualValueJson = JsonSerializer.Serialize(result.Actual),
                ExpectedValueJson = JsonSerializer.Serialize(result.Expected),
                LocationJson = JsonSerializer.Serialize(result.Location),
                Confidence = result.Confidence
            };
        }).ToArray();
    }

    private static SnapshotSourceReference SourceReference(AuditRuleSnapshot snapshot)
    {
        using var source = JsonDocument.Parse(snapshot.SourceReferenceJson);
        var root = source.RootElement;
        return new(
            NullableString(root, "sourceSection"),
            NullableInt(root, "pdfPage"),
            NullableString(root, "printedPage"));
    }

    private static string? NullableString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static int? NullableInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : null;

    private sealed record SnapshotSourceReference(string? SourceSection, int? PdfPage, string? PrintedPage);
}
