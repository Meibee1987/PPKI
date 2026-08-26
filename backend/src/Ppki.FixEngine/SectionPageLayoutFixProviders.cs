using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.FixEngine;

internal static class SectionFixSnapshot
{
    public static bool Location(JsonElement root, out int body, out int section)
    {
        body = section = -1;
        if (!FormattingFixSnapshot.Integer(root, "bodyElementIndex", out body)
            || !FormattingFixSnapshot.Integer(root, "sectionIndex", out section)
            || body < 0 || section < 0) return false;
        var hasParagraph = FormattingFixSnapshot.Integer(root, "paragraphIndex", out var paragraph);
        if (hasParagraph && paragraph < 0) return false;
        var paragraphSegment = hasParagraph
            ? $"/p:{paragraph}" : string.Empty;
        return string.Equals(FormattingFixSnapshot.Text(root, "compactLocation"),
            $"maindocument/s:{section}/b:{body}{paragraphSegment}/kind:section", StringComparison.OrdinalIgnoreCase);
    }

    public static ParsedSection Section(FixApplyContext context)
    {
        var target = context.Operation.Target;
        return context.SourceDocument.Sections.SingleOrDefault(value => value.Index == target.SectionIndex
            && value.Location?.PartKind == DocumentPartKind.MainDocument
            && value.Location.BodyElementIndex == target.BodyElementIndex)
            ?? throw new FixExecutionException("fix-operation-target-precondition-failed");
    }

    public static SectionProperties XmlSection(FixApplyContext context, Body body)
    {
        var element = body.Elements().ElementAtOrDefault(context.Operation.Target.BodyElementIndex!.Value);
        var properties = element switch
        {
            SectionProperties direct => direct,
            Paragraph paragraph => paragraph.ParagraphProperties?.SectionProperties,
            _ => null
        };
        return properties ?? throw new FixExecutionException("fix-operation-target-precondition-failed");
    }

    public static bool ExactExpected(JsonElement root, string property, string validationKey,
        params string[] values)
    {
        if (FormattingFixSnapshot.Text(root, "property") != property
            || FormattingFixSnapshot.Text(root, "validationKey") != validationKey
            || FormattingFixSnapshot.Text(root, "unit") != "twip"
            || FormattingFixSnapshot.Text(root, "tolerance") != "0") return false;
        var accepted = root.EnumerateObject().FirstOrDefault(item =>
            item.Name.Equals("acceptedValues", StringComparison.OrdinalIgnoreCase));
        return accepted.Value.ValueKind == JsonValueKind.Array
            && accepted.Value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString() : null).SequenceEqual(values, StringComparer.Ordinal);
    }

    public static string Twips(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

public sealed class SectionPageSizeFixProvider : IFixPreviewProvider, IFixApplyProvider
{
    public const string Id = "section-page-size-a4";
    public const string Version = "1.0";
    private static readonly long PortraitWidth = OpenXmlLayoutUnitConverter.ToTwips(21m, "cm");
    private static readonly long PortraitHeight = OpenXmlLayoutUnitConverter.ToTwips(29.7m, "cm");
    private static readonly string Portrait = $"{PortraitWidth}x{PortraitHeight}";
    private static readonly string Landscape = $"{PortraitHeight}x{PortraitWidth}";

    public string CapabilityId => Id;
    public string CapabilityVersion => Version;
    public IReadOnlySet<string> ValidationKeys { get; } =
        new HashSet<string>(["section.page-size-a4"], StringComparer.Ordinal);

    public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation,
        out string diagnosticCode)
    {
        operation = null!;
        diagnosticCode = "fix-preview-provider-rejected-snapshot";
        if (!FormattingFixSnapshot.Common(finding, "section.page-size-a4", "PPKI-LAY-003")
            || !FormattingFixSnapshot.TryRead(finding, out var actual, out var expected, out var location)) return false;
        using (actual) using (expected) using (location)
        {
            var normalized = FormattingFixSnapshot.Text(actual.RootElement, "normalizedValue");
            var hasPair = TryPair(normalized, out var width, out var height);
            if (FormattingFixSnapshot.Text(actual.RootElement, "property") != "pageSize"
                || (!hasPair && normalized != "x")
                || !SectionFixSnapshot.ExactExpected(expected.RootElement, "pageSize", finding.ValidationKey,
                    Portrait, Landscape)
                || !SectionFixSnapshot.Location(location.RootElement, out var body, out var section)) return false;
            var wanted = hasPair && width > height ? Landscape : Portrait;
            operation = new(new("main-document-section", body, section, null, null), "section.page-size",
                new("twips-pair", wanted), "source-finding-snapshot-must-match", "set-section-page-size-a4");
            diagnosticCode = "fix-operation-planned";
            return true;
        }
    }

    public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCreate(context.Finding, out var approved, out _))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        FormattingFixSnapshot.ExactContract(context, this, approved, "section.page-size-a4", "PPKI-LAY-003");
        if (context.Operation.PropertyIdentifier != "section.page-size"
            || context.Operation.Expected.Type != "twips-pair"
            || !TryPair(context.Operation.Expected.Value, out var wantedWidth, out var wantedHeight))
            throw new FixExecutionException("fix-operation-contract-invalid");
        var parsed = SectionFixSnapshot.Section(context);
        if (parsed.EffectiveFormatting?.PageWidthTwips.Value == wantedWidth
            && parsed.EffectiveFormatting.PageHeightTwips.Value == wantedHeight)
            return Task.FromResult(FixApplyOutcome.NoChange);
        using var actual = JsonDocument.Parse(context.Finding.ActualJson);
        if (!FormattingFixSnapshot.ActualMatches(actual.RootElement,
                $"{SectionFixSnapshot.Twips(parsed.EffectiveFormatting?.PageWidthTwips.Value)}x{SectionFixSnapshot.Twips(parsed.EffectiveFormatting?.PageHeightTwips.Value)}"))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        var outcome = FormattingFixSnapshot.Mutate(context, body =>
        {
            var section = SectionFixSnapshot.XmlSection(context, body);
            var size = section.GetFirstChild<PageSize>();
            if (size?.Width?.Value == wantedWidth && size.Height?.Value == wantedHeight)
                return FixApplyOutcome.NoChange;
            size ??= section.PrependChild(new PageSize());
            size.Width = checked((uint)wantedWidth);
            size.Height = checked((uint)wantedHeight);
            return FixApplyOutcome.Changed;
        });
        return Task.FromResult(outcome);
    }

    private static bool TryPair(string value, out long width, out long height)
    {
        width = height = 0;
        var parts = value.Split('x', StringSplitOptions.None);
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width > 0 && height > 0;
    }
}

public sealed class SectionMarginFixProvider : IFixPreviewProvider, IFixApplyProvider
{
    public const string Id = "section-margin-direct";
    public const string Version = "1.0";
    private static readonly long FourCm = OpenXmlLayoutUnitConverter.ToTwips(4m, "cm");
    private static readonly long ThreeCm = OpenXmlLayoutUnitConverter.ToTwips(3m, "cm");
    private static readonly IReadOnlyDictionary<string, Contract> Contracts =
        new Dictionary<string, Contract>(StringComparer.Ordinal)
        {
            ["section.margin-left-4cm"] = new("PPKI-LAY-008", "marginLeft", "section.margin-left", FourCm),
            ["section.margin-right-3cm"] = new("PPKI-LAY-009", "marginRight", "section.margin-right", ThreeCm),
            ["section.margin-top-3cm"] = new("PPKI-LAY-010", "marginTop", "section.margin-top", ThreeCm),
            ["section.margin-bottom-3cm"] = new("PPKI-LAY-011", "marginBottom", "section.margin-bottom", ThreeCm)
        };

    public string CapabilityId => Id;
    public string CapabilityVersion => Version;
    public IReadOnlySet<string> ValidationKeys { get; } = Contracts.Keys.ToHashSet(StringComparer.Ordinal);

    public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation,
        out string diagnosticCode)
    {
        operation = null!;
        diagnosticCode = "fix-preview-provider-rejected-snapshot";
        if (!Contracts.TryGetValue(finding.ValidationKey, out var contract)
            || !FormattingFixSnapshot.Common(finding, finding.ValidationKey, contract.RuleCode)
            || !FormattingFixSnapshot.TryRead(finding, out var actual, out var expected, out var location)) return false;
        using (actual) using (expected) using (location)
        {
            var wanted = contract.ExpectedTwips.ToString(CultureInfo.InvariantCulture);
            if (FormattingFixSnapshot.Text(actual.RootElement, "property") != contract.ActualProperty
                || !SectionFixSnapshot.ExactExpected(expected.RootElement, contract.ActualProperty,
                    finding.ValidationKey, wanted)
                || !SectionFixSnapshot.Location(location.RootElement, out var body, out var section)) return false;
            operation = new(new("main-document-section", body, section, null, null), contract.OperationProperty,
                new("twips", wanted), "source-finding-snapshot-must-match", "set-section-margin");
            diagnosticCode = "fix-operation-planned";
            return true;
        }
    }

    public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Contracts.TryGetValue(context.Finding.ValidationKey, out var contract)
            || !TryCreate(context.Finding, out var approved, out _))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        FormattingFixSnapshot.ExactContract(context, this, approved, context.Finding.ValidationKey, contract.RuleCode);
        var parsed = SectionFixSnapshot.Section(context);
        using var actual = JsonDocument.Parse(context.Finding.ActualJson);
        var current = contract.ActualProperty switch
        {
            "marginLeft" => parsed.EffectiveFormatting?.MarginLeftTwips.Value,
            "marginRight" => parsed.EffectiveFormatting?.MarginRightTwips.Value,
            "marginTop" => parsed.EffectiveFormatting?.MarginTopTwips.Value,
            _ => parsed.EffectiveFormatting?.MarginBottomTwips.Value
        };
        if (current == contract.ExpectedTwips)
            return Task.FromResult(FixApplyOutcome.NoChange);
        if (!FormattingFixSnapshot.ActualMatches(actual.RootElement, SectionFixSnapshot.Twips(current)))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        var outcome = FormattingFixSnapshot.Mutate(context, body =>
        {
            var section = SectionFixSnapshot.XmlSection(context, body);
            var margin = section.GetFirstChild<PageMargin>();
            margin ??= section.AppendChild(new PageMargin());
            var wanted = contract.ExpectedTwips;
            var already = contract.OperationProperty switch
            {
                "section.margin-left" => margin.Left?.Value == wanted,
                "section.margin-right" => margin.Right?.Value == wanted,
                "section.margin-top" => margin.Top?.Value == wanted,
                _ => margin.Bottom?.Value == wanted
            };
            if (already) return FixApplyOutcome.NoChange;
            if (contract.OperationProperty == "section.margin-left") margin.Left = checked((uint)wanted);
            else if (contract.OperationProperty == "section.margin-right") margin.Right = checked((uint)wanted);
            else if (contract.OperationProperty == "section.margin-top") margin.Top = checked((int)wanted);
            else margin.Bottom = checked((int)wanted);
            return FixApplyOutcome.Changed;
        });
        return Task.FromResult(outcome);
    }

    private sealed record Contract(string RuleCode, string ActualProperty, string OperationProperty, long ExpectedTwips);
}
