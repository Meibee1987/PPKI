using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed record ExactTextReplacementOperation(
    Guid DecisionId,
    ExactTextAnchor Anchor,
    ValidatedCorrectionReplacement Replacement);

public sealed record ExactTextReplacementResult(
    int ChangedCount,
    IReadOnlyDictionary<int, string> ExpectedParagraphText);

/// <summary>
/// Applies pre-resolved exact anchors. Detection/search APIs are intentionally absent.
/// Multi-run replacement is accepted only when run formatting and hyperlink containers match.
/// </summary>
public sealed class ExactTextReplacementProvider
{
    public const string Id = "text-exact-replacement";
    public const string Version = "1.0";

    public async Task<ExactTextReplacementResult> ApplyAsync(
        string workingFilePath,
        Guid documentVersionId,
        IReadOnlyList<ExactTextReplacementOperation> operations,
        ExactTextAnchorMaterializer materializer,
        CancellationToken cancellationToken)
    {
        if (operations.Count is < 1 or > 100)
            throw Conflict("correction-batch-size-invalid");

        var resolved = new List<ExactTextReplacementOperation>(operations.Count);
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.Anchor.DocumentVersionId != documentVersionId)
                throw Conflict("correction-source-version-conflict");
            var result = await materializer.ResolveAsync(workingFilePath, documentVersionId,
                operation.Anchor, cancellationToken);
            if (result.Status != ExactTextTargetStatus.Exact)
                throw Conflict(result.Status == ExactTextTargetStatus.Stale
                    ? "correction-anchor-stale" : "correction-anchor-unsupported");
            resolved.Add(operation);
        }

        ValidateRanges(resolved);
        var expected = BuildExpectedParagraphs(workingFilePath, resolved);
        using var package = WordprocessingDocument.Open(workingFilePath, true,
            new OpenSettings { AutoSave = false });
        foreach (var operation in resolved.OrderByDescending(value => value.Anchor.ParagraphLocation.ParagraphIndex)
                     .ThenByDescending(value => value.Anchor.Start)
                     .ThenBy(value => value.DecisionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyOne(package, operation);
        }
        package.MainDocumentPart?.Document?.Save();
        return new(resolved.Count, expected);
    }

    private static void ValidateRanges(IReadOnlyList<ExactTextReplacementOperation> operations)
    {
        foreach (var paragraph in operations.GroupBy(value => value.Anchor.ParagraphLocation.ToCompactString(),
                     StringComparer.Ordinal))
        {
            var ordered = paragraph.OrderBy(value => value.Anchor.Start)
                .ThenBy(value => value.Anchor.Length).ThenBy(value => value.DecisionId).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (current.Anchor.Start < previous.Anchor.Start + previous.Anchor.Length)
                    throw Conflict("correction-target-overlap");
            }
        }
    }

    private static IReadOnlyDictionary<int, string> BuildExpectedParagraphs(string path,
        IReadOnlyList<ExactTextReplacementOperation> operations)
    {
        using var package = WordprocessingDocument.Open(path, false, new OpenSettings { AutoSave = false });
        var result = new Dictionary<int, string>();
        foreach (var group in operations.GroupBy(value => value.Anchor.ParagraphLocation.ParagraphIndex
                     ?? throw Conflict("correction-paragraph-location-invalid")))
        {
            var paragraph = LocateParagraph(package, group.Key)
                ?? throw Conflict("correction-paragraph-location-invalid");
            var text = string.Concat(paragraph.Descendants<Run>()
                .SelectMany(run => run.ChildElements.OfType<Text>()).Select(value => value.Text));
            foreach (var operation in group.OrderByDescending(value => value.Anchor.Start))
                text = ScalarSlice(text, 0, operation.Anchor.Start) + operation.Replacement.Value
                    + ScalarSlice(text, operation.Anchor.Start + operation.Anchor.Length,
                        ScalarCount(text) - operation.Anchor.Start - operation.Anchor.Length);
            result[group.Key] = text;
        }
        return result;
    }

    private static void ApplyOne(WordprocessingDocument package, ExactTextReplacementOperation operation)
    {
        var paragraphIndex = operation.Anchor.ParagraphLocation.ParagraphIndex
            ?? throw Conflict("correction-paragraph-location-invalid");
        var paragraph = LocateParagraph(package, paragraphIndex)
            ?? throw Conflict("correction-paragraph-location-invalid");
        var runs = paragraph.Descendants<Run>().ToArray();
        var targets = operation.Anchor.Spans.Select(span =>
        {
            if (span.RunIndex >= runs.Length) throw Conflict("correction-anchor-stale");
            var run = runs[span.RunIndex];
            if (span.NodeIndex >= run.ChildElements.Count || run.ChildElements[span.NodeIndex] is not Text text)
                throw Conflict("correction-anchor-unsupported");
            return new Target(run, text, span);
        }).ToArray();

        var semantics = SemanticKey(targets[0].Run);
        if (targets.Any(target => !string.Equals(SemanticKey(target.Run), semantics, StringComparison.Ordinal)))
            throw Conflict("correction-multirun-semantics-incompatible");
        var currentTarget = string.Concat(targets.Select(target => ScalarSlice(target.Text.Text,
            target.Span.SourceStart, target.Span.SourceLength)));
        if (string.Equals(currentTarget, operation.Replacement.Value, StringComparison.Ordinal))
            throw Conflict("correction-target-no-change");

        for (var index = targets.Length - 1; index >= 0; index--)
        {
            var target = targets[index];
            var scalarLength = ScalarCount(target.Text.Text);
            if (target.Span.SourceStart + target.Span.SourceLength > scalarLength)
                throw Conflict("correction-anchor-stale");
            var prefix = ScalarSlice(target.Text.Text, 0, target.Span.SourceStart);
            var suffixStart = target.Span.SourceStart + target.Span.SourceLength;
            var suffix = ScalarSlice(target.Text.Text, suffixStart, scalarLength - suffixStart);
            target.Text.Text = prefix + (index == 0 ? operation.Replacement.Value : string.Empty) + suffix;
        }
    }

    private static string SemanticKey(Run run)
    {
        var hyperlink = run.Ancestors<Hyperlink>().SingleOrDefault();
        var hyperlinkKey = hyperlink is null ? "none" : hyperlink.Id?.Value ?? "missing-id";
        return hyperlinkKey + "\n" + (run.RunProperties?.OuterXml ?? string.Empty);
    }

    private static Paragraph? LocateParagraph(WordprocessingDocument document, int requestedIndex)
    {
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null || requestedIndex < 0) return null;
        var index = 0;
        foreach (var element in body.Elements())
        {
            if (element is Paragraph paragraph)
            {
                if (index++ == requestedIndex) return paragraph;
            }
            else if (element is Table table)
            {
                foreach (var nested in table.Descendants<Paragraph>())
                    if (index++ == requestedIndex) return nested;
            }
        }
        return null;
    }

    private static int ScalarCount(string value) => value.EnumerateRunes().Count();
    private static string ScalarSlice(string value, int start, int length) => string.Concat(
        value.EnumerateRunes().Skip(start).Take(length).Select(value => value.ToString()));
    private static FixExecutionException Conflict(string code) =>
        new(FixFailureCategory.Conflict, code);
    private sealed record Target(Run Run, Text Text, ExactTextSourceSpan Span);
}
