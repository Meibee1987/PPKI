namespace Ppki.DocxEngine;

public static class OpenXmlLayoutUnitConverter
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
                _ => throw new ArgumentOutOfRangeException(nameof(unit), "Unsupported Open XML layout unit.")
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Open XML layout value is out of range.");
        }
    }

    private static long Round(decimal value) =>
        decimal.ToInt64(decimal.Round(value, 0, MidpointRounding.AwayFromZero));

    private static string Normalize(string unit) => unit.Trim().ToLowerInvariant();
}
