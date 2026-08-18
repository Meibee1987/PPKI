using System.Globalization;
using System.Text;

namespace Ppki.DocxEngine;

public sealed record TextCorrectionCatalogRule(
    string RuleId,
    string Category,
    string SourcePattern,
    string SuggestedReplacement);

public sealed record DetectedTextCorrection(
    string RuleId,
    string Category,
    ExactTextAnchor Anchor,
    string SuggestedReplacement,
    string SuggestionHash);

/// <summary>
/// Explicit detection-only lexical search. Search results are immediately converted to exact
/// scalar anchors; downstream decision and apply paths never receive the matched source string.
/// </summary>
public sealed class DeterministicTextCorrectionDetector(
    IDocxParser parser,
    ExactTextAnchorMaterializer anchors)
{
    public const string DetectorId = "ppki-text-correction-detector";
    public const string DetectorVersion = "1.0";
    public const string CatalogVersion = "ppki-text-correction-catalog/1.0";
    public const int MaximumProposals = 100;

    public static IReadOnlyList<TextCorrectionCatalogRule> Catalog { get; } =
    [
        new("lex.di-analisa", "deterministic-lexical", "di analisa", "dianalisis"),
        new("lex.aktifitas", "deterministic-lexical", "aktifitas", "aktivitas"),
        new("lex.resiko", "deterministic-lexical", "resiko", "risiko")
    ];

    public async Task<IReadOnlyList<DetectedTextCorrection>> DetectAsync(
        string sourceDocxPath,
        Guid documentVersionId,
        string sourceSha256,
        CancellationToken cancellationToken = default)
    {
        var document = await parser.ParseAsync(sourceDocxPath, cancellationToken);
        var results = new List<DetectedTextCorrection>();
        foreach (var paragraph in document.Paragraphs.OrderBy(value => value.Index))
        {
            foreach (var rule in Catalog.OrderBy(value => value.RuleId, StringComparer.Ordinal))
            {
                var searchFrom = 0;
                while (searchFrom <= paragraph.Text.Length - rule.SourcePattern.Length)
                {
                    // IndexOf is confined to detection. It is not used by targeting or apply.
                    var utf16Index = paragraph.Text.IndexOf(rule.SourcePattern, searchFrom,
                        StringComparison.Ordinal);
                    if (utf16Index < 0) break;
                    searchFrom = utf16Index + rule.SourcePattern.Length;
                    if (!HasTokenBoundaries(paragraph.Text, utf16Index, rule.SourcePattern.Length)) continue;
                    var scalarStart = ScalarCount(paragraph.Text.AsSpan(0, utf16Index));
                    var scalarLength = rule.SourcePattern.EnumerateRunes().Count();
                    var target = await anchors.BuildAsync(sourceDocxPath, documentVersionId, sourceSha256,
                        paragraph.Index, scalarStart, scalarLength, cancellationToken);
                    if (target.Status != ExactTextTargetStatus.Exact || target.Anchor is null) continue;
                    results.Add(new(rule.RuleId, rule.Category, target.Anchor,
                        rule.SuggestedReplacement, ExactTextAnchorContract.Fingerprint(
                            "suggested-replacement", rule.SuggestedReplacement)));
                    if (results.Count >= MaximumProposals) return results;
                }
            }
        }
        return results;
    }

    private static bool HasTokenBoundaries(string value, int start, int length)
    {
        var before = start == 0 ? (Rune?)null : LastRune(value.AsSpan(0, start));
        var afterIndex = start + length;
        var after = afterIndex == value.Length ? (Rune?)null : FirstRune(value.AsSpan(afterIndex));
        return !IsToken(before) && !IsToken(after);
    }

    private static bool IsToken(Rune? rune) => rune is not null &&
        (Rune.IsLetterOrDigit(rune.Value) || Rune.GetUnicodeCategory(rune.Value) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.ConnectorPunctuation);

    private static Rune FirstRune(ReadOnlySpan<char> value)
    {
        Rune.DecodeFromUtf16(value, out var rune, out _);
        return rune;
    }

    private static Rune LastRune(ReadOnlySpan<char> value)
    {
        Rune.DecodeLastFromUtf16(value, out var rune, out _);
        return rune;
    }

    private static int ScalarCount(ReadOnlySpan<char> value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) count++;
        return count;
    }
}
