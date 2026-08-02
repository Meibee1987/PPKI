using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Ppki.Domain;

namespace Ppki.RuleEngine;

public sealed class ResolvedRuleSetSnapshotBuilder : IResolvedRuleSetSnapshotBuilder
{
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<AuditRuleSnapshot> Build(
        Guid auditJobId,
        IEnumerable<RuleDefinition> resolvedRules,
        string layer,
        int precedence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);

        return resolvedRules
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
