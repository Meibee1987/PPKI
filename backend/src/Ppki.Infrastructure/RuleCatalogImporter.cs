using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public static class RuleCatalogImporter
{
    private static readonly IReadOnlyDictionary<string, string> ImplementedValidators =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PPKI-LAY-003"] = "section.page-size-a4",
            ["PPKI-LAY-005"] = "body.font-times-new-roman-12",
            ["PPKI-LAY-008"] = "section.margin-left-4cm",
            ["PPKI-LAY-009"] = "section.margin-right-3cm",
            ["PPKI-LAY-010"] = "section.margin-top-3cm",
            ["PPKI-LAY-011"] = "section.margin-bottom-3cm",
            ["PPKI-LAY-017"] = "body.line-spacing-single",
            ["PPKI-LAY-018"] = "body.first-line-indent-1cm",
            ["PPKI-LAY-019"] = "body.justified",
            ["PPKI-HDG-001"] = "heading.chapter-number-upper-roman-no-period",
            ["PPKI-HDG-002"] = "heading.maximum-depth-3",
            ["PPKI-HDG-003"] = "heading.chapter-uppercase",
            ["PPKI-HDG-004"] = "heading.chapter-bold",
            ["PPKI-HDG-005"] = "heading.chapter-no-period-no-underline",
            ["PPKI-HDG-006"] = "heading.chapter-centered",
            ["PPKI-HDG-007"] = "heading.subheading-decimal-left",
            ["PPKI-HDG-009"] = "heading.subheading-bold-no-period-no-underline",
            ["PPKI-HDG-011"] = "heading.subsubheading-decimal-left",
            ["PPKI-HDG-013"] = "heading.subsubheading-regular-no-period-no-underline",
            ["PPKI-ABS-001"] = "abstract.skripsi-language-pair",
            ["PPKI-ABS-003"] = "abstract.skripsi-narrative-paragraph-count-one",
            ["PPKI-ABS-004"] = "abstract.skripsi-word-count-max-200",
            ["PPKI-ABS-011"] = "abstract.skripsi-single-spacing-zero-paragraph-spacing",
            ["PPKI-ABS-013"] = "summary.thesis-dissertation-language-pair",
            ["PPKI-ABS-019"] = "abstract-summary-single-spacing-zero-paragraph-spacing"
        };

    public static async Task ImportAsync(
        PpkiDbContext db,
        string catalogPath,
        CancellationToken cancellationToken = default)
    {
        var existingRules = await db.Rules.ToListAsync(cancellationToken);
        if (existingRules.Count > 0)
        {
            if (ReconcileImplementedMappings(existingRules) > 0)
                await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var json = await File.ReadAllTextAsync(catalogPath, cancellationToken);
        var catalog = JsonSerializer.Deserialize<RuleCatalog>(json)
            ?? throw new InvalidOperationException("Rule catalog could not be parsed.");

        foreach (var source in catalog.Rules)
        {
            var implemented = ImplementedValidators.TryGetValue(source.RuleId, out var validationKey);
            db.Rules.Add(new RuleDefinition
            {
                RuleCode = source.RuleId,
                Domain = source.Domain,
                Subdomain = source.Subdomain,
                AppliesTo = source.AppliesTo,
                Element = source.Element,
                OfficialRequirement = source.OfficialRequirement,
                ExpectedValuePattern = source.ExpectedValuePattern,
                Severity = Enum.Parse<RuleSeverity>(source.Severity, ignoreCase: true),
                FixMode = Enum.Parse<FixMode>(source.FixMode, ignoreCase: true),
                ValidationKey = validationKey ?? "manual.not-implemented",
                IsImplemented = implemented,
                PdfPage = source.PdfPage,
                PrintedPage = source.PrintedPage?.ToString(),
                SourceSection = source.SourceSection
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    internal static int ReconcileImplementedMappings(IEnumerable<RuleDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var changed = 0;
        foreach (var rule in rules)
        {
            if (!ImplementedValidators.TryGetValue(rule.RuleCode, out var validationKey))
                continue;
            if (rule.IsImplemented && string.Equals(rule.ValidationKey, validationKey, StringComparison.Ordinal))
                continue;

            rule.ValidationKey = validationKey;
            rule.IsImplemented = true;
            changed++;
        }
        return changed;
    }

    private sealed record RuleCatalog(
        [property: JsonPropertyName("rules")] IReadOnlyList<RuleSource> Rules);

    private sealed record RuleSource(
        [property: JsonPropertyName("rule_id")] string RuleId,
        [property: JsonPropertyName("domain")] string Domain,
        [property: JsonPropertyName("subdomain")] string? Subdomain,
        [property: JsonPropertyName("applies_to")] string AppliesTo,
        [property: JsonPropertyName("element")] string Element,
        [property: JsonPropertyName("official_requirement")] string OfficialRequirement,
        [property: JsonPropertyName("expected_value_pattern")] string ExpectedValuePattern,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("fix_mode")] string FixMode,
        [property: JsonPropertyName("pdf_page")] int? PdfPage,
        [property: JsonPropertyName("printed_page")] JsonElement? PrintedPage,
        [property: JsonPropertyName("source_section")] string? SourceSection);
}
