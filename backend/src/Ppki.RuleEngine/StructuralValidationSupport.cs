using System.Globalization;
using System.Text;
using System.Text.Json;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.RuleEngine;

public static class StructuralValidatorLimits
{
    public const int MaximumHeadingCharacters = 512;
    public const int MaximumNarrativeCharacters = 100_000;
    public const int MaximumOrderingEntries = 1_000;
}

internal static class StructuralValidationSupport
{

    public static RuleValidationResult? CheckApplicability(
        RuleValidationContext context,
        out JsonElement configuration)
    {
        try
        {
            using var document = JsonDocument.Parse(context.Snapshot.ValidationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                configuration = default;
                return RuleValidationResult.Invalid("validation-json-invalid");
            }
            configuration = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            configuration = default;
            return RuleValidationResult.Invalid("validation-json-invalid");
        }

        var selector = context.Snapshot.AppliesTo.Trim();
        if (selector.Equals("Semua", StringComparison.OrdinalIgnoreCase)
            || selector.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (context.DocumentKind is null)
            return RuleValidationResult.Invalid("document-kind-required");

        if (selector.Equals("Semua terkait", StringComparison.OrdinalIgnoreCase))
            return null;

        var accepted = selector.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseDocumentKind)
            .ToArray();
        if (accepted.Any(value => value is null))
            return RuleValidationResult.Unsupported("document-selector-unsupported");
        return accepted.Contains(context.DocumentKind)
            ? null
            : new RuleValidationResult(ValidationApplicability.NotApplicable, []);
    }

    public static int Integer(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new LayoutRuleConfigurationException("validation-parameter-invalid");
        return result;
    }

    public static IReadOnlyList<(ParsedHeading Heading, ParsedParagraph Paragraph)> Headings(
        RuleValidationContext context,
        JsonElement configuration)
    {
        var includeCandidates = LayoutValidationSupport.Boolean(configuration, "includeCandidates", false);
        var paragraphs = context.Document.Paragraphs.ToDictionary(value => value.Index);
        return context.Document.Headings
            .Where(value => value.Classification == HeadingClassification.Confirmed || includeCandidates)
            .Where(value => value.Location.PartKind == DocumentPartKind.MainDocument)
            .Where(value => paragraphs.TryGetValue(value.ParagraphIndex, out var paragraph) && !paragraph.IsInTable)
            .OrderBy(value => value.Order)
            .Select(value => (value, paragraphs[value.ParagraphIndex]))
            .ToArray();
    }

    public static IReadOnlySet<int> ConfirmedChapterHeadingIndexes(ParsedDocument document) =>
        document.DocumentStructure.Sections
            .Where(value => value.Kind == SemanticSectionKind.Chapter)
            .Where(value => value.ClassificationState == SemanticClassificationState.Confirmed)
            .Select(value => value.HeadingIndex)
            .ToHashSet();

    public static bool HasUnresolvedChapterClassification(ParsedDocument document) =>
        document.DocumentStructure.Sections.Any(value => value.Kind == SemanticSectionKind.Chapter
            && value.ClassificationState != SemanticClassificationState.Confirmed);

    public static IReadOnlyList<ParsedRun> VisibleRuns(ParsedParagraph paragraph) => paragraph.RunList
        .Where(value => !value.IsDeleted && !value.IsHidden)
        .Where(value => value.EffectiveFormatting?.Hidden.Value != true)
        .Where(value => value.TextSegments.Any(segment => !string.IsNullOrEmpty(segment)))
        .OrderBy(value => value.Index)
        .ToArray();

    public static HeadingTextInspection InspectHeadingText(ParsedParagraph paragraph)
    {
        var builder = new StringBuilder();
        foreach (var run in VisibleRuns(paragraph))
        {
            foreach (var segment in run.TextSegments)
            {
                if (builder.Length + segment.Length > StructuralValidatorLimits.MaximumHeadingCharacters)
                    return new(false, false, TrailingPunctuationCategory.Unresolved, true, builder.Length);
                builder.Append(segment);
            }
        }

        var normalized = builder.ToString().Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length > StructuralValidatorLimits.MaximumHeadingCharacters)
            return new(false, false, TrailingPunctuationCategory.Unresolved, true, normalized.Length);
        var hasLetter = normalized.EnumerateRunes().Any(Rune.IsLetter);
        var uppercase = hasLetter && string.Equals(normalized, normalized.ToUpperInvariant(), StringComparison.Ordinal);
        var punctuation = normalized.Length == 0
            ? TrailingPunctuationCategory.None
            : normalized[^1] == '.' ? TrailingPunctuationCategory.Period
            : char.IsPunctuation(normalized[^1]) ? TrailingPunctuationCategory.Other
            : TrailingPunctuationCategory.None;
        return new(normalized.Length > 0, uppercase, punctuation, false, normalized.Length);
    }

    public static IReadOnlyList<ParsedParagraph> NarrativeParagraphs(
        ParsedDocument document,
        AbstractSectionDescriptor descriptor)
    {
        if (descriptor.ContentStartLocation?.BodyElementIndex is not int startBodyIndex
            || descriptor.EndLocation.BodyElementIndex is not int endBodyIndex)
            return [];
        var keyword = descriptor.KeywordParagraphLocation?.ToCompactString();
        return document.Paragraphs
            .Where(value => value.Location?.PartKind == DocumentPartKind.MainDocument)
            .Where(value => !value.IsInTable && !value.IsHeading)
            .Where(value => value.Location?.BodyElementIndex is int bodyIndex
                && bodyIndex >= startBodyIndex && bodyIndex <= endBodyIndex)
            .Where(value => keyword is null || value.Location?.ToCompactString() != keyword)
            .Where(value => VisibleRuns(value).Count > 0)
            .OrderBy(value => value.Location!.BodyElementIndex)
            .ThenBy(value => value.Index)
            .ToArray();
    }

    public static int CountWords(IEnumerable<ParsedParagraph> paragraphs, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (builder.Length > 0) builder.Append(' ');
            foreach (var run in VisibleRuns(paragraph))
            {
                foreach (var segment in run.TextSegments)
                {
                    if (builder.Length + segment.Length > StructuralValidatorLimits.MaximumNarrativeCharacters)
                        throw new StructuralValidationLimitException("narrative-text-limit-exceeded");
                    builder.Append(segment);
                }
            }
        }

        var normalized = builder.ToString().Normalize(NormalizationForm.FormKC);
        if (normalized.Length > StructuralValidatorLimits.MaximumNarrativeCharacters)
            throw new StructuralValidationLimitException("narrative-text-limit-exceeded");
        var runes = normalized.EnumerateRunes().ToArray();
        var count = 0;
        var insideToken = false;
        for (var index = 0; index < runes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = runes[index];
            if (Rune.IsLetterOrDigit(current))
            {
                if (!insideToken) count++;
                insideToken = true;
                continue;
            }

            var internalJoiner = insideToken && IsWordJoiner(current)
                && index + 1 < runes.Length && Rune.IsLetterOrDigit(runes[index + 1]);
            if (!internalJoiner) insideToken = false;
        }
        return count;
    }

    public static LayoutFindingActual Actual(
        string property,
        string? rawValue,
        string? normalizedValue,
        string unit,
        DocumentElementLocation location,
        FormattingResolutionState state = FormattingResolutionState.Resolved,
        FormattingSourceKind sourceKind = FormattingSourceKind.Unspecified,
        string? sourceStyleId = null,
        bool inherited = false,
        string? diagnosticCode = null) => new(
            property,
            rawValue,
            normalizedValue,
            unit,
            state,
            sourceKind,
            sourceStyleId,
            inherited,
            diagnosticCode,
            location.SectionIndex,
            location.ParagraphIndex,
            location.RunIndex);

    public static LayoutFindingLocation DocumentLocation() =>
        LayoutValidationSupport.Location(new DocumentElementLocation(
            DocumentPartKind.MainDocument,
            "/word/document.xml"));

    private static DocumentKind? ParseDocumentKind(string value) => value.Trim() switch
    {
        var item when item.Equals("Laporan akhir", StringComparison.OrdinalIgnoreCase) => DocumentKind.LaporanAkhir,
        var item when item.Equals("Skripsi", StringComparison.OrdinalIgnoreCase) => DocumentKind.Skripsi,
        var item when item.Equals("Tesis", StringComparison.OrdinalIgnoreCase) => DocumentKind.Tesis,
        var item when item.Equals("Disertasi", StringComparison.OrdinalIgnoreCase) => DocumentKind.Disertasi,
        _ => null
    };

    private static bool IsWordJoiner(Rune value) => value.Value is '\'' or 0x2019 or '-' or 0x2010;
}

internal sealed record HeadingTextInspection(
    bool HasVisibleText,
    bool IsUppercase,
    TrailingPunctuationCategory TrailingPunctuation,
    bool LimitExceeded,
    int CharacterCount);

internal enum TrailingPunctuationCategory { None, Period, Other, Unresolved }

internal sealed class StructuralValidationLimitException(string code) : Exception
{
    public string Code { get; } = code;
}
