using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.RuleEngine;

public sealed class ResolvedRuleSetSnapshotBuilder : IResolvedRuleSetSnapshotBuilder
{
    public const int CurrentSchemaVersion = 2;

    public IReadOnlyList<AuditRuleSnapshot> Build(
        Guid auditJobId,
        IEnumerable<RuleDefinition> resolvedRules,
        string layer,
        int precedence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);

        var rules = resolvedRules.ToArray();
        if (rules.Any(rule => rule.ReviewBlockingPolicy == ReviewBlockingPolicy.PendingApproval))
            throw new ReviewReadinessPolicyResolutionException("review-readiness-policy-pending-approval");
        var invalid = rules.FirstOrDefault(rule => rule.ReviewBlockingPolicy is null
            or ReviewBlockingPolicy.Unknown
            || !string.Equals(rule.ReadinessPolicyVersion, ReviewReadinessPolicy.Version, StringComparison.Ordinal));
        if (invalid is not null)
            throw new ReviewReadinessPolicyResolutionException("review-readiness-policy-not-applicable");

        return rules
            .OrderBy(rule => rule.RuleCode, StringComparer.Ordinal)
            .ThenBy(rule => rule.AppliesTo, StringComparer.Ordinal)
            .ThenBy(rule => rule.ValidationKey, StringComparer.Ordinal)
            .Select((rule, index) => new AuditRuleSnapshot
            {
                AuditJobId = auditJobId,
                RuleId = rule.Id,
                RuleCode = rule.RuleCode,
                Domain = rule.Domain,
                Subdomain = rule.Subdomain,
                AppliesTo = rule.AppliesTo,
                Element = rule.Element,
                RequirementJson = JsonSerializer.Serialize(new
                {
                    officialRequirement = rule.OfficialRequirement,
                    expectedValuePattern = rule.ExpectedValuePattern
                }),
                ValidationKey = rule.ValidationKey,
                ValidationJson = "{}",
                Severity = rule.Severity,
                FixMode = rule.FixMode,
                ReviewBlockingPolicy = rule.ReviewBlockingPolicy,
                ReadinessPolicyVersion = rule.ReadinessPolicyVersion,
                SourceReferenceJson = JsonSerializer.Serialize(new
                {
                    sourceSection = rule.SourceSection,
                    pdfPage = rule.PdfPage,
                    printedPage = rule.PrintedPage
                }),
                Layer = layer,
                Precedence = precedence,
                Ordinal = index + 1,
                SnapshotSchemaVersion = CurrentSchemaVersion
            })
            .ToArray();
    }
}

public sealed class ResolvedRuleSetHasher : IResolvedRuleSetHasher
{
    public string Hash(IEnumerable<AuditRuleSnapshot> snapshots)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartArray();
            foreach (var snapshot in snapshots
                .OrderBy(item => item.Ordinal)
                .ThenBy(item => item.RuleCode, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("rule_code", snapshot.RuleCode);
                writer.WriteString("domain", snapshot.Domain);
                if (snapshot.Subdomain is null) writer.WriteNull("subdomain");
                else writer.WriteString("subdomain", snapshot.Subdomain);
                writer.WriteString("applies_to", snapshot.AppliesTo);
                writer.WriteString("element", snapshot.Element);
                writer.WritePropertyName("requirement");
                WriteCanonicalJson(writer, snapshot.RequirementJson);
                writer.WriteString("validation_key", snapshot.ValidationKey);
                writer.WritePropertyName("validation");
                WriteCanonicalJson(writer, snapshot.ValidationJson);
                writer.WriteString("severity", snapshot.Severity.ToString());
                writer.WriteString("fix_mode", snapshot.FixMode.ToString());
                if (snapshot.SnapshotSchemaVersion >= 2)
                {
                    if (!ReviewReadinessPolicy.IsPolicyAwareSnapshot(snapshot))
                        throw new InvalidOperationException("Policy-aware snapshot is incomplete.");
                    writer.WriteString("review_blocking_policy", snapshot.ReviewBlockingPolicy!.Value.ToString());
                    writer.WriteString("readiness_policy_version", snapshot.ReadinessPolicyVersion);
                }
                writer.WritePropertyName("source_reference");
                WriteCanonicalJson(writer, snapshot.SourceReferenceJson);
                writer.WriteString("layer", snapshot.Layer);
                writer.WriteNumber("precedence", snapshot.Precedence);
                writer.WriteNumber("ordinal", snapshot.Ordinal);
                writer.WriteNumber("snapshot_schema_version", snapshot.SnapshotSchemaVersion);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, string json)
    {
        using var document = JsonDocument.Parse(json);
        WriteCanonicalElement(writer, document.RootElement);
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonicalElement(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException("Unsupported snapshot JSON value.");
        }
    }
}

public sealed class ReviewReadinessPolicyResolutionException(string diagnosticCode)
    : InvalidOperationException(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}
