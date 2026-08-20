using System.Text;
using System.Text.Json;
using Ppki.DocxEngine;

namespace Ppki.Application;

public sealed record StructuralFindingExcerptDto(
    Guid FindingId,
    Guid DocumentVersionId,
    string Status,
    string TargetType,
    string? Excerpt,
    string? TargetText,
    FindingPageLocationDto PageLocation)
{
    public override string ToString() =>
        $"StructuralFindingExcerpt(FindingId={FindingId:D},Status={Status},Content=[REDACTED])";
}

public interface IStructuralFindingExcerptService
{
    Task<StructuralFindingExcerptDto?> MaterializeAsync(Guid auditId, Guid findingId,
        Guid actorUserId, CancellationToken cancellationToken);
}

public static class StructuralFindingExcerptMaterializer
{
    public const int MaximumExcerptScalars = 240;

    public static async Task<(string Status, string TargetType, string? Excerpt, string? TargetText)>
        MaterializeAsync(string sourceDocxPath, string locationJson, CancellationToken cancellationToken)
    {
        if (!TryLocation(locationJson, out var expected)) return Unavailable();

        ParsedDocument document;
        try
        {
            document = await new OpenXmlDocxParser().ParseAsync(sourceDocxPath, cancellationToken);
        }
        catch (Exception exception) when (exception is DocxParserException or IOException
            or UnauthorizedAccessException or InvalidDataException)
        {
            return Unavailable();
        }

        var paragraph = expected.TargetType == "Section"
            ? SectionBoundaryParagraph(document, expected)
            : document.Paragraphs.SingleOrDefault(value => value.Index == expected.ParagraphIndex);
        if (paragraph?.Location is null
            || (expected.TargetType == "Section" ? !MatchesSectionBoundary(expected, paragraph.Location) : !Matches(expected, paragraph.Location)))
            return Unavailable();
        var normalized = paragraph.Text.Trim();
        if (normalized.Length == 0) return Unavailable();

        var excerpt = Bound(normalized);
        var targetType = expected.TargetType ?? (paragraph.IsHeading ? "Heading" : "Paragraph");
        var targetText = normalized.EnumerateRunes().Count() <= MaximumExcerptScalars ? normalized : null;
        return ("Exact", targetType, excerpt, targetText);
    }

    private static bool TryLocation(string json, out ExactParagraphLocation location)
    {
        location = default!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var compact = String(root, "CompactLocation", "compactLocation");
            if (compact is not null && TryCompactLocation(compact, root, out location)) return true;
            var partKind = EnumValue(root, "PartKind", "partKind");
            var partUri = String(root, "PartUri", "partUri");
            var bodyElementIndex = Integer(root, "BodyElementIndex", "bodyElementIndex");
            var paragraphIndex = Integer(root, "ParagraphIndex", "paragraphIndex");
            if (!IsMainDocument(partKind) || partUri != "/word/document.xml"
                || bodyElementIndex is null || paragraphIndex is null)
                return false;
            location = new(bodyElementIndex.Value, paragraphIndex.Value,
                Integer(root, "SectionIndex", "sectionIndex"),
                Integer(root, "TableIndex", "tableIndex"),
                Integer(root, "RowIndex", "rowIndex"),
                Integer(root, "CellIndex", "cellIndex"),
                IsSection(EnumValue(root, "ElementKind", "elementKind")) ? "Section" : null);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryCompactLocation(string compact, JsonElement root,
        out ExactParagraphLocation location)
    {
        location = default!;
        var segments = compact.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || segments[0] != "maindocument") return false;
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        string? kind = null;
        foreach (var segment in segments.Skip(1))
        {
            var pair = segment.Split(':', 2, StringSplitOptions.None);
            if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0) return false;
            var key = pair[0];
            var value = pair[1];
            if (key == "kind") { kind = value; continue; }
            if (!int.TryParse(value, out var parsed) || parsed < 0 || !values.TryAdd(key, parsed)) return false;
        }
        if (kind is not ("paragraph" or "run" or "section") || !values.TryGetValue("s", out var sectionIndex)
            || !values.TryGetValue("b", out var body) || !values.TryGetValue("p", out var paragraph)) return false;
        var explicitBody = Integer(root, "BodyElementIndex", "bodyElementIndex");
        var explicitParagraph = Integer(root, "ParagraphIndex", "paragraphIndex");
        var explicitSection = Integer(root, "SectionIndex", "sectionIndex");
        var explicitRun = Integer(root, "RunIndex", "runIndex");
        if (explicitBody != body || explicitParagraph != paragraph
            || (explicitSection is not null && (!values.TryGetValue("s", out var section) || section != explicitSection))
            || (explicitRun is not null && (!values.TryGetValue("r", out var run) || run != explicitRun)))
            return false;
        location = new(body, paragraph, sectionIndex,
            Nullable(values, "t"), Nullable(values, "row"), Nullable(values, "cell"),
            kind == "section" ? "Section" : null);
        return true;
    }

    private static int? Nullable(IReadOnlyDictionary<string, int> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static bool Matches(ExactParagraphLocation expected, DocumentElementLocation actual) =>
        actual.PartKind == DocumentPartKind.MainDocument
        && actual.PartUri == "/word/document.xml"
        && actual.BodyElementIndex == expected.BodyElementIndex
        && actual.ParagraphIndex == expected.ParagraphIndex
        && expected.SectionIndex == actual.SectionIndex
        && expected.TableIndex == actual.TableIndex
        && expected.RowIndex == actual.RowIndex
        && expected.CellIndex == actual.CellIndex;

    private static ParsedParagraph? SectionBoundaryParagraph(ParsedDocument document,
        ExactParagraphLocation expected)
    {
        var bodyElement = document.BodyElementOrder.SingleOrDefault(value =>
            value.Index == expected.BodyElementIndex && value.Kind == ParsedBodyElementKind.Paragraph);
        return bodyElement?.ParagraphIndex is null ? null
            : document.Paragraphs.SingleOrDefault(value => value.Index == bodyElement.ParagraphIndex);
    }

    private static bool MatchesSectionBoundary(ExactParagraphLocation expected, DocumentElementLocation actual) =>
        actual.PartKind == DocumentPartKind.MainDocument
        && actual.PartUri == "/word/document.xml"
        && actual.BodyElementIndex == expected.BodyElementIndex
        && actual.SectionIndex == expected.SectionIndex
        && actual.TableIndex is null && actual.RowIndex is null && actual.CellIndex is null;

    private static string Bound(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length <= MaximumExcerptScalars) return value;
        return string.Concat(runes.Take(MaximumExcerptScalars - 1).Select(value => value.ToString())) + "…";
    }

    private static string? EnumValue(JsonElement root, string first, string second)
    {
        if (!Property(root, first, second, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed.ToString(),
            _ => null
        };
    }

    private static bool IsMainDocument(string? value) => value is "MainDocument" or "mainDocument" or "0";
    private static bool IsSection(string? value) => value is "Section" or "section" or "0";

    private static string? String(JsonElement root, string first, string second) =>
        Property(root, first, second, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? Integer(JsonElement root, string first, string second) =>
        Property(root, first, second, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed) && parsed >= 0 ? parsed : null;

    private static bool Property(JsonElement root, string first, string second, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object
            && (root.TryGetProperty(first, out value) || root.TryGetProperty(second, out value));
    }

    private static (string Status, string TargetType, string? Excerpt, string? TargetText) Unavailable() =>
        ("Unavailable", "Other", null, null);

    private sealed record ExactParagraphLocation(int BodyElementIndex, int ParagraphIndex,
        int? SectionIndex, int? TableIndex, int? RowIndex, int? CellIndex, string? TargetType = null);
}
