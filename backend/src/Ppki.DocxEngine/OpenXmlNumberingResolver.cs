using System.Globalization;
using System.Text;

namespace Ppki.DocxEngine;

public sealed record NumberingResolutionResult(
    IReadOnlyList<ParsedParagraph> Paragraphs,
    IReadOnlyList<ParserDiagnostic> Diagnostics);

public sealed class OpenXmlNumberingResolver
{
    private readonly IReadOnlyDictionary<int, ParsedAbstractNumbering> _abstracts;
    private readonly IReadOnlyDictionary<int, ParsedNumberingInstance> _instances;
    private readonly IReadOnlyDictionary<(int AbstractId, int Level), ParsedNumberingLevel> _levels;
    private readonly IReadOnlyDictionary<(int NumberingId, int Level), ParsedNumberingLevelOverride> _overrides;
    private readonly List<ParserDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<int, CounterState> _counters = [];

    public OpenXmlNumberingResolver(ParsedNumberingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _abstracts = catalog.AbstractDefinitions.ToDictionary(value => value.AbstractNumberingId);
        _instances = catalog.Instances.ToDictionary(value => value.NumberingId);
        _levels = catalog.AbstractDefinitions
            .SelectMany(abstractNumbering => abstractNumbering.Levels.Select(level =>
                (Key: (abstractNumbering.AbstractNumberingId, level.Level), Value: level)))
            .GroupBy(value => value.Key)
            .ToDictionary(group => group.Key, group => group.First().Value);
        _overrides = catalog.Instances
            .SelectMany(instance => instance.LevelOverrides.Select(level =>
                (Key: (instance.NumberingId, level.Level), Value: level)))
            .GroupBy(value => value.Key)
            .ToDictionary(group => group.Key, group => group.First().Value);
    }

    public NumberingResolutionResult Resolve(IReadOnlyList<ParsedParagraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var resolved = new List<ParsedParagraph>(paragraphs.Count);
        foreach (var paragraph in paragraphs.OrderBy(value => value.Index))
        {
            resolved.Add(paragraph with { EffectiveNumbering = ResolveParagraph(paragraph) });
        }
        return new(resolved.ToArray(), _diagnostics.ToArray());
    }

    private EffectiveParagraphNumbering ResolveParagraph(ParsedParagraph paragraph)
    {
        var formatting = paragraph.EffectiveFormatting;
        if (formatting is null || formatting.NumberingId.State == FormattingResolutionState.Unspecified)
        {
            return Unspecified();
        }
        var numberId = formatting.NumberingId.Value;
        var level = formatting.NumberingLevel.Value;
        var provenance = NumberingProvenance(formatting);
        if (numberId is null) return Unspecified();
        if (numberId == 0)
        {
            return new(NumberingResolutionState.Disabled, 0, level, null, provenance,
                IsExplicitlyDisabled: true);
        }
        if (level is null)
        {
            const string code = "numbering-level-missing";
            AddDiagnostic(code, "parser.numbering_level_missing", paragraph.Location,
                ("numberingId", numberId.Value));
            return Unresolved(numberId, null, null, provenance, code);
        }
        if (level is < 0 or > 8)
        {
            const string code = "numbering-level-invalid";
            AddDiagnostic(code, "parser.numbering_level_invalid", paragraph.Location,
                ("numberingId", numberId.Value), ("level", level.Value));
            return Unresolved(numberId, level, null, provenance, code);
        }
        if (!_instances.TryGetValue(numberId.Value, out var instance))
        {
            const string code = "numbering-instance-missing";
            AddDiagnostic(code, "parser.numbering_instance_missing", paragraph.Location,
                ("numberingId", numberId.Value));
            return Unresolved(numberId, level, null, provenance, code);
        }
        if (instance.AbstractNumberingId is not int abstractId || !_abstracts.TryGetValue(abstractId, out var abstractNumbering))
        {
            const string code = "abstract-numbering-missing";
            AddDiagnostic(code, "parser.abstract_numbering_missing", paragraph.Location,
                ("numberingId", numberId.Value));
            return Unresolved(numberId, level, instance.AbstractNumberingId, provenance, code);
        }

        var levelDefinition = EffectiveLevel(instance, abstractNumbering, level.Value);
        if (levelDefinition is null)
        {
            const string code = "numbering-level-missing";
            AddDiagnostic(code, "parser.numbering_level_missing", paragraph.Location,
                ("numberingId", numberId.Value), ("level", level.Value));
            return Unresolved(numberId, level, abstractId, provenance, code);
        }

        var label = ResolveLabel(instance, abstractNumbering, levelDefinition, paragraph.Location);
        return new(label.State == NumberingResolutionState.Resolved
                ? NumberingResolutionState.Resolved
                : NumberingResolutionState.Unresolved,
            numberId, level, abstractId,
            provenance with { DiagnosticCode = label.DiagnosticCode },
            label,
            DiagnosticCode: label.DiagnosticCode);
    }

    private ResolvedNumberingLabel ResolveLabel(
        ParsedNumberingInstance instance,
        ParsedAbstractNumbering abstractNumbering,
        ParsedNumberingLevel level,
        DocumentElementLocation? location)
    {
        var counters = _counters.TryGetValue(instance.NumberingId, out var existing)
            ? existing
            : _counters[instance.NumberingId] = new();
        for (var deeper = level.Level + 1; deeper < counters.Values.Length; deeper++)
        {
            var deeperDefinition = EffectiveLevel(instance, abstractNumbering, deeper);
            var restart = deeperDefinition?.RestartAfterLevel;
            if (restart == 0 || restart is > 0 && level.Level != restart.Value - 1) continue;
            counters.Started[deeper] = false;
            counters.Values[deeper] = 0;
        }

        var startOverride = _overrides.TryGetValue((instance.NumberingId, level.Level), out var currentOverride)
            ? currentOverride.StartOverride
            : null;
        var start = startOverride ?? level.StartValue ?? 1;
        if (!counters.Started[level.Level])
        {
            counters.Values[level.Level] = start;
            counters.Started[level.Level] = true;
        }
        else
        {
            counters.Values[level.Level]++;
        }

        if (level.Format == ParsedNumberingFormat.Unsupported)
        {
            const string code = "numbering-format-unsupported";
            AddDiagnostic(code, "parser.numbering_format_unsupported", location,
                ("numberingId", instance.NumberingId), ("level", level.Level));
            return new(NumberingResolutionState.Unresolved, null, null, level.Format, level.Suffix, code);
        }

        var levelText = level.LevelText;
        if (levelText is null)
        {
            const string code = "numbering-level-text-invalid";
            AddDiagnostic(code, "parser.numbering_level_text_invalid", location,
                ("numberingId", instance.NumberingId), ("level", level.Level));
            return new(NumberingResolutionState.Unresolved, null, null, level.Format, level.Suffix, code);
        }

        var rendered = RenderLevelText(levelText, level, abstractNumbering, counters);
        if (rendered is null)
        {
            const string code = "numbering-level-text-invalid";
            AddDiagnostic(code, "parser.numbering_level_text_invalid", location,
                ("numberingId", instance.NumberingId), ("level", level.Level));
            return new(NumberingResolutionState.Unresolved, null, null, level.Format, level.Suffix, code);
        }
        var suffix = level.Suffix switch
        {
            ParsedNumberingSuffix.Tab => "\t",
            ParsedNumberingSuffix.Space => " ",
            _ => string.Empty
        };
        return new(NumberingResolutionState.Resolved, rendered, rendered + suffix, level.Format, level.Suffix);
    }

    private string? RenderLevelText(
        string levelText,
        ParsedNumberingLevel currentLevel,
        ParsedAbstractNumbering abstractNumbering,
        CounterState counters)
    {
        if (currentLevel.Format == ParsedNumberingFormat.None) return string.Empty;
        if (currentLevel.Format == ParsedNumberingFormat.Bullet) return levelText;

        var builder = new StringBuilder(levelText.Length + 8);
        for (var index = 0; index < levelText.Length; index++)
        {
            var character = levelText[index];
            if (character != '%')
            {
                builder.Append(character);
                continue;
            }
            if (index + 1 >= levelText.Length || levelText[index + 1] is < '1' or > '9') return null;
            var referencedLevel = levelText[++index] - '1';
            if (!counters.Started[referencedLevel]) return null;
            var definition = _levels.TryGetValue((abstractNumbering.AbstractNumberingId, referencedLevel), out var referenced)
                ? referenced
                : null;
            if (definition is null) return null;
            var format = currentLevel.IsLegalNumbering == true ? ParsedNumberingFormat.Decimal : definition.Format;
            var formatted = FormatCounter(counters.Values[referencedLevel], format);
            if (formatted is null) return null;
            builder.Append(formatted);
        }
        return builder.ToString();
    }

    private static string? FormatCounter(int value, ParsedNumberingFormat format) => format switch
    {
        ParsedNumberingFormat.Decimal => value.ToString(CultureInfo.InvariantCulture),
        ParsedNumberingFormat.UpperRoman => Roman(value),
        ParsedNumberingFormat.LowerRoman => Roman(value)?.ToLowerInvariant(),
        ParsedNumberingFormat.UpperLetter => Letters(value),
        ParsedNumberingFormat.LowerLetter => Letters(value)?.ToLowerInvariant(),
        ParsedNumberingFormat.None => string.Empty,
        _ => null
    };

    private static string? Roman(int value)
    {
        if (value is <= 0 or > 3999) return null;
        var values = new (int Value, string Symbol)[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };
        var builder = new StringBuilder();
        foreach (var item in values)
        {
            while (value >= item.Value)
            {
                builder.Append(item.Symbol);
                value -= item.Value;
            }
        }
        return builder.ToString();
    }

    private static string? Letters(int value)
    {
        if (value <= 0) return null;
        var builder = new StringBuilder();
        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }
        return builder.ToString();
    }

    private ParsedNumberingLevel? EffectiveLevel(
        ParsedNumberingInstance instance,
        ParsedAbstractNumbering abstractNumbering,
        int level)
    {
        return _overrides.TryGetValue((instance.NumberingId, level), out var levelOverride)
            && levelOverride.LevelDefinition is not null
                ? levelOverride.LevelDefinition
                : _levels.TryGetValue((abstractNumbering.AbstractNumberingId, level), out var definition)
                    ? definition
                    : null;
    }

    private static NumberingProvenance NumberingProvenance(EffectiveParagraphFormatting formatting)
    {
        var source = formatting.NumberingId.Provenance;
        return new(source.SourceKind, "numberingId/numberingLevel", source.SourceStyleId,
            source.Inherited, source.DiagnosticCode ?? formatting.NumberingLevel.Provenance.DiagnosticCode);
    }

    private static EffectiveParagraphNumbering Unspecified() => new(
        NumberingResolutionState.Unspecified, null, null, null,
        new(FormattingSourceKind.Unspecified, "numberingId/numberingLevel", null, false));

    private static EffectiveParagraphNumbering Unresolved(
        int? numberId,
        int? level,
        int? abstractId,
        NumberingProvenance provenance,
        string code) => new(
        NumberingResolutionState.Unresolved, numberId, level, abstractId,
        provenance with { DiagnosticCode = code }, DiagnosticCode: code);

    private void AddDiagnostic(
        string code,
        string messageKey,
        DocumentElementLocation? location,
        params (string Name, int Value)[] metadata)
    {
        var key = $"{code}:{location?.ToCompactString()}:{string.Join(':', metadata.Select(value => value.Value))}";
        if (!_diagnosticKeys.Add(key)) return;
        _diagnostics.Add(new(code, ParserDiagnosticSeverity.Warning, messageKey, location,
            metadata.OrderBy(value => value.Name, StringComparer.Ordinal)
                .Select(value => new ParserDiagnosticMetadata(value.Name, value.Value.ToString(CultureInfo.InvariantCulture)))
                .ToArray()));
    }

    private sealed class CounterState
    {
        public int[] Values { get; } = new int[9];
        public bool[] Started { get; } = new bool[9];
    }
}
