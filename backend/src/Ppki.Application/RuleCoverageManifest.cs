using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Ppki.Application;

public enum RuleImplementationStatus
{
    Implemented,
    Partial,
    Manual
}

public sealed record RuleCoverageEntry(
    string RuleCode,
    string? ValidationKey,
    RuleImplementationStatus Status,
    string? ImplementationVersion,
    string? CapabilityId,
    string? CapabilityVersion,
    IReadOnlyList<string> TestCoverage);

public sealed record RuleCoverageCapability(
    string ValidationKey,
    string CapabilityId,
    string CapabilityVersion);

public static class RuleCoverageManifest
{
    private static readonly IReadOnlyList<RuleCoverageEntry> Definitions = Array.AsReadOnly(new[]
    {
        Implemented("PPKI-ABS-001", "abstract.skripsi-language-pair", "Wave1AbstractValidatorTests"),
        Implemented("PPKI-ABS-003", "abstract.skripsi-narrative-paragraph-count-one", "Wave1AbstractValidatorTests"),
        Implemented("PPKI-ABS-004", "abstract.skripsi-word-count-max-200", "Wave1AbstractValidatorTests"),
        Manual("PPKI-ABS-007"),
        Manual("PPKI-ABS-009"),
        Implemented("PPKI-ABS-011", "abstract.skripsi-single-spacing-zero-paragraph-spacing", "Wave1AbstractValidatorTests",
            "abstract-spacing-direct-paragraph", "1.0"),
        Implemented("PPKI-ABS-013", "summary.thesis-dissertation-language-pair", "Wave1AbstractValidatorTests"),
        Implemented("PPKI-ABS-019", "abstract-summary-single-spacing-zero-paragraph-spacing", "Wave1AbstractValidatorTests",
            "abstract-spacing-direct-paragraph", "1.0"),
        Manual("PPKI-FIG-003"),
        Manual("PPKI-FIG-007"),
        Implemented("PPKI-HDG-001", "heading.chapter-number-upper-roman-no-period", "Wave1HeadingValidatorTests"),
        Implemented("PPKI-HDG-002", "heading.maximum-depth-3", "Wave1HeadingValidatorTests"),
        Implemented("PPKI-HDG-003", "heading.chapter-uppercase", "Wave1HeadingValidatorTests"),
        Implemented("PPKI-HDG-004", "heading.chapter-bold", "Wave1HeadingValidatorTests",
            "chapter-bold-direct-heading-runs", "1.0"),
        Implemented("PPKI-HDG-005", "heading.chapter-no-period-no-underline", "Wave1HeadingValidatorTests",
            "chapter-decoration-direct-heading-runs", "1.0"),
        Implemented("PPKI-HDG-006", "heading.chapter-centered", "Wave1HeadingValidatorTests",
            "chapter-centered-direct-paragraph", "1.0"),
        Implemented("PPKI-HDG-007", "heading.subheading-decimal-left", "Wave1HeadingValidatorTests",
            "subheading-left-direct-paragraph", "1.0"),
        Manual("PPKI-HDG-008"),
        Implemented("PPKI-HDG-009", "heading.subheading-bold-no-period-no-underline", "Wave1HeadingValidatorTests",
            "subheading-decoration-direct-heading-runs", "1.0"),
        Implemented("PPKI-HDG-011", "heading.subsubheading-decimal-left", "Wave1HeadingValidatorTests",
            "subsubheading-left-direct-paragraph", "1.0"),
        Implemented("PPKI-HDG-013", "heading.subsubheading-regular-no-period-no-underline", "Wave1HeadingValidatorTests",
            "subsubheading-decoration-direct-heading-runs", "1.0"),
        Implemented("PPKI-LAY-003", "section.page-size-a4", "SectionPageLayoutFixProviderTests",
            "section-page-size-a4", "1.0"),
        Implemented("PPKI-LAY-005", "body.font-times-new-roman-12", "BodyFontSizeFixProviderTests",
            "body-font-direct-run", "1.0"),
        Implemented("PPKI-LAY-008", "section.margin-left-4cm", "SectionPageLayoutFixProviderTests",
            "section-margin-direct", "1.0"),
        Implemented("PPKI-LAY-009", "section.margin-right-3cm", "SectionPageLayoutFixProviderTests",
            "section-margin-direct", "1.0"),
        Implemented("PPKI-LAY-010", "section.margin-top-3cm", "SectionPageLayoutFixProviderTests",
            "section-margin-direct", "1.0"),
        Implemented("PPKI-LAY-011", "section.margin-bottom-3cm", "SectionPageLayoutFixProviderTests",
            "section-margin-direct", "1.0"),
        Implemented("PPKI-LAY-017", "body.line-spacing-single", "ParagraphFormatFixProviderTests",
            "body-line-spacing-direct-paragraph", "1.0"),
        Implemented("PPKI-LAY-018", "body.first-line-indent-1cm", "ParagraphFormatFixProviderTests",
            "body-first-line-indent-direct-paragraph", "1.0"),
        Implemented("PPKI-LAY-019", "body.justified", "ParagraphFormatFixProviderTests",
            "body-justified-direct-paragraph", "1.0"),
        Manual("PPKI-STR-001"),
        Manual("PPKI-STR-021"),
        Manual("PPKI-STR-022"),
        Manual("PPKI-TBL-012")
    });

    public static IReadOnlyList<RuleCoverageEntry> Entries { get; } = Definitions;

    public static IReadOnlyDictionary<string, string> ImplementedMappings { get; } =
        new ReadOnlyDictionary<string, string>(Definitions
            .Where(value => value.Status == RuleImplementationStatus.Implemented)
            .ToDictionary(value => value.RuleCode, value => value.ValidationKey!, StringComparer.OrdinalIgnoreCase));

    private static RuleCoverageEntry Implemented(
        string ruleCode,
        string validationKey,
        string testCoverage,
        string? capabilityId = null,
        string? capabilityVersion = null) =>
        new(ruleCode, validationKey, RuleImplementationStatus.Implemented, "1.0", capabilityId,
            capabilityVersion, Array.AsReadOnly(new[] { testCoverage }));

    private static RuleCoverageEntry Manual(string ruleCode) =>
        new(ruleCode, null, RuleImplementationStatus.Manual, null, null, null, Array.Empty<string>());
}

public static partial class RuleCoverageQualityGate
{
    public const int MinimumTargetRuleCount = 30;

    public static void Validate(
        IEnumerable<RuleCoverageEntry> entries,
        IEnumerable<string> catalogRuleCodes,
        IEnumerable<string> registeredValidationKeys,
        IEnumerable<RuleCoverageCapability> registeredCapabilities)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(catalogRuleCodes);
        ArgumentNullException.ThrowIfNull(registeredValidationKeys);
        ArgumentNullException.ThrowIfNull(registeredCapabilities);

        var manifest = entries.OrderBy(value => value.RuleCode, StringComparer.Ordinal).ToArray();
        if (manifest.Length < MinimumTargetRuleCount)
            throw new InvalidOperationException($"Rule coverage manifest must contain at least {MinimumTargetRuleCount} target rules.");

        var duplicateRuleCode = manifest.GroupBy(value => value.RuleCode, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRuleCode is not null)
            throw new InvalidOperationException($"Rule coverage manifest contains duplicate RuleCode '{duplicateRuleCode.Key}'.");

        var catalog = catalogRuleCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validators = registeredValidationKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capabilities = registeredCapabilities
            .ToDictionary(value => value.ValidationKey, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifest)
        {
            if (string.IsNullOrWhiteSpace(entry.RuleCode) || !catalog.Contains(entry.RuleCode))
                throw new InvalidOperationException($"Manifest RuleCode '{entry.RuleCode}' is absent from the authoritative catalog.");

            var hasRuntimeValidator = entry.Status == RuleImplementationStatus.Implemented
                || (entry.Status == RuleImplementationStatus.Partial
                    && !string.IsNullOrWhiteSpace(entry.ValidationKey));
            if (entry.Status == RuleImplementationStatus.Implemented && string.IsNullOrWhiteSpace(entry.ValidationKey))
                throw new InvalidOperationException($"Implemented rule '{entry.RuleCode}' must declare a ValidationKey.");
            if (hasRuntimeValidator && !validators.Contains(entry.ValidationKey!))
                throw new InvalidOperationException($"Rule '{entry.RuleCode}' references unregistered ValidationKey '{entry.ValidationKey}'.");
            if (hasRuntimeValidator && !ValidVersion().IsMatch(entry.ImplementationVersion ?? string.Empty))
                throw new InvalidOperationException($"Rule '{entry.RuleCode}' must declare a valid implementation version.");
            if (hasRuntimeValidator && (entry.TestCoverage.Count == 0
                || entry.TestCoverage.Any(value => !ValidTestIdentifier().IsMatch(value))))
                throw new InvalidOperationException($"Rule '{entry.RuleCode}' must declare valid automated test coverage metadata.");

            var hasCapabilityId = !string.IsNullOrWhiteSpace(entry.CapabilityId);
            var hasCapabilityVersion = !string.IsNullOrWhiteSpace(entry.CapabilityVersion);
            if (hasCapabilityId != hasCapabilityVersion)
                throw new InvalidOperationException($"Rule '{entry.RuleCode}' has incomplete fixer capability metadata.");
            var actualCapability = entry.ValidationKey is not null && capabilities.TryGetValue(entry.ValidationKey, out var capability)
                ? capability : null;
            if (actualCapability is null && hasCapabilityId)
                throw new InvalidOperationException($"Rule '{entry.RuleCode}' claims an unregistered fixer capability.");
            if (actualCapability is not null && (!string.Equals(entry.CapabilityId, actualCapability.CapabilityId, StringComparison.Ordinal)
                || !string.Equals(entry.CapabilityVersion, actualCapability.CapabilityVersion, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Rule '{entry.RuleCode}' fixer capability metadata does not match the production registry.");

            if (entry.Status == RuleImplementationStatus.Manual
                && (entry.ValidationKey is not null || entry.ImplementationVersion is not null
                    || hasCapabilityId || entry.TestCoverage.Count > 0))
                throw new InvalidOperationException($"Manual rule '{entry.RuleCode}' must not claim compiled runtime coverage.");
        }
    }

    [GeneratedRegex(@"^[1-9][0-9]*\.[0-9]+(?:\.[0-9]+)?$")]
    private static partial Regex ValidVersion();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.]+Tests$")]
    private static partial Regex ValidTestIdentifier();
}

public static class RuleCoverageDocumentation
{
    public static string Render(IEnumerable<RuleCoverageEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ordered = entries.OrderBy(value => value.RuleCode, StringComparer.Ordinal).ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("# PPKI MVP Rule Coverage");
        builder.AppendLine();
        builder.AppendLine("Generated deterministically from `Ppki.Application.RuleCoverageManifest`. Do not edit this table manually.");
        builder.AppendLine();
        builder.AppendLine($"Target rules: {ordered.Length}; Implemented: {ordered.Count(value => value.Status == RuleImplementationStatus.Implemented)}; Partial: {ordered.Count(value => value.Status == RuleImplementationStatus.Partial)}; Manual/non-automated: {ordered.Count(value => value.Status == RuleImplementationStatus.Manual)}.");
        builder.AppendLine();
        builder.AppendLine("- **Implemented**: a registered deterministic validator covers the catalog requirement and has real automated tests.");
        builder.AppendLine("- **Partial**: compiled validation covers only part of the official requirement; the requirement is not weakened.");
        builder.AppendLine("- **Manual/non-automated**: no compiled validator is currently claimed; reviewer judgment remains required.");
        builder.AppendLine();
        builder.AppendLine("| RuleCode | ValidationKey | Status | Implementation version | Fixer capability | Test coverage |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var entry in ordered)
        {
            var capability = entry.CapabilityId is null ? "—" : $"`{entry.CapabilityId}@{entry.CapabilityVersion}`";
            var tests = entry.TestCoverage.Count == 0 ? "—" : string.Join(", ", entry.TestCoverage.Select(value => $"`{value}`"));
            builder.Append("| `").Append(entry.RuleCode).Append("` | ")
                .Append(entry.ValidationKey is null ? "—" : $"`{entry.ValidationKey}`").Append(" | ")
                .Append(entry.Status == RuleImplementationStatus.Manual ? "Manual/non-automated" : entry.Status).Append(" | ")
                .Append(entry.ImplementationVersion is null ? "—" : $"`{entry.ImplementationVersion}`").Append(" | ")
                .Append(capability).Append(" | ").Append(tests).AppendLine(" |");
        }
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
