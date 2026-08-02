using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.RuleEngine;

public sealed class PageSizeA4Validator : IRuleValidator
{
    public string ValidationKey => "section.page-size-a4";

    public IReadOnlyList<RuleFinding> Validate(ParsedDocument document, RuleDefinition rule)
    {
        return document.Sections
            .Where(section => !IsA4(section))
            .Select(section => new RuleFinding(
                $"Section {section.Index + 1} tidak menggunakan ukuran A4.",
                new { section.WidthCm, section.HeightCm },
                new { WidthCm = 21.0m, HeightCm = 29.7m },
                new { Section = section.Index + 1 }))
            .ToList();
    }

    private static bool IsA4(ParsedSection section)
    {
        if (section.WidthCm is null || section.HeightCm is null)
        {
            return false;
        }

        var portrait = Close(section.WidthCm.Value, 21m) && Close(section.HeightCm.Value, 29.7m);
        var landscape = Close(section.WidthCm.Value, 29.7m) && Close(section.HeightCm.Value, 21m);
        return portrait || landscape;
    }

    private static bool Close(decimal actual, decimal expected) => Math.Abs(actual - expected) <= 0.15m;
}

public abstract class MarginValidatorBase(
    string validationKey,
    string side,
    decimal expectedCm) : IRuleValidator
{
    public string ValidationKey { get; } = validationKey;

    public IReadOnlyList<RuleFinding> Validate(ParsedDocument document, RuleDefinition rule)
    {
        return document.Sections
            .Select(section => new { Section = section, Value = GetValue(section) })
            .Where(x => x.Value is null || Math.Abs(x.Value.Value - expectedCm) > 0.05m)
            .Select(x => new RuleFinding(
                $"Margin {side} section {x.Section.Index + 1} tidak sesuai PPKI.",
                new { Side = side, ValueCm = x.Value },
                new { Side = side, ValueCm = expectedCm },
                new { Section = x.Section.Index + 1 }))
            .ToList();
    }

    protected abstract decimal? GetValue(ParsedSection section);
}

public sealed class MarginLeftValidator() : MarginValidatorBase("section.margin-left-4cm", "kiri", 4m)
{
    protected override decimal? GetValue(ParsedSection section) => section.MarginLeftCm;
}

public sealed class MarginRightValidator() : MarginValidatorBase("section.margin-right-3cm", "kanan", 3m)
{
    protected override decimal? GetValue(ParsedSection section) => section.MarginRightCm;
}

public sealed class MarginTopValidator() : MarginValidatorBase("section.margin-top-3cm", "atas", 3m)
{
    protected override decimal? GetValue(ParsedSection section) => section.MarginTopCm;
}

public sealed class MarginBottomValidator() : MarginValidatorBase("section.margin-bottom-3cm", "bawah", 3m)
{
    protected override decimal? GetValue(ParsedSection section) => section.MarginBottomCm;
}
