using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ppki.Domain;

namespace Ppki.Application;

[JsonConverter(typeof(JsonStringEnumConverter<AuditComparisonStatus>))]
public enum AuditComparisonStatus
{
    StillDetected,
    Changed,
    NoLongerDetected,
    NewlyDetected
}

public sealed class AuditComparisonException(string diagnosticCode) : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

public sealed record AuditComparisonQuery(
    AuditComparisonStatus? Status,
    RuleSeverity? Severity,
    string? Domain,
    string? RuleCode,
    int Page,
    int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;
    public const int MaximumPage = 10_000;
    public const int MaximumComparisonItems = 20_000;

    public static bool TryCreate(
        string? status,
        string? severity,
        string? domain,
        string? ruleCode,
        string? sort,
        int? page,
        int? pageSize,
        out AuditComparisonQuery query,
        out string? errorCode)
    {
        query = null!;
        errorCode = null;
        if (!TryEnum(status, out AuditComparisonStatus? parsedStatus)
            || !TryEnum(severity, out RuleSeverity? parsedSeverity))
        {
            errorCode = "audit-comparison-filter-enum-invalid";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(sort)
            && !sort.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "audit-comparison-sort-invalid";
            return false;
        }
        if (!TryFilter(domain, 128, out var normalizedDomain)
            || !TryFilter(ruleCode, 128, out var normalizedRuleCode))
        {
            errorCode = "audit-comparison-filter-text-invalid";
            return false;
        }

        var selectedPage = page ?? 1;
        var selectedPageSize = pageSize ?? DefaultPageSize;
        if (selectedPage is < 1 or > MaximumPage
            || selectedPageSize is < 1 or > MaximumPageSize
            || (long)(selectedPage - 1) * selectedPageSize >= MaximumComparisonItems)
        {
            errorCode = "audit-comparison-pagination-invalid";
            return false;
        }

        query = new(parsedStatus, parsedSeverity, normalizedDomain,
            normalizedRuleCode, selectedPage, selectedPageSize);
        return true;
    }

    private static bool TryEnum<T>(string? value, out T? parsed) where T : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var name = Enum.GetNames<T>().SingleOrDefault(candidate =>
            candidate.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
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

public sealed record AuditComparisonLocationSummary(
    string? CompactLocation,
    int? SectionIndex,
    int? BodyElementIndex,
    int? ParagraphIndex,
    int? RunIndex);

public sealed record AuditComparisonActualSummary(
    string? Property,
    string? NormalizedValue,
    string? Unit,
    string? ResolutionState,
    string? SourceKind,
    bool? Inherited,
    string? DiagnosticCode);

public sealed record AuditComparisonExpectedSummary(
    string? Property,
    IReadOnlyList<string> AcceptedValues,
    string? Unit,
    string? Tolerance,
    string? ContractSource,
    string? ValidationKey);

public sealed record AuditComparisonFindingDto(
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
    AuditComparisonLocationSummary Location,
    AuditComparisonActualSummary Actual,
    AuditComparisonExpectedSummary Expected,
    decimal? Confidence,
    AuditFindingSourceDto Source);

public sealed record AuditComparisonItemDto(
    AuditComparisonStatus Status,
    AuditComparisonFindingDto? Before,
    AuditComparisonFindingDto? After,
    string RuleCode,
    string ValidationKey,
    string Domain,
    string Element,
    string Severity,
    int RuleOrdinal,
    AuditComparisonLocationSummary Location);

public sealed record AuditComparisonStatusCounts(
    int StillDetected,
    int Changed,
    int NoLongerDetected,
    int NewlyDetected);

public sealed record AuditComparisonSeverityCounts(
    string Severity,
    AuditComparisonStatusCounts Counts,
    int TotalCount);

public sealed record AuditComparisonDomainCounts(
    string Domain,
    AuditComparisonStatusCounts Counts,
    int TotalCount);

public sealed record AuditComparisonScoreDto(
    string State,
    decimal? Value,
    string? PolicyVersion,
    string? DiagnosticCode);

public sealed record AuditComparisonSummaryDto(
    int SourceFindingCount,
    int ResultFindingCount,
    int StillDetectedCount,
    int ChangedCount,
    int NoLongerDetectedCount,
    int NewlyDetectedCount,
    IReadOnlyList<AuditComparisonSeverityCounts> Severities,
    IReadOnlyList<AuditComparisonDomainCounts> Domains,
    AuditComparisonScoreDto SourceScore,
    AuditComparisonScoreDto ResultScore,
    decimal? ScoreDelta);

public sealed record AuditComparisonDto(
    Guid SourceAuditId,
    Guid ResultAuditId,
    Guid FixExecutionId,
    Guid SourceDocumentVersionId,
    Guid ResultDocumentVersionId,
    string ComparisonState,
    AuditComparisonSummaryDto Summary,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<AuditComparisonItemDto> Items);

public interface IAuditComparisonService
{
    Task<AuditComparisonDto?> GetAsync(
        Guid fixExecutionId,
        Guid ownerUserId,
        AuditComparisonQuery query,
        CancellationToken cancellationToken);
}

public sealed record AuditComparisonFindingSnapshot(
    Guid Id,
    Guid AuditId,
    int RuleOrdinal,
    string RuleCode,
    string Domain,
    string ValidationKey,
    string Element,
    RuleSeverity Severity,
    FixMode FixMode,
    FindingStatus FindingState,
    string ReasonCode,
    string ActualJson,
    string ExpectedJson,
    string LocationJson,
    decimal? Confidence,
    string? SourceSection,
    int? PdfPage,
    string? PrintedPage);

public static class AuditComparisonEngine
{
    public static IReadOnlyList<AuditComparisonItemDto> Compare(
        IEnumerable<AuditComparisonFindingSnapshot> source,
        IEnumerable<AuditComparisonFindingSnapshot> result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        var before = source.Select(value => PreparedFinding.Create(value, true)).ToArray();
        var after = result.Select(value => PreparedFinding.Create(value, false)).ToArray();
        var items = new List<AuditComparisonItemDto>();

        foreach (var value in before.Where(value => !value.Pairable))
            items.Add(Item(AuditComparisonStatus.NoLongerDetected, value, null));
        foreach (var value in after.Where(value => !value.Pairable))
            items.Add(Item(AuditComparisonStatus.NewlyDetected, null, value));

        var beforeGroups = before.Where(value => value.Pairable)
            .GroupBy(value => value.TargetKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var afterGroups = after.Where(value => value.Pairable)
            .GroupBy(value => value.TargetKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var targetKeys = beforeGroups.Keys.Concat(afterGroups.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal);
        foreach (var targetKey in targetKeys)
        {
            var sourceGroup = beforeGroups.GetValueOrDefault(targetKey) ?? [];
            var resultGroup = afterGroups.GetValueOrDefault(targetKey) ?? [];
            var sourceActualGroups = sourceGroup.GroupBy(value => value.ActualFingerprint, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Stable(group).ToArray(), StringComparer.Ordinal);
            var resultActualGroups = resultGroup.GroupBy(value => value.ActualFingerprint, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Stable(group).ToArray(), StringComparer.Ordinal);
            var actualKeys = sourceActualGroups.Keys.Concat(resultActualGroups.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal);
            foreach (var actualKey in actualKeys)
            {
                var exactBefore = sourceActualGroups.GetValueOrDefault(actualKey) ?? [];
                var exactAfter = resultActualGroups.GetValueOrDefault(actualKey) ?? [];
                var exactCount = Math.Min(exactBefore.Length, exactAfter.Length);
                for (var index = 0; index < exactCount; index++)
                    items.Add(Item(AuditComparisonStatus.StillDetected, exactBefore[index], exactAfter[index]));
                Remove(sourceGroup, exactBefore.Take(exactCount));
                Remove(resultGroup, exactAfter.Take(exactCount));
            }

            var changedBefore = Stable(sourceGroup).ToArray();
            var changedAfter = Stable(resultGroup).ToArray();
            var changedCount = Math.Min(changedBefore.Length, changedAfter.Length);
            for (var index = 0; index < changedCount; index++)
                items.Add(Item(AuditComparisonStatus.Changed, changedBefore[index], changedAfter[index]));
            for (var index = changedCount; index < changedBefore.Length; index++)
                items.Add(Item(AuditComparisonStatus.NoLongerDetected, changedBefore[index], null));
            for (var index = changedCount; index < changedAfter.Length; index++)
                items.Add(Item(AuditComparisonStatus.NewlyDetected, null, changedAfter[index]));
        }

        return items.OrderBy(value => StatusRank(value.Status))
            .ThenBy(value => value.RuleOrdinal)
            .ThenBy(value => value.Domain, StringComparer.Ordinal)
            .ThenBy(value => value.ValidationKey, StringComparer.Ordinal)
            .ThenBy(value => LocationKey(value.Location), StringComparer.Ordinal)
            .ThenBy(value => value.RuleCode, StringComparer.Ordinal)
            .ThenBy(value => value.Before?.Id ?? value.After?.Id)
            .ThenBy(value => value.After?.Id)
            .ToArray();
    }

    public static AuditComparisonSummaryDto Summary(
        IReadOnlyList<AuditComparisonItemDto> items,
        int sourceFindingCount,
        int resultFindingCount,
        AuditComparisonScoreDto sourceScore,
        AuditComparisonScoreDto resultScore)
    {
        var status = Counts(items);
        var severities = items.GroupBy(value => value.Severity, StringComparer.Ordinal)
            .OrderBy(group => SeverityRank(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AuditComparisonSeverityCounts(
                group.Key, Counts(group), group.Count()))
            .ToArray();
        var domains = items.GroupBy(value => value.Domain, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AuditComparisonDomainCounts(
                group.Key, Counts(group), group.Count()))
            .ToArray();
        decimal? delta = sourceScore.Value is not null && resultScore.Value is not null
            ? resultScore.Value.Value - sourceScore.Value.Value
            : null;
        return new(sourceFindingCount, resultFindingCount,
            status.StillDetected, status.Changed, status.NoLongerDetected, status.NewlyDetected,
            severities, domains, sourceScore, resultScore, delta);
    }

    public static IEnumerable<AuditComparisonItemDto> ApplyFilters(
        IEnumerable<AuditComparisonItemDto> values,
        AuditComparisonQuery query)
    {
        if (query.Status is not null) values = values.Where(value => value.Status == query.Status);
        if (query.Severity is not null) values = values.Where(value => value.Severity == query.Severity.ToString());
        if (query.Domain is not null) values = values.Where(value => value.Domain == query.Domain);
        if (query.RuleCode is not null) values = values.Where(value => value.RuleCode == query.RuleCode);
        return values;
    }

    private static AuditComparisonStatusCounts Counts(IEnumerable<AuditComparisonItemDto> values)
    {
        var counts = values.GroupBy(value => value.Status).ToDictionary(group => group.Key, group => group.Count());
        return new(
            counts.GetValueOrDefault(AuditComparisonStatus.StillDetected),
            counts.GetValueOrDefault(AuditComparisonStatus.Changed),
            counts.GetValueOrDefault(AuditComparisonStatus.NoLongerDetected),
            counts.GetValueOrDefault(AuditComparisonStatus.NewlyDetected));
    }

    private static IEnumerable<PreparedFinding> Stable(IEnumerable<PreparedFinding> values) => values
        .OrderBy(value => value.ActualFingerprint, StringComparer.Ordinal)
        .ThenBy(value => value.SemanticSortKey, StringComparer.Ordinal)
        .ThenBy(value => value.Source.Id);

    private static void Remove(List<PreparedFinding> values, IEnumerable<PreparedFinding> removed)
    {
        foreach (var value in removed) values.Remove(value);
    }

    private static AuditComparisonItemDto Item(
        AuditComparisonStatus status,
        PreparedFinding? before,
        PreparedFinding? after)
    {
        var selected = after ?? before ?? throw new InvalidOperationException("Comparison item is empty.");
        return new(status, before?.Dto, after?.Dto, selected.Source.RuleCode,
            selected.Source.ValidationKey, selected.Source.Domain, selected.Source.Element,
            selected.Source.Severity.ToString(), selected.Source.RuleOrdinal, selected.Location);
    }

    private static int StatusRank(AuditComparisonStatus status) => status switch
    {
        AuditComparisonStatus.StillDetected => 0,
        AuditComparisonStatus.Changed => 1,
        AuditComparisonStatus.NoLongerDetected => 2,
        AuditComparisonStatus.NewlyDetected => 3,
        _ => 4
    };

    private static int SeverityRank(string severity) => severity switch
    {
        nameof(RuleSeverity.Error) => 0,
        nameof(RuleSeverity.Warning) => 1,
        nameof(RuleSeverity.Info) => 2,
        _ => 3
    };

    private static string LocationKey(AuditComparisonLocationSummary value) => string.Join('|',
        value.BodyElementIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
        value.SectionIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
        value.ParagraphIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
        value.RunIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
        value.CompactLocation ?? string.Empty);

    private sealed record PreparedFinding(
        AuditComparisonFindingSnapshot Source,
        bool Pairable,
        string TargetKey,
        string ActualFingerprint,
        string SemanticSortKey,
        AuditComparisonLocationSummary Location,
        AuditComparisonFindingDto Dto)
    {
        public static PreparedFinding Create(AuditComparisonFindingSnapshot source, bool isBefore)
        {
            Validate(source);
            var actual = SafeJson.Actual(source.ActualJson);
            var expected = SafeJson.Expected(source.ExpectedJson);
            var location = SafeJson.Location(source.LocationJson);
            var actualFingerprint = CanonicalJsonFingerprint.Create(source.ActualJson);
            var expectedFingerprint = CanonicalJsonFingerprint.Create(source.ExpectedJson);
            var locationFingerprint = CanonicalJsonFingerprint.Create(source.LocationJson);
            var property = actual.Property ?? expected.Property;
            var pairable = !string.IsNullOrWhiteSpace(property)
                && (!string.IsNullOrWhiteSpace(location.CompactLocation)
                    || location.SectionIndex is not null || location.BodyElementIndex is not null
                    || location.ParagraphIndex is not null || location.RunIndex is not null);
            var targetKey = pairable ? HashParts(source.RuleOrdinal.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), source.RuleCode, source.Domain,
                source.ValidationKey, source.Element, locationFingerprint, property!) : string.Empty;
            var semanticSortKey = HashParts(expectedFingerprint, locationFingerprint,
                source.ReasonCode, source.FixMode.ToString(), property ?? string.Empty);
            var dto = new AuditComparisonFindingDto(source.Id, source.AuditId, source.RuleOrdinal,
                source.RuleCode, source.Domain, source.ValidationKey, source.Element,
                source.Severity.ToString(), source.FixMode.ToString(), source.FindingState.ToString(),
                SafeJson.Text(source.ReasonCode, 256), location, actual, expected, source.Confidence,
                new(SafeJson.NullableText(source.SourceSection, 256), source.PdfPage,
                    SafeJson.NullableText(source.PrintedPage, 64)));
            return new(source, pairable, targetKey, actualFingerprint, semanticSortKey, location, dto);
        }

        private static void Validate(AuditComparisonFindingSnapshot value)
        {
            if (value.Id == Guid.Empty || value.AuditId == Guid.Empty || value.RuleOrdinal <= 0
                || string.IsNullOrWhiteSpace(value.RuleCode) || string.IsNullOrWhiteSpace(value.Domain)
                || string.IsNullOrWhiteSpace(value.ValidationKey) || string.IsNullOrWhiteSpace(value.Element)
                || string.IsNullOrWhiteSpace(value.ReasonCode))
                throw new AuditComparisonException("audit-comparison-finding-snapshot-invalid");
        }
    }

    private static string HashParts(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values) builder.Append(value.Length).Append(':').Append(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

public static class CanonicalJsonFingerprint
{
    private const int MaximumJsonLength = 65_536;
    private const int MaximumDepth = 16;
    private const int MaximumNodes = 2_048;
    private const int MaximumCollectionItems = 256;
    private const int MaximumStringLength = 4_096;

    public static string Create(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonLength)
            throw new AuditComparisonException("audit-comparison-json-invalid");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            var buffer = new ArrayBufferWriter<byte>();
            var nodes = 0;
            using (var writer = new Utf8JsonWriter(buffer)) Write(writer, document.RootElement, 0, ref nodes);
            return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
        }
        catch (JsonException)
        {
            throw new AuditComparisonException("audit-comparison-json-invalid");
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, int depth, ref int nodes)
    {
        if (depth > MaximumDepth || ++nodes > MaximumNodes)
            throw new AuditComparisonException("audit-comparison-json-limit-exceeded");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
                if (properties.Length > MaximumCollectionItems)
                    throw new AuditComparisonException("audit-comparison-json-limit-exceeded");
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    if (property.Name.Length > MaximumStringLength)
                        throw new AuditComparisonException("audit-comparison-json-limit-exceeded");
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, depth + 1, ref nodes);
                }
                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
            {
                var items = value.EnumerateArray().ToArray();
                if (items.Length > MaximumCollectionItems)
                    throw new AuditComparisonException("audit-comparison-json-limit-exceeded");
                writer.WriteStartArray();
                foreach (var item in items) Write(writer, item, depth + 1, ref nodes);
                writer.WriteEndArray();
                break;
            }
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (text.Length > MaximumStringLength)
                    throw new AuditComparisonException("audit-comparison-json-limit-exceeded");
                writer.WriteStringValue(text);
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new AuditComparisonException("audit-comparison-json-invalid");
        }
    }
}

internal static class SafeJson
{
    public static AuditComparisonActualSummary Actual(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        return new(NullableProperty(root, "Property"), NullableProperty(root, "NormalizedValue"),
            NullableProperty(root, "Unit"), NullableProperty(root, "ResolutionState"),
            NullableProperty(root, "SourceKind"), NullableBoolean(root, "Inherited"),
            NullableProperty(root, "DiagnosticCode"));
    }

    public static AuditComparisonExpectedSummary Expected(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var accepted = Property(root, "AcceptedValues", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Take(16).Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => Text(value.GetString() ?? string.Empty, 128)).ToArray()
            : [];
        return new(NullableProperty(root, "Property"), accepted, NullableProperty(root, "Unit"),
            NullableProperty(root, "Tolerance"), NullableProperty(root, "ContractSource"),
            NullableProperty(root, "ValidationKey"));
    }

    public static AuditComparisonLocationSummary Location(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        return new(NullableProperty(root, "CompactLocation", 256), NullableInteger(root, "SectionIndex"),
            NullableInteger(root, "BodyElementIndex"), NullableInteger(root, "ParagraphIndex"),
            NullableInteger(root, "RunIndex"));
    }

    public static string Text(string value, int maximumLength)
    {
        var safe = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return safe.Length <= maximumLength ? safe : safe[..maximumLength];
    }

    public static string? NullableText(string? value, int maximumLength) =>
        value is null ? null : Text(value, maximumLength);

    private static JsonDocument Parse(string json)
    {
        _ = CanonicalJsonFingerprint.Create(json);
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new AuditComparisonException("audit-comparison-json-object-required");
        }
        return document;
    }

    private static string? NullableProperty(JsonElement root, string name, int maximumLength = 256) =>
        Property(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? Text(value.GetString() ?? string.Empty, maximumLength)
            : null;

    private static bool? NullableBoolean(JsonElement root, string name) =>
        Property(root, name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        } : null;

    private static int? NullableInteger(JsonElement root, string name) =>
        Property(root, name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result) && result >= 0 ? result : null;

    private static bool Property(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
            if (Normalize(property.Name) == Normalize(name))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
