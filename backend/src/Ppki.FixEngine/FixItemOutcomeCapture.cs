using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed record FixItemResultDraft(
    Guid Id,
    Guid FixPlanItemId,
    int OperationOrdinal,
    FixItemOutcome Outcome,
    string ValidationKey,
    string FixKey,
    string FixerVersion,
    string PropertyIdentifier,
    string StructuralAnchorJson,
    string? BeforePayloadJson,
    string? AfterPayloadJson,
    string? SafeFailureCode);

public static partial class FixItemOutcomeCapture
{
    public const string AnchorSchemaVersion = "fix-structural-anchor/1.0";
    public const string ValueSchemaVersion = "fix-item-value/1.0";
    public const int MaximumAnchorBytes = 512;
    public const int MaximumPayloadBytes = 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<FixItemResultDraft> Successful(Guid executionId, int attempt,
        ApprovedFixPlanSnapshot approved, ParsedDocument before, ParsedDocument after,
        Guid? resultDocumentVersionId)
    {
        return approved.Items.OrderBy(value => value.Operation.Ordinal).ThenBy(value => value.ItemId)
            .Select(item =>
            {
                var beforePayload = Value(item.Operation, before);
                var afterPayload = Value(item.Operation, after);
                var outcome = string.Equals(beforePayload, afterPayload, StringComparison.Ordinal)
                    ? FixItemOutcome.Skipped : FixItemOutcome.Applied;
                if (outcome == FixItemOutcome.Applied && resultDocumentVersionId is null)
                    throw new FixExecutionException("fix-item-result-publication-required");
                return Draft(executionId, attempt, item, outcome, beforePayload, afterPayload, null);
            }).ToArray();
    }

    public static IReadOnlyList<FixItemResultDraft> Failed(Guid executionId, int attempt,
        ApprovedFixPlanSnapshot approved, string safeFailureCode) =>
        approved.Items.OrderBy(value => value.Operation.Ordinal).ThenBy(value => value.ItemId)
            .Select(item => Draft(executionId, attempt, item, FixItemOutcome.Failed,
                null, null, safeFailureCode)).ToArray();

    private static FixItemResultDraft Draft(Guid executionId, int attempt,
        ApprovedFixPlanItemSnapshot item, FixItemOutcome outcome, string? before, string? after, string? failure) =>
        new(DeterministicId(executionId, attempt, item.ItemId), item.ItemId, item.Operation.Ordinal,
            outcome, item.ValidationKey, item.CapabilityId, item.CapabilityVersion,
            item.Operation.PropertyIdentifier, Anchor(item.Operation.Target), before, after, failure);

    public static string Anchor(FixTargetLocation target) => JsonSerializer.Serialize(new AnchorPayload(
        AnchorSchemaVersion, target.Scope, target.BodyElementIndex, target.SectionIndex,
        target.ParagraphIndex, target.RunIndex), Json);

    public static string Value(FixPlanOperation operation, ParsedDocument document)
    {
        string type;
        string value;
        if (operation.Target.Scope == "main-document-section")
        {
            var section = document.Sections.SingleOrDefault(item => item.Index == operation.Target.SectionIndex
                && item.Location?.PartKind == DocumentPartKind.MainDocument
                && item.Location.BodyElementIndex == operation.Target.BodyElementIndex)
                ?? throw new FixExecutionException("fix-operation-postcondition-failed");
            var format = section.EffectiveFormatting;
            (type, value) = operation.PropertyIdentifier switch
            {
                "section.page-size" => ("twips-pair", $"{Number(format?.PageWidthTwips.Value)}x{Number(format?.PageHeightTwips.Value)}"),
                "section.margin-left" => ("twips", Number(format?.MarginLeftTwips.Value)),
                "section.margin-right" => ("twips", Number(format?.MarginRightTwips.Value)),
                "section.margin-top" => ("twips", Number(format?.MarginTopTwips.Value)),
                "section.margin-bottom" => ("twips", Number(format?.MarginBottomTwips.Value)),
                _ => throw new FixExecutionException("fix-item-result-property-unsupported")
            };
        }
        else
        {
            var paragraph = document.Paragraphs.SingleOrDefault(item =>
                item.Location?.PartKind == DocumentPartKind.MainDocument
                && item.Location.BodyElementIndex == operation.Target.BodyElementIndex
                && item.Location.ParagraphIndex == operation.Target.ParagraphIndex
                && (operation.Target.SectionIndex is null || item.Location.SectionIndex == operation.Target.SectionIndex))
                ?? throw new FixExecutionException("fix-operation-postcondition-failed");
            var visible = VisibleRuns(paragraph);
            var run = operation.Target.RunIndex is null ? null
                : paragraph.RunList.SingleOrDefault(item => item.Index == operation.Target.RunIndex);
            (type, value) = operation.PropertyIdentifier switch
            {
                "paragraph.alignment" or "heading.alignment" => ("enum-code", Alignment(paragraph.DirectAlignment)),
                "heading.runs-bold" => ("boolean-state", Bold(visible)),
                "heading.runs-underline" => ("enum-code", Underline(visible)),
                "paragraph.line-spacing-value" => ("twips", Number(paragraph.DirectLineSpacingValue)),
                "paragraph.line-spacing-rule" => ("enum-code", SafeToken(paragraph.DirectLineSpacingRule)),
                "paragraph.spacing-before" => ("twips", Number(paragraph.DirectSpacingBeforeTwips)),
                "paragraph.spacing-after" => ("twips", Number(paragraph.DirectSpacingAfterTwips)),
                "paragraph.first-line-indent" => ("twips", Number(paragraph.DirectFirstLineIndentTwips)),
                "run.font-family-ascii" => Font(run?.DirectFontAscii),
                "run.font-family-high-ansi" => Font(run?.DirectFontHighAnsi),
                "run.font-size" => ("half-points", Number(run?.DirectFontSizeHalfPoints)),
                _ => throw new FixExecutionException("fix-item-result-property-unsupported")
            };
        }
        return JsonSerializer.Serialize(new ValuePayload(ValueSchemaVersion,
            operation.PropertyIdentifier, type, value), Json);
    }

    private static Guid DeterministicId(Guid executionId, int attempt, Guid itemId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"fix-item-result/1.0\n{executionId:D}\n{attempt}\n{itemId:D}"));
        var bytes = digest[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unset";
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unset";
    private static string Alignment(ParsedAlignment? value) => value switch
    {
        ParsedAlignment.Justified => "justified",
        ParsedAlignment.Center => "center",
        ParsedAlignment.Left => "left",
        ParsedAlignment.Right => "right",
        _ => "unset"
    };
    private static string Bold(IReadOnlyList<ParsedRun> runs)
    {
        if (runs.Count == 0) return "unset";
        var values = runs.Select(value => value.Bold).Distinct().ToArray();
        return values.Length == 1 ? values[0]?.ToString().ToLowerInvariant() ?? "unset" : "mixed";
    }
    private static string Underline(IReadOnlyList<ParsedRun> runs)
    {
        if (runs.Count == 0) return "unset";
        var values = runs.Select(value => string.IsNullOrWhiteSpace(value.Underline)
            ? "none" : SafeToken(value.Underline)).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : "mixed";
    }
    private static string SafeToken(string? value) => value is not null && SafeFormattingToken().IsMatch(value)
        ? value : "unset";
    private static (string Type, string Value) Font(string? value)
    {
        string[] known = ["Times New Roman", "Arial", "Calibri", "Cambria", "Aptos",
            "Courier New", "Georgia", "Tahoma", "Verdana"];
        if (value is null) return ("font-family-token", "unset");
        if (known.Contains(value, StringComparer.OrdinalIgnoreCase))
            return ("font-family-token", value);
        return ("font-family-sha256", Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))));
    }
    private static IReadOnlyList<ParsedRun> VisibleRuns(ParsedParagraph paragraph) => paragraph.RunList
        .Where(value => !value.IsDeleted && !value.IsHidden && value.EffectiveFormatting?.Hidden.Value != true
            && value.TextSegments.Any(segment => !string.IsNullOrEmpty(segment)))
        .OrderBy(value => value.Index).ToArray();

    [GeneratedRegex("^[A-Za-z0-9 ._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFormattingToken();

    private sealed record AnchorPayload(string SchemaVersion, string Scope, int? BodyElementIndex,
        int? SectionIndex, int? ParagraphIndex, int? RunIndex);
    private sealed record ValuePayload(string SchemaVersion, string Property, string ValueType, string Value);
}
