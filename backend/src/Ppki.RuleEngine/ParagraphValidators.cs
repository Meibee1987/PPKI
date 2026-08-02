using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.RuleEngine;

public abstract class ParagraphAggregateValidator : IRuleValidator
{
    public abstract string ValidationKey { get; }
    protected abstract bool IsMismatch(ParsedParagraph paragraph);
    protected abstract object Actual(ParsedParagraph paragraph);
    protected abstract object Expected { get; }
    protected abstract string Message(int count);

    public IReadOnlyList<RuleFinding> Validate(ParsedDocument document, RuleDefinition rule)
    {
        var mismatches = document.Paragraphs
            .Where(IsBodyParagraph)
            .Where(IsMismatch)
            .ToList();

        if (mismatches.Count == 0)
        {
            return [];
        }

        var sample = mismatches.Take(20).ToList();
        return
        [
            new RuleFinding(
                Message(mismatches.Count),
                new
                {
                    Count = mismatches.Count,
                    Examples = sample.Select(x => new { Paragraph = x.Index + 1, Value = Actual(x) })
                },
                Expected,
                new
                {
                    Paragraphs = sample.Select(x => x.Index + 1),
                    Truncated = mismatches.Count > sample.Count
                },
                0.9m)
        ];
    }

    private static bool IsBodyParagraph(ParsedParagraph paragraph) =>
        !string.IsNullOrWhiteSpace(paragraph.Text)
        && !paragraph.IsHeading
        && !paragraph.IsInTable;
}

public sealed class BodyFontValidator : ParagraphAggregateValidator
{
    public override string ValidationKey => "body.font-times-new-roman-12";
    protected override object Expected => new { Font = "Times New Roman", SizePt = 12m };

    protected override bool IsMismatch(ParsedParagraph paragraph)
    {
        var fontMismatch = paragraph.FontName is not null
            && !paragraph.FontName.Equals("Times New Roman", StringComparison.OrdinalIgnoreCase);
        var sizeMismatch = paragraph.FontSizePt is not null
            && Math.Abs(paragraph.FontSizePt.Value - 12m) > 0.01m;
        return fontMismatch || sizeMismatch;
    }

    protected override object Actual(ParsedParagraph paragraph) =>
        new { paragraph.FontName, paragraph.FontSizePt };

    protected override string Message(int count) =>
        $"Ditemukan {count} paragraf isi dengan font atau ukuran yang tidak sesuai.";
}

public sealed class LineSpacingValidator : ParagraphAggregateValidator
{
    public override string ValidationKey => "body.line-spacing-single";
    protected override object Expected => new { LineSpacing = 1m };
    protected override bool IsMismatch(ParsedParagraph paragraph) =>
        paragraph.LineSpacingMultiple is not null
        && Math.Abs(paragraph.LineSpacingMultiple.Value - 1m) > 0.05m;
    protected override object Actual(ParsedParagraph paragraph) =>
        new { paragraph.LineSpacingMultiple };
    protected override string Message(int count) =>
        $"Ditemukan {count} paragraf isi yang tidak menggunakan spasi tunggal.";
}

public sealed class FirstLineIndentValidator : ParagraphAggregateValidator
{
    public override string ValidationKey => "body.first-line-indent-1cm";
    protected override object Expected => new { FirstLineIndentCm = 1m };
    protected override bool IsMismatch(ParsedParagraph paragraph) =>
        paragraph.FirstLineIndentCm is not null
        && Math.Abs(paragraph.FirstLineIndentCm.Value - 1m) > 0.05m;
    protected override object Actual(ParsedParagraph paragraph) =>
        new { paragraph.FirstLineIndentCm };
    protected override string Message(int count) =>
        $"Ditemukan {count} paragraf isi dengan indentasi baris pertama yang tidak sesuai.";
}

public sealed class JustifiedValidator : ParagraphAggregateValidator
{
    public override string ValidationKey => "body.justified";
    protected override object Expected => new { Alignment = "Both/Justified" };
    protected override bool IsMismatch(ParsedParagraph paragraph) =>
        !paragraph.Alignment.Equals("Both", StringComparison.OrdinalIgnoreCase)
        && !paragraph.Alignment.Equals("Justified", StringComparison.OrdinalIgnoreCase);
    protected override object Actual(ParsedParagraph paragraph) =>
        new { paragraph.Alignment };
    protected override string Message(int count) =>
        $"Ditemukan {count} paragraf isi yang belum rata kanan-kiri.";
}
