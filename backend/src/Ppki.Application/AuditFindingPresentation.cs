using System.Globalization;
using System.Text.Json;

namespace Ppki.Application;

public static class AuditFindingPresentation
{
    public static AuditFindingPresentationDto Create(string actualJson, string expectedJson)
    {
        try
        {
            using var actual = JsonDocument.Parse(actualJson);
            using var expected = JsonDocument.Parse(expectedJson);
            var property = String(actual.RootElement, "Property", "property");
            var expectedProperty = String(expected.RootElement, "Property", "property");
            if (property is null || (expectedProperty is not null && expectedProperty != property))
                return Unavailable();

            var current = String(actual.RootElement, "NormalizedValue", "normalizedValue");
            var accepted = Strings(expected.RootElement, "AcceptedValues", "acceptedValues");
            return property switch
            {
                "sectionPresence.SummaryIndonesian" => SectionPresence(
                    "Ringkasan Bahasa Indonesia", "Ringkasan Bahasa Indonesia tidak ditemukan.",
                    "Dokumen harus memiliki Ringkasan Bahasa Indonesia.", current, accepted),
                "sectionPresence.SummaryEnglish" => SectionPresence(
                    "Summary Bahasa Inggris", "Summary Bahasa Inggris tidak ditemukan.",
                    "Dokumen harus memiliki Summary Bahasa Inggris.", current, accepted),
                "alignment" => Comparison("Perataan teks", "Perataan teks tidak sesuai persyaratan.",
                    Enum(current), accepted.Select(Enum).FirstOrDefault()),
                "numberingTrailingPeriod" => Comparison("Tanda titik setelah nomor",
                    "Tanda titik setelah nomor tidak sesuai persyaratan.", Boolean(current),
                    accepted.Select(Boolean).FirstOrDefault()),
                "numberingFormat" => Comparison("Format nomor", "Format nomor tidak sesuai persyaratan.",
                    Numbering(current), accepted.Select(Numbering).FirstOrDefault()),
                "numberingPattern" => Comparison("Pola penomoran", "Pola penomoran tidak sesuai persyaratan.",
                    Numbering(current), accepted.Select(Numbering).FirstOrDefault()),
                "marginLeft" => Comparison("Margin kiri", "Margin kiri tidak sesuai persyaratan.",
                    TwipCentimetres(current), accepted.Select(TwipCentimetres).FirstOrDefault()),
                "firstLineIndent" => Comparison("Indentasi baris pertama", "Indentasi baris pertama tidak sesuai persyaratan.",
                    TwipCentimetres(current), accepted.Select(TwipCentimetres).FirstOrDefault()),
                "lineSpacingValue" => Comparison("Jarak baris", "Jarak baris tidak sesuai persyaratan.",
                    LineSpacing(current), accepted.Select(LineSpacing).FirstOrDefault()),
                "lineSpacingRule" => Comparison("Aturan jarak baris", "Aturan jarak baris tidak sesuai persyaratan.",
                    Enum(current), accepted.Select(Enum).FirstOrDefault()),
                "spacingBeforeTwips" => Comparison("Jarak sebelum paragraf", "Jarak sebelum paragraf tidak sesuai persyaratan.",
                    TwipPoints(current), accepted.Select(TwipPoints).FirstOrDefault()),
                "spacingAfterTwips" => Comparison("Jarak setelah paragraf", "Jarak setelah paragraf tidak sesuai persyaratan.",
                    TwipPoints(current), accepted.Select(TwipPoints).FirstOrDefault()),
                "fontSize" => Comparison("Ukuran huruf", "Ukuran huruf tidak sesuai persyaratan.",
                    HalfPoint(current), accepted.Select(HalfPoint).FirstOrDefault()),
                "font.ascii" or "font.highAnsi" => Comparison("Jenis huruf", "Jenis huruf tidak sesuai persyaratan.",
                    Font(current), accepted.Select(Font).FirstOrDefault()),
                _ => Unavailable(property)
            };
        }
        catch (JsonException)
        {
            return Unavailable();
        }
    }

    private static AuditFindingPresentationDto SectionPresence(
        string label, string problem, string requirement, string? current, IReadOnlyList<string> accepted)
    {
        var before = current switch { "absent" => "Belum tersedia", "present" => "Tersedia", _ => null };
        var required = accepted.Contains("present", StringComparer.Ordinal) ? requirement : null;
        return Dto("SectionRequirement", label, problem, "Ditemukan", before, "Wajib", required);
    }

    private static AuditFindingPresentationDto Comparison(
        string label, string problem, string? before, string? expected) =>
        Dto("StructuralComparison", label, problem, "Sebelum", before, "Diharapkan", expected);

    private static AuditFindingPresentationDto Dto(
        string kind, string label, string problem, string beforeLabel, string? before,
        string expectedLabel, string? expected) => new(kind, label, problem, beforeLabel,
            before, expectedLabel, expected, before is not null && expected is not null
                ? "Complete" : before is not null || expected is not null ? "Partial" : "Unavailable");

    private static AuditFindingPresentationDto Unavailable(string? property = null) => new(
        "Unavailable", PropertyLabel(property),
        "Temuan ini memerlukan pemeriksaan pada dokumen.", "Sebelum", null,
        "Diharapkan", null, "Unavailable");

    private static string PropertyLabel(string? property) => property switch
    {
        "sectionPresence.SummaryIndonesian" => "Ringkasan Bahasa Indonesia",
        "sectionPresence.SummaryEnglish" => "Summary Bahasa Inggris",
        "numberingTrailingPeriod" => "Tanda titik setelah nomor",
        "numberingPattern" => "Pola penomoran",
        "alignment" => "Perataan teks",
        "marginLeft" => "Margin kiri",
        _ => "Persyaratan dokumen"
    };

    private static string? String(JsonElement root, string first, string second)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!(root.TryGetProperty(first, out var value) || root.TryGetProperty(second, out value))
            || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > 80 ? null : text;
    }

    private static IReadOnlyList<string> Strings(JsonElement root, string first, string second)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !(root.TryGetProperty(first, out var value) || root.TryGetProperty(second, out value))
            || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Take(3).Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim()).Where(item => !string.IsNullOrEmpty(item) && item.Length <= 80)
            .Select(item => item!).ToArray();
    }

    private static string? Enum(string? value) => value switch
    {
        "Left" => "Kiri", "Center" => "Tengah", "Right" => "Kanan",
        "Justified" => "Rata kiri-kanan", "auto" => "Otomatis", "single" => "Tunggal",
        "unresolved" or null or "" => null, _ => null
    };

    private static string? Boolean(string? value) => value switch
    {
        "true" => "Dengan tanda titik penutup", "false" => "Tanpa tanda titik penutup",
        _ => null
    };

    private static string? Numbering(string? value) => value switch
    {
        "UpperRoman" => "Angka Romawi kapital (I, II, III)",
        "arabic-dotted-level-2" => "Angka Arab bertingkat dua (1.1, 1.2)",
        "arabic-dotted-level-3" => "Angka Arab bertingkat tiga (1.1.1)",
        "unresolved" or null or "" => null, _ => null
    };

    private static string? TwipCentimetres(string? value) => Decimal(value, out var number)
        ? $"{(number / 567m).ToString("0.##", CultureInfo.GetCultureInfo("id-ID"))} cm" : null;

    private static string? LineSpacing(string? value) => Decimal(value, out var number)
        ? $"{(number / 240m).ToString("0.##", CultureInfo.GetCultureInfo("id-ID"))} spasi" : null;

    private static string? HalfPoint(string? value) => Decimal(value, out var number)
        ? $"{(number / 2m).ToString("0.##", CultureInfo.GetCultureInfo("id-ID"))} pt" : null;

    private static string? TwipPoints(string? value) => Decimal(value, out var number)
        ? $"{(number / 20m).ToString("0.##", CultureInfo.GetCultureInfo("id-ID"))} pt" : null;

    private static string? Font(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsLetterOrDigit(character) || character is ' ' or '.' or '-' or '_')
        ? value : null;

    private static bool Decimal(string? value, out decimal number) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out number)
        && number is >= 0 and <= 100_000;
}
