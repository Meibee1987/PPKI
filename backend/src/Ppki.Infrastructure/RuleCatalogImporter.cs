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
            ["PPKI-LAY-019"] = "body.justified"
        };

    public static async Task ImportAsync(
        PpkiDbContext db,
        string catalogPath,
        CancellationToken cancellationToken = default)
    {
        if (await db.Rules.AnyAsync(cancellationToken))
        {
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
