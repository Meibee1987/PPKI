namespace Ppki.DocxEngine;

public sealed record HeadingOutlineResult(
    IReadOnlyList<ParsedHeading> Headings,
    DocumentOutline Outline,
    IReadOnlyList<ParserDiagnostic> Diagnostics);

public sealed class DocumentOutlineBuilder
{
    private static readonly IReadOnlyDictionary<string, int> BuiltInHeadingIds =
        Enumerable.Range(1, 9).ToDictionary(value => $"heading{value}", value => value, StringComparer.Ordinal);

    private readonly IReadOnlyList<ParsedStyleReference> _styles;
    private readonly IReadOnlyDictionary<string, ParsedStyleReference> _styleLookup;
    private readonly IReadOnlyDictionary<int, ParsedNumberingInstance> _numberingInstances;
    private readonly IReadOnlyDictionary<int, ParsedAbstractNumbering> _abstractNumbering;
    private readonly int _maximumNodes;
    private readonly List<ParserDiagnostic> _diagnostics = [];

    public DocumentOutlineBuilder(
        IReadOnlyList<ParsedStyleReference> styles,
        ParsedNumberingCatalog numbering,
        int maximumNodes)
    {
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(numbering);
        if (maximumNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        _styles = styles.OrderBy(value => value.DeclarationOrder).ToArray();
        _styleLookup = _styles.ToDictionary(value => value.StyleId, StringComparer.OrdinalIgnoreCase);
        _numberingInstances = numbering.Instances.ToDictionary(value => value.NumberingId);
        _abstractNumbering = numbering.AbstractDefinitions.ToDictionary(value => value.AbstractNumberingId);
        _maximumNodes = maximumNodes;
    }

    public HeadingOutlineResult Build(IReadOnlyList<ParsedParagraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var headings = DetectHeadings(paragraphs);
        var outline = BuildOutline(headings, paragraphs);
        return new(headings, outline, _diagnostics.ToArray());
    }

    private IReadOnlyList<ParsedHeading> DetectHeadings(IReadOnlyList<ParsedParagraph> paragraphs)
    {
        var result = new List<ParsedHeading>();
        int? previousSection = null;
        foreach (var paragraph in paragraphs.OrderBy(value => value.Index))
        {
            if (paragraph.Location?.PartKind != DocumentPartKind.MainDocument) continue;
            var currentSection = paragraph.Location.SectionIndex;
            var startsNewSection = previousSection is null || currentSection != previousSection;
            previousSection = currentSection;
            if (paragraph.RunList.Count > 0 && paragraph.RunList.All(run => run.IsDeleted || run.IsHidden)) continue;

            var evidence = new List<HeadingEvidence>();
            var levels = new List<(int Level, int Priority)>();
            var directOutline = paragraph.DirectFormatting?.OutlineLevel;
            if (directOutline is not null)
            {
                AddOutlineEvidence(directOutline.Value, 0, HeadingEvidenceKind.DirectOutlineLevel,
                    FormattingSourceKind.DirectFormatting, null, evidence, levels, paragraph.Location);
            }
            else if (paragraph.EffectiveFormatting?.OutlineLevel.Value is int effectiveOutline
                && paragraph.EffectiveFormatting.OutlineLevel.Provenance.SourceKind
                    is FormattingSourceKind.ParagraphStyle or FormattingSourceKind.BasedOnStyle)
            {
                var provenance = paragraph.EffectiveFormatting.OutlineLevel.Provenance;
                var kind = provenance.SourceKind == FormattingSourceKind.BasedOnStyle
                    ? HeadingEvidenceKind.BasedOnHeadingStyle
                    : HeadingEvidenceKind.ParagraphStyleOutlineLevel;
                AddOutlineEvidence(effectiveOutline, 1, kind, provenance.SourceKind,
                    provenance.SourceStyleId, evidence, levels, paragraph.Location);
            }

            var effectiveStyleId = EffectiveStyleId(paragraph);
            if (BuiltInHeadingLevel(effectiveStyleId) is int builtInLevel)
            {
                evidence.Add(new(HeadingEvidenceKind.ExplicitHeadingStyleReference,
                    FormattingSourceKind.ParagraphStyle, "paragraphStyleId", effectiveStyleId));
                evidence.Add(new(HeadingEvidenceKind.BuiltInHeadingStyle,
                    FormattingSourceKind.ParagraphStyle, "builtInHeadingStyle", effectiveStyleId,
                    OutlineLevel: builtInLevel - 1));
                levels.Add((builtInLevel, 2));
            }
            else if (BasedOnBuiltInHeading(effectiveStyleId) is { } basedOn)
            {
                evidence.Add(new(HeadingEvidenceKind.BasedOnHeadingStyle,
                    FormattingSourceKind.BasedOnStyle, "basedOn", basedOn.StyleId,
                    OutlineLevel: basedOn.Level - 1));
                levels.Add((basedOn.Level, 3));
            }

            if (NumberingLinkedHeading(paragraph.EffectiveNumbering) is { } linked)
            {
                evidence.Add(new(HeadingEvidenceKind.NumberingLevelLinkedToHeadingStyle,
                    FormattingSourceKind.ParagraphStyle, "numberingLevelParagraphStyle", linked.StyleId,
                    OutlineLevel: linked.Level - 1,
                    NumberingLevel: paragraph.EffectiveNumbering?.Level));
                levels.Add((linked.Level, 4));
            }

            if (levels.Count == 0) continue;
            var selected = levels.OrderBy(value => value.Priority).First().Level;
            if (levels.Any(value => value.Level != selected))
            {
                _diagnostics.Add(new("heading-evidence-conflict", ParserDiagnosticSeverity.Warning,
                    "parser.heading_evidence_conflict", paragraph.Location));
            }
            if (string.IsNullOrWhiteSpace(paragraph.Text))
            {
                _diagnostics.Add(new("heading-empty", ParserDiagnosticSeverity.Warning,
                    "parser.heading_empty", paragraph.Location));
            }
            result.Add(new(
                result.Count,
                paragraph.Index,
                paragraph.Location!,
                selected,
                HeadingClassification.Confirmed,
                evidence.ToArray(),
                effectiveStyleId,
                selected - 1,
                paragraph.EffectiveNumbering,
                startsNewSection,
                paragraph.Index));
        }
        return result.ToArray();
    }

    private DocumentOutline BuildOutline(
        IReadOnlyList<ParsedHeading> headings,
        IReadOnlyList<ParsedParagraph> paragraphs)
    {
        var paragraphLookup = paragraphs.ToDictionary(value => value.Index);
        var roots = new List<NodeBuilder>();
        var stack = new List<NodeBuilder>();
        int? previousLevel = null;
        var nodeCount = 0;
        foreach (var heading in headings.Where(value => value.Classification == HeadingClassification.Confirmed).OrderBy(value => value.Order))
        {
            if (!paragraphLookup.TryGetValue(heading.ParagraphIndex, out var paragraph) || paragraph.IsInTable) continue;
            if (nodeCount >= _maximumNodes)
            {
                throw new DocxParserException("resource-limit-exceeded", "DOCX parser resource limit exceeded: outline-nodes.");
            }
            if (previousLevel is not null && heading.Level > previousLevel.Value + 1)
            {
                _diagnostics.Add(new("heading-level-skipped", ParserDiagnosticSeverity.Warning,
                    "parser.heading_level_skipped", heading.Location,
                    [new("level", heading.Level.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
            }
            while (stack.Count > 0 && stack[^1].Level >= heading.Level) stack.RemoveAt(stack.Count - 1);
            var parent = stack.Count == 0 ? null : stack[^1];
            var node = new NodeBuilder(
                nodeCount++,
                heading.Index,
                heading.Location,
                heading.Level,
                parent?.Index);
            if (parent is null) roots.Add(node); else parent.Children.Add(node);
            stack.Add(node);
            previousLevel = heading.Level;
        }
        return new(roots.Select(ToImmutable).ToArray(), nodeCount);
    }

    private void AddOutlineEvidence(
        int outlineLevel,
        int priority,
        HeadingEvidenceKind kind,
        FormattingSourceKind sourceKind,
        string? styleId,
        ICollection<HeadingEvidence> evidence,
        ICollection<(int Level, int Priority)> levels,
        DocumentElementLocation? location)
    {
        if (outlineLevel is < 0 or > 8)
        {
            _diagnostics.Add(new("heading-level-invalid", ParserDiagnosticSeverity.Warning,
                "parser.heading_level_invalid", location));
            return;
        }
        evidence.Add(new(kind, sourceKind, "outlineLevel", styleId, outlineLevel));
        levels.Add((outlineLevel + 1, priority));
    }

    private string? EffectiveStyleId(ParsedParagraph paragraph) => paragraph.StyleId
        ?? _styles.FirstOrDefault(value => value.ParsedType == ParsedStyleType.Paragraph && value.IsDefault)?.StyleId;

    private static int? BuiltInHeadingLevel(string? styleId)
    {
        if (styleId is null) return null;
        return BuiltInHeadingIds.TryGetValue(styleId.Replace(" ", string.Empty).ToLowerInvariant(), out var level)
            ? level
            : null;
    }

    private (string StyleId, int Level)? BasedOnBuiltInHeading(string? styleId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = styleId;
        while (current is not null && visited.Add(current) && _styleLookup.TryGetValue(current, out var style))
        {
            current = style.BasedOnStyleId;
            if (BuiltInHeadingLevel(current) is int level) return (current!, level);
        }
        return null;
    }

    private (string StyleId, int Level)? NumberingLinkedHeading(EffectiveParagraphNumbering? numbering)
    {
        if (numbering?.State != NumberingResolutionState.Resolved
            || numbering.NumberingId is not int numberingId
            || numbering.Level is not int level) return null;
        if (!_numberingInstances.TryGetValue(numberingId, out var instance)
            || instance.AbstractNumberingId is not int abstractId
            || !_abstractNumbering.TryGetValue(abstractId, out var abstractNumbering)) return null;
        var levelDefinition = instance?.LevelOverrides.FirstOrDefault(value => value.Level == level)?.LevelDefinition
            ?? abstractNumbering?.Levels.FirstOrDefault(value => value.Level == level);
        var styleId = levelDefinition?.ParagraphStyleId;
        if (BuiltInHeadingLevel(styleId) is int builtIn) return (styleId!, builtIn);
        if (styleId is not null && _styleLookup.TryGetValue(styleId, out var style)
            && style.ParagraphProperties?.OutlineLevel is int outline and >= 0 and <= 8)
        {
            return (styleId, outline + 1);
        }
        return null;
    }

    private static DocumentOutlineNode ToImmutable(NodeBuilder node) => new(
        node.Index, node.HeadingIndex, node.Location, node.Level, node.ParentNodeIndex,
        node.Children.Select(ToImmutable).ToArray());

    private sealed class NodeBuilder(
        int index,
        int headingIndex,
        DocumentElementLocation location,
        int level,
        int? parentNodeIndex)
    {
        public int Index { get; } = index;
        public int HeadingIndex { get; } = headingIndex;
        public DocumentElementLocation Location { get; } = location;
        public int Level { get; } = level;
        public int? ParentNodeIndex { get; } = parentNodeIndex;
        public List<NodeBuilder> Children { get; } = [];
    }
}
