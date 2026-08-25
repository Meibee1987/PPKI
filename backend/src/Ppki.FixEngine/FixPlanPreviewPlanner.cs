using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.FixEngine;

public enum FixCapabilityDependencyKind
{
    RequiresBefore,
    RequiresAfter
}

public sealed record FixCapabilityDependency(
    string CapabilityId,
    string CapabilityVersion,
    FixCapabilityDependencyKind Kind);

public sealed record RemediationCapability(
    string CapabilityId,
    string CapabilityVersion,
    string ValidationKey,
    FixOperationKind OperationKind,
    IReadOnlyList<string> RequiredSnapshotFields,
    bool RequiresConfirmation,
    bool DocumentMutationImplementationExists,
    string PreviewProviderId,
    string DescriptionCode,
    bool AllowsIdenticalOperationMerge,
    IFixPreviewProvider Provider,
    IReadOnlyList<FixCapabilityDependency>? Dependencies = null);

public sealed record FixOperationDraft(
    FixTargetLocation Target,
    string PropertyIdentifier,
    FixExpectedValueDescriptor Expected,
    string PreconditionCode,
    string SummaryCode);

public interface IFixPreviewProvider
{
    bool TryCreate(
        FixPlanFindingSnapshot finding,
        out FixOperationDraft operation,
        out string diagnosticCode);

    bool TryCreateBeforeAfter(
        FixPlanFindingSnapshot finding,
        FixOperationDraft operation,
        out FixPlanDraftBeforeAfterDto preview)
    {
        preview = null!;
        if (!TryCreate(finding, out var authoritative, out _)
            || authoritative != operation)
            return false;

        var presentation = AuditFindingPresentation.Create(finding.ActualJson, finding.ExpectedJson);
        if (presentation.EvidenceState == "Unavailable") return false;
        preview = new(presentation.Kind, presentation.PropertyLabel,
            presentation.BeforeLabel, presentation.BeforeValue,
            "Setelah", presentation.ExpectedValue, presentation.EvidenceState);
        return true;
    }
}

public interface IRemediationCapabilityRegistry
{
    IReadOnlyList<RemediationCapability> Capabilities { get; }
    bool TryGet(string validationKey, out RemediationCapability capability);
}

public sealed class RemediationCapabilityRegistry : IRemediationCapabilityRegistry
{
    private static readonly Regex Identifier = new("^[a-z0-9][a-z0-9.-]{0,127}$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, RemediationCapability> byValidationKey;

    public RemediationCapabilityRegistry(IEnumerable<RemediationCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var supplied = capabilities.ToArray();
        foreach (var capability in supplied) Validate(capability);
        var ordered = supplied
            .Select(value => value with
            {
                RequiredSnapshotFields = Array.AsReadOnly(value.RequiredSnapshotFields.ToArray()),
                Dependencies = Array.AsReadOnly((value.Dependencies ?? []).OrderBy(item => item.CapabilityId, StringComparer.Ordinal)
                    .ThenBy(item => item.CapabilityVersion, StringComparer.Ordinal).ThenBy(item => item.Kind).ToArray())
            })
            .OrderBy(value => value.ValidationKey, StringComparer.Ordinal)
            .ToArray();
        if (ordered.GroupBy(value => value.ValidationKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new FixPlanConfigurationException("fix-capability-validation-key-duplicate");
        Capabilities = Array.AsReadOnly(ordered);
        byValidationKey = ordered.ToDictionary(value => value.ValidationKey, StringComparer.Ordinal);
    }

    public IReadOnlyList<RemediationCapability> Capabilities { get; }

    public bool TryGet(string validationKey, out RemediationCapability capability) =>
        byValidationKey.TryGetValue(validationKey, out capability!);

    public static RemediationCapabilityRegistry Empty() => new([]);

    private static void Validate(RemediationCapability value)
    {
        if (value.Provider is null
            || !Identifier.IsMatch(value.CapabilityId ?? string.Empty)
            || !Identifier.IsMatch(value.CapabilityVersion ?? string.Empty)
            || !Identifier.IsMatch(value.ValidationKey ?? string.Empty)
            || !Identifier.IsMatch(value.PreviewProviderId ?? string.Empty)
            || !Identifier.IsMatch(value.DescriptionCode ?? string.Empty)
            || value.RequiredSnapshotFields is null
            || value.RequiredSnapshotFields.Any(field => !Identifier.IsMatch(field ?? string.Empty))
            || value.RequiredSnapshotFields.Distinct(StringComparer.Ordinal).Count() != value.RequiredSnapshotFields.Count)
            throw new FixPlanConfigurationException("fix-capability-configuration-invalid");
        var dependencies = value.Dependencies ?? [];
        if (dependencies.Any(item => !Identifier.IsMatch(item.CapabilityId ?? string.Empty)
                || !Identifier.IsMatch(item.CapabilityVersion ?? string.Empty)
                || !Enum.IsDefined(item.Kind))
            || dependencies.Distinct().Count() != dependencies.Count)
            throw new FixPlanConfigurationException("fix-capability-dependency-configuration-invalid");
    }
}

public sealed class DeterministicFixPlanPreviewPlanner(
    IRemediationCapabilityRegistry registry) : IFixPlanPreviewPlanner
{
    public const string PlannerVersion = "fix-plan-preview/1.0";
    private const int MaximumJsonLength = 16_384;
    private static readonly Regex SafeCode = new("^[a-z0-9][a-z0-9.-]{0,127}$", RegexOptions.CultureInvariant);

    public FixPlanPreview Create(FixPlanSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ordered = source.Findings
            .OrderBy(value => value.RuleOrdinal)
            .ThenBy(value => value.RuleCode, StringComparer.Ordinal)
            .ThenBy(value => value.LocationJson, StringComparer.Ordinal)
            .ThenBy(value => value.FindingId)
            .ToArray();

        if (ordered.Length == 0)
            return Terminal(source, ordered, FixPlanState.InvalidSelection, "fix-plan-selection-empty");
        if (source.AuditStatus != AuditJobStatus.Completed)
            return Terminal(source, ordered, FixPlanState.AuditIncomplete, "fix-plan-audit-incomplete");
        if (ordered.Select(value => value.FindingId).Distinct().Count() != ordered.Length)
            return Terminal(source, ordered, FixPlanState.InvalidSnapshot, "fix-plan-finding-identity-duplicate");
        if (!ValidSha(source.SourceVersionSha256)
            || !ValidSha(source.ResolvedRuleSetHash)
            || source.DocumentKindSnapshot is null)
            return Terminal(source, ordered, FixPlanState.InvalidSnapshot, "fix-plan-source-snapshot-invalid");

        var items = new List<FixPlanItem>(ordered.Length);
        var candidates = new List<OperationCandidate>();
        var identities = new List<SnapshotIdentity>(ordered.Length);

        foreach (var finding in ordered)
        {
            if (!TrySnapshotIdentity(finding, out var identity))
            {
                items.Add(Item(finding, FixPlanItemDisposition.InvalidSnapshot, "fix-plan-finding-snapshot-invalid"));
                identities.Add(SnapshotIdentity.Invalid(finding));
                continue;
            }
            identities.Add(identity);

            if (!registry.TryGet(finding.ValidationKey, out var capability))
            {
                items.Add(Item(finding, FixPlanItemDisposition.Unsupported, "fix-capability-not-registered"));
                continue;
            }

            if (!capability.Provider.TryCreate(finding, out var draft, out var diagnostic)
                || !ValidDraft(draft)
                || !SafeCode.IsMatch(diagnostic ?? string.Empty))
            {
                items.Add(Item(finding, FixPlanItemDisposition.InvalidSnapshot, "fix-preview-provider-rejected-snapshot"));
                continue;
            }

            items.Add(Item(finding, FixPlanItemDisposition.Planned, "fix-operation-planned"));
            candidates.Add(new(finding, capability, draft));
        }

        var conflicts = new List<FixPlanConflict>();
        var operations = new List<OperationCandidate>();
        foreach (var group in candidates.GroupBy(TargetKey, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var values = group.OrderBy(value => value.Finding.FindingId).ToArray();
            var compatible = values.Select(OperationMeaning).Distinct(StringComparer.Ordinal).Count() == 1;
            if (!compatible)
            {
                var ids = values.Select(value => value.Finding.FindingId).Order().ToArray();
                conflicts.Add(new(group.Key, ids, "fix-operation-target-conflict"));
                var conflictIds = ids.ToHashSet();
                for (var index = 0; index < items.Count; index++)
                    if (conflictIds.Contains(items[index].FindingId))
                        items[index] = items[index] with { Disposition = FixPlanItemDisposition.Conflict, DiagnosticCode = "fix-operation-target-conflict" };
                continue;
            }

            var canMerge = values.Length > 1
                && values.All(value => value.Capability.AllowsIdenticalOperationMerge)
                && values.Select(value => $"{value.Capability.CapabilityId}\n{value.Capability.CapabilityVersion}").Distinct(StringComparer.Ordinal).Count() == 1;
            operations.Add(canMerge ? Merge(values) : values[0]);
            if (!canMerge) operations.AddRange(values.Skip(1));
        }

        var finalOperations = operations
            .OrderBy(TargetKey, StringComparer.Ordinal)
            .ThenBy(value => value.Capability.CapabilityId, StringComparer.Ordinal)
            .ThenBy(value => value.Finding.RuleCode, StringComparer.Ordinal)
            .ThenBy(value => value.Finding.FindingId)
            .Select((value, index) => ToOperation(value, index + 1))
            .ToArray();
        var finalItems = items
            .OrderBy(value => ordered.Single(finding => finding.FindingId == value.FindingId).RuleOrdinal)
            .ThenBy(value => value.RuleCode, StringComparer.Ordinal)
            .ThenBy(value => value.FindingId)
            .ToArray();

        var planned = finalItems.Count(value => value.Disposition == FixPlanItemDisposition.Planned);
        var unsupported = finalItems.Count(value => value.Disposition == FixPlanItemDisposition.Unsupported);
        var conflict = finalItems.Count(value => value.Disposition == FixPlanItemDisposition.Conflict);
        var invalid = finalItems.Count(value => value.Disposition == FixPlanItemDisposition.InvalidSnapshot);
        var state = conflicts.Count > 0 ? FixPlanState.Conflict
            : planned > 0 && unsupported + invalid == 0 ? FixPlanState.Ready
            : planned > 0 ? FixPlanState.PartiallyReady
            : invalid > 0 ? FixPlanState.InvalidSnapshot
            : FixPlanState.NotAvailable;
        var diagnostics = finalItems.Select(value => value.DiagnosticCode)
            .Concat(conflicts.Select(value => value.DiagnosticCode))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var hash = Hash(source, identities, finalItems, finalOperations, conflicts, state);

        return new(source.AuditId, source.DocumentVersionId, source.SourceVersionSha256,
            source.ResolvedRuleSetHash!, source.DocumentKindSnapshot.Value.ToString(), PlannerVersion,
            ordered.Length, planned, unsupported, conflict, invalid, finalItems, finalOperations,
            conflicts, hash, state, diagnostics);
    }

    private static FixPlanPreview Terminal(
        FixPlanSource source,
        IReadOnlyList<FixPlanFindingSnapshot> findings,
        FixPlanState state,
        string code)
    {
        var items = findings.Select(value => Item(value, FixPlanItemDisposition.InvalidSnapshot, code)).ToArray();
        var identities = findings.Select(value => TrySnapshotIdentity(value, out var identity) ? identity : SnapshotIdentity.Invalid(value)).ToArray();
        var hash = Hash(source, identities, items, [], [], state);
        return new(source.AuditId, source.DocumentVersionId, source.SourceVersionSha256,
            source.ResolvedRuleSetHash ?? string.Empty, source.DocumentKindSnapshot?.ToString() ?? string.Empty,
            PlannerVersion, findings.Count, 0, 0, 0, findings.Count, items, [], [], hash, state, [code]);
    }

    private static FixPlanItem Item(FixPlanFindingSnapshot value, FixPlanItemDisposition disposition, string diagnostic) =>
        new(value.FindingId, value.RuleCode, value.ValidationKey, value.RuleOrdinal, disposition, diagnostic);

    private static bool ValidSha(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidDraft(FixOperationDraft value) => value is not null
        && value.Target is not null
        && SafeCode.IsMatch(value.Target.Scope ?? string.Empty)
        && NonNegative(value.Target.BodyElementIndex)
        && NonNegative(value.Target.SectionIndex)
        && NonNegative(value.Target.ParagraphIndex)
        && NonNegative(value.Target.RunIndex)
        && SafeCode.IsMatch(value.PropertyIdentifier ?? string.Empty)
        && value.Expected is not null
        && ValidExpected(value.Expected)
        && SafeCode.IsMatch(value.PreconditionCode ?? string.Empty)
        && SafeCode.IsMatch(value.SummaryCode ?? string.Empty);

    private static bool NonNegative(int? value) => value is null or >= 0;

    private static bool ValidExpected(FixExpectedValueDescriptor expected)
    {
        if (expected.Value is not { Length: > 0 and <= 128 }) return false;
        return expected.Type switch
        {
            "boolean" => expected.Value is "true" or "false",
            "integer" or "twips" or "half-points" =>
                long.TryParse(expected.Value, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
            "decimal" => decimal.TryParse(expected.Value,
                System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "string-code" => expected.Value.All(character => !char.IsControl(character)),
            "enum-code" => SafeCode.IsMatch(expected.Value),
            _ => false
        };
    }

    private static bool TrySnapshotIdentity(FixPlanFindingSnapshot finding, out SnapshotIdentity identity)
    {
        identity = null!;
        if (finding.FindingId == Guid.Empty || finding.RuleOrdinal < 0
            || string.IsNullOrWhiteSpace(finding.RuleCode) || finding.RuleCode.Length > 128
            || string.IsNullOrWhiteSpace(finding.Domain) || finding.Domain.Length > 128
            || string.IsNullOrWhiteSpace(finding.Element) || finding.Element.Length > 128
            || string.IsNullOrWhiteSpace(finding.ValidationKey) || finding.ValidationKey.Length > 256
            || finding.SnapshotSchemaVersion < 1
            || !CanonicalJson.TryDigest(finding.ActualJson, MaximumJsonLength, out var actual)
            || !CanonicalJson.TryDigest(finding.ExpectedJson, MaximumJsonLength, out var expected)
            || !CanonicalJson.TryDigest(finding.LocationJson, MaximumJsonLength, out var location))
            return false;
        identity = new(finding.FindingId, finding.RuleOrdinal, finding.RuleCode, finding.Domain,
            finding.Element, finding.ValidationKey, finding.Severity.ToString(), finding.FixMode.ToString(),
            finding.FindingState.ToString(), finding.SnapshotSchemaVersion, actual, expected, location);
        return true;
    }

    private static string TargetKey(OperationCandidate value)
    {
        var target = value.Draft.Target;
        return string.Join("/", target.Scope, target.BodyElementIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            target.SectionIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            target.ParagraphIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            target.RunIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            value.Draft.PropertyIdentifier);
    }

    private static string OperationMeaning(OperationCandidate value) => string.Join("\n",
        value.Capability.OperationKind.ToString(), value.Draft.Expected.Type, value.Draft.Expected.Value,
        value.Draft.PreconditionCode);

    private static OperationCandidate Merge(IReadOnlyList<OperationCandidate> values) =>
        values[0] with { SourceFindingIds = values.SelectMany(value => value.SourceFindingIds).Distinct().Order().ToArray() };

    private static FixPlanOperation ToOperation(OperationCandidate value, int ordinal) => new(
        value.Capability.OperationKind, value.Capability.CapabilityId, value.Capability.CapabilityVersion,
        value.Finding.RuleCode, value.Finding.ValidationKey, value.SourceFindingIds.Order().ToArray(),
        value.Draft.Target, value.Draft.PropertyIdentifier, value.Draft.Expected,
        value.Capability.RequiresConfirmation, ordinal, value.Draft.PreconditionCode, value.Draft.SummaryCode);

    private static string Hash(
        FixPlanSource source,
        IReadOnlyList<SnapshotIdentity> identities,
        IReadOnlyList<FixPlanItem> items,
        IReadOnlyList<FixPlanOperation> operations,
        IReadOnlyList<FixPlanConflict> conflicts,
        FixPlanState state)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("planner_version", PlannerVersion);
            writer.WriteString("audit_id", source.AuditId);
            writer.WriteString("document_version_id", source.DocumentVersionId);
            writer.WriteString("source_sha256", source.SourceVersionSha256);
            writer.WriteString("resolved_rule_set_hash", source.ResolvedRuleSetHash ?? string.Empty);
            writer.WriteString("document_kind", source.DocumentKindSnapshot?.ToString() ?? string.Empty);
            writer.WriteString("state", state.ToString());
            writer.WritePropertyName("findings"); writer.WriteStartArray();
            foreach (var value in identities.OrderBy(value => value.Ordinal).ThenBy(value => value.RuleCode, StringComparer.Ordinal).ThenBy(value => value.Id))
            {
                writer.WriteStartObject();
                writer.WriteString("id", value.Id); writer.WriteNumber("ordinal", value.Ordinal);
                writer.WriteString("rule_code", value.RuleCode); writer.WriteString("domain", value.Domain);
                writer.WriteString("element", value.Element); writer.WriteString("validation_key", value.ValidationKey);
                writer.WriteString("severity", value.Severity); writer.WriteString("fix_mode", value.FixMode);
                writer.WriteString("finding_state", value.FindingState); writer.WriteNumber("snapshot_schema_version", value.SnapshotSchemaVersion);
                writer.WriteString("actual_hash", value.ActualHash); writer.WriteString("expected_hash", value.ExpectedHash);
                writer.WriteString("location_hash", value.LocationHash); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("items"); JsonSerializer.Serialize(writer, items);
            writer.WritePropertyName("operations"); JsonSerializer.Serialize(writer, operations);
            writer.WritePropertyName("conflicts"); JsonSerializer.Serialize(writer, conflicts);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private sealed record OperationCandidate(
        FixPlanFindingSnapshot Finding,
        RemediationCapability Capability,
        FixOperationDraft Draft)
    {
        public IReadOnlyList<Guid> SourceFindingIds { get; init; } = [Finding.FindingId];
    }

    private sealed record SnapshotIdentity(
        Guid Id, int Ordinal, string RuleCode, string Domain, string Element,
        string ValidationKey, string Severity, string FixMode, string FindingState,
        int SnapshotSchemaVersion, string ActualHash, string ExpectedHash, string LocationHash)
    {
        public static SnapshotIdentity Invalid(FixPlanFindingSnapshot value) => new(
            value.FindingId, value.RuleOrdinal, value.RuleCode ?? string.Empty,
            value.Domain ?? string.Empty, value.Element ?? string.Empty, value.ValidationKey ?? string.Empty,
            value.Severity.ToString(), value.FixMode.ToString(), value.FindingState.ToString(),
            value.SnapshotSchemaVersion, "invalid", "invalid", "invalid");
    }
}

internal static class CanonicalJson
{
    public static bool TryDigest(string json, int maximumLength, out string digest)
    {
        digest = string.Empty;
        if (string.IsNullOrWhiteSpace(json) || json.Length > maximumLength) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Write(writer, document.RootElement, 0);
            digest = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 8) throw new JsonException();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!names.Add(property.Name)) throw new JsonException();
                    writer.WritePropertyName(property.Name); Write(writer, property.Value, depth + 1);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item, depth + 1); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new JsonException();
        }
    }
}
