namespace Ppki.DocxEngine;

public sealed record ParsedDocument(
    IReadOnlyList<ParsedSection> Sections,
    IReadOnlyList<ParsedParagraph> Paragraphs);

public sealed record ParsedSection(
    int Index,
    decimal? WidthCm,
    decimal? HeightCm,
    decimal? MarginTopCm,
    decimal? MarginRightCm,
    decimal? MarginBottomCm,
    decimal? MarginLeftCm);

public sealed record ParsedParagraph(
    int Index,
    string Text,
    string? StyleId,
    bool IsHeading,
    bool IsInTable,
    string? FontName,
    decimal? FontSizePt,
    string Alignment,
    decimal? LineSpacingMultiple,
    decimal? FirstLineIndentCm);

public interface IDocxParser
{
    Task<ParsedDocument> ParseAsync(string filePath, CancellationToken cancellationToken);
}
