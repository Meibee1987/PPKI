using System.Globalization;
using System.Text.Json;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.RuleEngine;

public static class LayoutUnitConverter
{
    public static long ToTwips(decimal value, string unit)
    {
        try
        {
            return Normalize(unit) switch
            {
                "twip" or "twips" => Round(value),
                "cm" => Round(value * 144_000m / 254m),
                "mm" => Round(value * 14_400m / 254m),
                "in" or "inch" => Round(value * 1_440m),
                "pt" or "point" => Round(value * 20m),
                _ => throw new LayoutRuleConfigurationException("validation-unit-unsupported")
            };
        }
        catch (OverflowException)
        {
            throw new LayoutRuleConfigurationException("validation-parameter-out-of-range");
        }
    }

    public static int ToHalfPoints(decimal value, string unit)
    {
        try
        {
            return Normalize(unit) switch
            {
                "half-point" or "half-points" => checked((int)Round(value)),
                "pt" or "point" => checked((int)Round(value * 2m)),
                _ => throw new LayoutRuleConfigurationException("validation-unit-unsupported")
            };
        }
        catch (OverflowException)
        {
            throw new LayoutRuleConfigurationException("validation-parameter-out-of-range");
        }
    }

    private static long Round(decimal value) => decimal.ToInt64(decimal.Round(value, 0, MidpointRounding.AwayFromZero));
    private static string Normalize(string unit) => unit.Trim().ToLowerInvariant();
}

internal sealed class LayoutRuleConfigurationException(string code) : Exception
{
    public string Code { get; } = code;
}

internal static class LayoutValidationSupport
{
    public const string AllSections = "all-sections";
    public const string NormalBodyParagraphs = "normal-body-paragraphs";
    public const string VisibleRunSelector = "visible-runs-in-normal-body-paragraphs";

    public static bool TryPrepare(
        AuditRuleSnapshot snapshot,
        string expectedSelector,
        out JsonDocument configuration,
        out RuleValidationResult failure)
    {
        configuration = null!;
        failure = null!;
        if (snapshot.AppliesTo is not ("Semua" or "All"))
        {
            failure = RuleValidationResult.Unsupported("document-context-unsupported");
            return false;
        }
        try
        {
            configuration = JsonDocument.Parse(snapshot.ValidationJson);
            if (configuration.RootElement.ValueKind != JsonValueKind.Object)
                throw new LayoutRuleConfigurationException("validation-json-invalid");
            var selector = String(configuration.RootElement, "selector", expectedSelector);
            if (!string.Equals(selector, expectedSelector, StringComparison.Ordinal))
            {
                configuration.Dispose();
                failure = RuleValidationResult.Unsupported("validation-selector-unsupported");
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            failure = RuleValidationResult.Invalid("validation-json-invalid");
            return false;
        }
        catch (LayoutRuleConfigurationException exception)
        {
            configuration?.Dispose();
            failure = RuleValidationResult.Invalid(exception.Code);
            return false;
        }
    }

    public static decimal Decimal(JsonElement root, string name, decimal fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result))
            throw new LayoutRuleConfigurationException("validation-parameter-invalid");
        return result;
    }

    public static string String(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new LayoutRuleConfigurationException("validation-parameter-invalid");
        return value.GetString()!;
    }

    public static bool Boolean(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new LayoutRuleConfigurationException("validation-parameter-invalid");
        return value.GetBoolean();
    }

    public static IReadOnlyList<string> Strings(JsonElement root, string name, IReadOnlyList<string> fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Array)
            throw new LayoutRuleConfigurationException("validation-parameter-invalid");
        var result = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString() : null).ToArray();
        if (result.Length == 0 || result.Any(string.IsNullOrWhiteSpace))
            throw new LayoutRuleConfigurationException("validation-parameter-invalid");
        return result.Select(item => item!).ToArray();
    }

    public static IEnumerable<ParsedParagraph> NormalParagraphs(ParsedDocument document)
    {
        var headingIndexes = document.Headings.Select(value => value.ParagraphIndex).ToHashSet();
        return document.Paragraphs
            .Where(value => value.Location?.PartKind == DocumentPartKind.MainDocument)
            .Where(value => !value.IsInTable && !value.IsHeading && !headingIndexes.Contains(value.Index))
            .Where(HasVisibleContent)
            .OrderBy(value => value.Index);
    }

    public static IEnumerable<ParsedRun> VisibleRuns(ParsedParagraph paragraph) => paragraph.RunList
        .Where(value => !value.IsDeleted && !value.IsHidden)
        .Where(value => value.EffectiveFormatting?.Hidden.Value != true)
        .Where(value => value.TextSegments.Any(segment => !string.IsNullOrWhiteSpace(segment)))
        .OrderBy(value => value.Index);

    public static bool HasVisibleContent(ParsedParagraph paragraph) => VisibleRuns(paragraph).Any();

    public static LayoutFindingActual Actual<T>(
        string property,
        ResolvedFormattingValue<T> value,
        string unit,
        DocumentElementLocation location,
        string? normalized = null) => new(
            property,
            Invariant(value.Value),
            normalized ?? Invariant(value.Value),
            unit,
            value.State,
            value.Provenance.SourceKind,
            value.Provenance.SourceStyleId,
            value.Provenance.Inherited,
            value.Provenance.DiagnosticCode,
            location.SectionIndex,
            location.ParagraphIndex,
            location.RunIndex);

    public static LayoutFindingExpected Expected(
        AuditRuleSnapshot snapshot,
        string property,
        IEnumerable<string> values,
        string unit,
        string? tolerance = null) => new(
            property,
            values.ToArray(),
            unit,
            tolerance,
            "resolved-snapshot-validation-key",
            snapshot.ValidationKey);

    public static LayoutFindingLocation Location(DocumentElementLocation location) => new(
        location.ToCompactString(),
        location.SectionIndex,
        location.BodyElementIndex,
        location.ParagraphIndex,
        location.RunIndex);

    public static string? Invariant<T>(T value) => value switch
    {
        null => null,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    public static bool Mismatch<T>(ResolvedFormattingValue<T?> actual, T expected, decimal tolerance = 0m)
        where T : struct, IComparable<T>
    {
        if (actual.State != FormattingResolutionState.Resolved || actual.Value is null) return true;
        if (typeof(T) == typeof(long))
        {
            var left = Convert.ToDecimal(actual.Value, CultureInfo.InvariantCulture);
            var right = Convert.ToDecimal(expected, CultureInfo.InvariantCulture);
            return Math.Abs(left - right) > tolerance;
        }
        return actual.Value.Value.CompareTo(expected) != 0;
    }

    public static DocumentElementLocation SectionLocation(ParsedSection section) => section.Location ?? new(
        DocumentPartKind.MainDocument,
        "/word/document.xml",
        SectionIndex: section.Index,
        ElementKind: DocumentElementKind.Section);
}
