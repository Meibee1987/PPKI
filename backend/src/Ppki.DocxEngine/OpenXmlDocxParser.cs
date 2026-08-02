using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Ppki.DocxEngine;

public sealed class OpenXmlDocxParser : IDocxParser
{
    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var document = WordprocessingDocument.Open(filePath, false);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("DOCX does not contain a MainDocumentPart.");
        var body = mainPart.Document?.Body
            ?? throw new InvalidDataException("The DOCX file does not contain a document body.");

        var styles = mainPart.StyleDefinitionsPart?.Styles;
        var defaults = ResolveDefaults(styles);
        var sections = ParseSections(body);
        var paragraphs = ParseParagraphs(body, styles, defaults);

        return Task.FromResult(new ParsedDocument(sections, paragraphs));
    }

    private static IReadOnlyList<ParsedSection> ParseSections(Body body)
    {
        var properties = body.Descendants<SectionProperties>().ToList();
        if (properties.Count == 0)
        {
            return [new ParsedSection(0, null, null, null, null, null, null)];
        }

        return properties.Select((section, index) =>
        {
            var pageSize = section.GetFirstChild<PageSize>();
            var margin = section.GetFirstChild<PageMargin>();
            return new ParsedSection(
                index,
                TwipsToCm(pageSize?.Width?.Value),
                TwipsToCm(pageSize?.Height?.Value),
                TwipsToCm(margin?.Top?.Value),
                TwipsToCm(margin?.Right?.Value),
                TwipsToCm(margin?.Bottom?.Value),
                TwipsToCm(margin?.Left?.Value));
        }).ToList();
    }

    private static IReadOnlyList<ParsedParagraph> ParseParagraphs(
        Body body,
        Styles? styles,
        TextDefaults defaults)
    {
        return body.Descendants<Paragraph>().Select((paragraph, index) =>
        {
            var text = paragraph.InnerText.Trim();
            var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            var style = styles?.Elements<Style>()
                .FirstOrDefault(x => string.Equals(x.StyleId?.Value, styleId, StringComparison.OrdinalIgnoreCase));
            var firstRunProperties = paragraph.Elements<Run>()
                .Select(x => x.RunProperties)
                .FirstOrDefault(x => x is not null);

            var font = FirstNonEmpty(
                firstRunProperties?.RunFonts?.Ascii?.Value,
                firstRunProperties?.RunFonts?.HighAnsi?.Value,
                style?.StyleRunProperties?.RunFonts?.Ascii?.Value,
                style?.StyleRunProperties?.RunFonts?.HighAnsi?.Value,
                defaults.FontName);

            var fontSize = HalfPointsToPoints(FirstNonEmpty(
                firstRunProperties?.FontSize?.Val?.Value,
                style?.StyleRunProperties?.FontSize?.Val?.Value,
                defaults.FontSizeHalfPoints));

            var alignment = paragraph.ParagraphProperties?.Justification?.Val?.Value.ToString()
                ?? style?.StyleParagraphProperties?.Justification?.Val?.Value.ToString()
                ?? "Left";

            var spacing = paragraph.ParagraphProperties?.SpacingBetweenLines
                ?? style?.StyleParagraphProperties?.SpacingBetweenLines;
            var lineSpacing = ResolveLineSpacing(spacing);

            var indentation = paragraph.ParagraphProperties?.Indentation
                ?? style?.StyleParagraphProperties?.Indentation;
            var firstLineIndent = TwipsStringToCm(indentation?.FirstLine?.Value);

            return new ParsedParagraph(
                index,
                text,
                styleId,
                styleId?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true,
                paragraph.Ancestors<Table>().Any(),
                font,
                fontSize,
                alignment,
                lineSpacing,
                firstLineIndent);
        }).ToList();
    }

    private static TextDefaults ResolveDefaults(Styles? styles)
    {
        var runDefaults = styles?.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        return new TextDefaults(
            FirstNonEmpty(runDefaults?.RunFonts?.Ascii?.Value, runDefaults?.RunFonts?.HighAnsi?.Value),
            runDefaults?.FontSize?.Val?.Value);
    }

    private static decimal? ResolveLineSpacing(SpacingBetweenLines? spacing)
    {
        var line = spacing?.Line?.Value;
        if (!decimal.TryParse(line, out var value))
        {
            return null;
        }

        var rule = spacing?.LineRule?.Value;
        return rule is null || rule == LineSpacingRuleValues.Auto
            ? Math.Round(value / 240m, 2)
            : null;
    }

    private static decimal? HalfPointsToPoints(string? value) =>
        decimal.TryParse(value, out var halfPoints) ? halfPoints / 2m : null;

    private static decimal? TwipsStringToCm(string? value) =>
        decimal.TryParse(value, out var twips) ? TwipsToCm(twips) : null;

    private static decimal? TwipsToCm(long? twips) =>
        twips is null ? null : Math.Round(twips.Value / 1440m * 2.54m, 2);

    private static decimal TwipsToCm(decimal twips) =>
        Math.Round(twips / 1440m * 2.54m, 2);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private sealed record TextDefaults(string? FontName, string? FontSizeHalfPoints);
}
