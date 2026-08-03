namespace Ppki.DocxEngine;

public sealed class OpenXmlFormattingResolver
{
    private readonly IReadOnlyList<ParsedStyleReference> _styles;
    private readonly IReadOnlyDictionary<string, ParsedStyleReference> _lookup;
    private readonly ParsedDocumentDefaults _defaults;
    private readonly ParsedThemeFontCatalog _theme;
    private readonly int _maximumDepth;
    private readonly Dictionary<string, StyleChain> _chainCache = new(StringComparer.Ordinal);
    private readonly List<ParserDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);

    public OpenXmlFormattingResolver(
        IReadOnlyList<ParsedStyleReference> styles,
        ParsedDocumentDefaults defaults,
        ParsedThemeFontCatalog theme,
        int maximumStyleInheritanceDepth)
    {
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(theme);
        if (maximumStyleInheritanceDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumStyleInheritanceDepth));

        _styles = styles.OrderBy(style => style.DeclarationOrder).ToArray();
        _lookup = _styles.ToDictionary(style => style.StyleId, StringComparer.OrdinalIgnoreCase);
        _defaults = defaults;
        _theme = theme;
        _maximumDepth = maximumStyleInheritanceDepth;
    }

    public IReadOnlyList<ParserDiagnostic> Diagnostics => _diagnostics.ToArray();

    public EffectiveParagraphFormatting ResolveParagraph(
        ParagraphFormattingProperties direct,
        string? paragraphStyleId,
        DocumentElementLocation location)
    {
        var chain = GetChain(paragraphStyleId, ParsedStyleType.Paragraph, location, useDefault: true);
        return new(
            ResolveParagraphValue(direct.Alignment, chain, value => value.Alignment, _defaults.Paragraph.Alignment, "alignment"),
            ResolveParagraphValue(direct.IndentLeftTwips, chain, value => value.IndentLeftTwips, _defaults.Paragraph.IndentLeftTwips, "indentLeftTwips"),
            ResolveParagraphValue(direct.IndentRightTwips, chain, value => value.IndentRightTwips, _defaults.Paragraph.IndentRightTwips, "indentRightTwips"),
            ResolveParagraphValue(direct.FirstLineIndentTwips, chain, value => value.FirstLineIndentTwips, _defaults.Paragraph.FirstLineIndentTwips, "firstLineIndentTwips"),
            ResolveParagraphValue(direct.HangingIndentTwips, chain, value => value.HangingIndentTwips, _defaults.Paragraph.HangingIndentTwips, "hangingIndentTwips"),
            ResolveParagraphValue(direct.SpacingBeforeTwips, chain, value => value.SpacingBeforeTwips, _defaults.Paragraph.SpacingBeforeTwips, "spacingBeforeTwips"),
            ResolveParagraphValue(direct.SpacingAfterTwips, chain, value => value.SpacingAfterTwips, _defaults.Paragraph.SpacingAfterTwips, "spacingAfterTwips"),
            ResolveParagraphValue(direct.LineSpacingValue, chain, value => value.LineSpacingValue, _defaults.Paragraph.LineSpacingValue, "lineSpacingValue"),
            ResolveParagraphValue(direct.LineSpacingRule, chain, value => value.LineSpacingRule, _defaults.Paragraph.LineSpacingRule, "lineSpacingRule"),
            ResolveParagraphValue(direct.KeepWithNext, chain, value => value.KeepWithNext, _defaults.Paragraph.KeepWithNext, "keepWithNext"),
            ResolveParagraphValue(direct.KeepLinesTogether, chain, value => value.KeepLinesTogether, _defaults.Paragraph.KeepLinesTogether, "keepLinesTogether"),
            ResolveParagraphValue(direct.PageBreakBefore, chain, value => value.PageBreakBefore, _defaults.Paragraph.PageBreakBefore, "pageBreakBefore"),
            ResolveParagraphValue(direct.WidowControl, chain, value => value.WidowControl, _defaults.Paragraph.WidowControl, "widowControl"),
            ResolveParagraphValue(direct.ContextualSpacing, chain, value => value.ContextualSpacing, _defaults.Paragraph.ContextualSpacing, "contextualSpacing"),
            ResolveParagraphValue(direct.OutlineLevel, chain, value => value.OutlineLevel, _defaults.Paragraph.OutlineLevel, "outlineLevel"),
            ResolveParagraphValue(direct.NumberingId, chain, value => value.NumberingId, _defaults.Paragraph.NumberingId, "numberingId"),
            ResolveParagraphValue(direct.NumberingLevel, chain, value => value.NumberingLevel, _defaults.Paragraph.NumberingLevel, "numberingLevel"));
    }

    public EffectiveRunFormatting ResolveRun(
        RunFormattingProperties direct,
        string? paragraphStyleId,
        DocumentElementLocation location)
    {
        var characterChain = GetChain(direct.CharacterStyleId, ParsedStyleType.Character, location, useDefault: false);
        var paragraphChain = GetChain(paragraphStyleId, ParsedStyleType.Paragraph, location, useDefault: true);
        return new(
            ResolveFont(direct.FontAscii, direct.FontAsciiTheme, characterChain, paragraphChain,
                value => value.FontAscii, value => value.FontAsciiTheme, _defaults.Run.FontAscii, _defaults.Run.FontAsciiTheme, "fontAscii"),
            ResolveFont(direct.FontHighAnsi, direct.FontHighAnsiTheme, characterChain, paragraphChain,
                value => value.FontHighAnsi, value => value.FontHighAnsiTheme, _defaults.Run.FontHighAnsi, _defaults.Run.FontHighAnsiTheme, "fontHighAnsi"),
            ResolveFont(direct.FontEastAsia, direct.FontEastAsiaTheme, characterChain, paragraphChain,
                value => value.FontEastAsia, value => value.FontEastAsiaTheme, _defaults.Run.FontEastAsia, _defaults.Run.FontEastAsiaTheme, "fontEastAsia"),
            ResolveFont(direct.FontComplexScript, direct.FontComplexScriptTheme, characterChain, paragraphChain,
                value => value.FontComplexScript, value => value.FontComplexScriptTheme, _defaults.Run.FontComplexScript, _defaults.Run.FontComplexScriptTheme, "fontComplexScript"),
            ResolveRunValue(direct.FontSizeHalfPoints, characterChain, paragraphChain, value => value.FontSizeHalfPoints, _defaults.Run.FontSizeHalfPoints, "fontSizeHalfPoints"),
            ResolveRunValue(direct.ComplexScriptFontSizeHalfPoints, characterChain, paragraphChain, value => value.ComplexScriptFontSizeHalfPoints, _defaults.Run.ComplexScriptFontSizeHalfPoints, "complexScriptFontSizeHalfPoints"),
            ResolveToggle(direct.Bold, characterChain, paragraphChain, value => value.Bold, _defaults.Run.Bold, "bold"),
            ResolveToggle(direct.Italic, characterChain, paragraphChain, value => value.Italic, _defaults.Run.Italic, "italic"),
            ResolveRunValue(direct.Underline, characterChain, paragraphChain, value => value.Underline, _defaults.Run.Underline, "underline"),
            ResolveToggle(direct.Strike, characterChain, paragraphChain, value => value.Strike, _defaults.Run.Strike, "strike"),
            ResolveToggle(direct.Hidden, characterChain, paragraphChain, value => value.Hidden, _defaults.Run.Hidden, "hidden"),
            ResolveToggle(direct.Caps, characterChain, paragraphChain, value => value.Caps, _defaults.Run.Caps, "caps"),
            ResolveToggle(direct.SmallCaps, characterChain, paragraphChain, value => value.SmallCaps, _defaults.Run.SmallCaps, "smallCaps"),
            ResolveRunValue(direct.Color, characterChain, paragraphChain, value => value.Color, _defaults.Run.Color, "color"),
            ResolveRunValue(direct.Language, characterChain, paragraphChain, value => value.Language, _defaults.Run.Language, "language"),
            ResolveRunValue(direct.LanguageEastAsia, characterChain, paragraphChain, value => value.LanguageEastAsia, _defaults.Run.LanguageEastAsia, "languageEastAsia"),
            ResolveRunValue(direct.LanguageComplexScript, characterChain, paragraphChain, value => value.LanguageComplexScript, _defaults.Run.LanguageComplexScript, "languageComplexScript"),
            ResolveRunValue(direct.VerticalAlignment, characterChain, paragraphChain, value => value.VerticalAlignment, _defaults.Run.VerticalAlignment, "verticalAlignment"));
    }

    public static EffectiveSectionFormatting ResolveSection(ParsedSection section) => new(
        SectionValue(section.PageWidthTwips, "pageWidthTwips"),
        SectionValue(section.PageHeightTwips, "pageHeightTwips"),
        SectionValue(section.Orientation, "orientation"),
        SectionValue(section.MarginTopTwips, "marginTopTwips"),
        SectionValue(section.MarginRightTwips, "marginRightTwips"),
        SectionValue(section.MarginBottomTwips, "marginBottomTwips"),
        SectionValue(section.MarginLeftTwips, "marginLeftTwips"),
        SectionValue(section.HeaderDistanceTwips, "headerDistanceTwips"),
        SectionValue(section.FooterDistanceTwips, "footerDistanceTwips"),
        SectionValue(section.GutterTwips, "gutterTwips"),
        SectionValue(section.ColumnCount, "columnCount"),
        SectionValue(section.ColumnSpacingTwips, "columnSpacingTwips"),
        SectionValue(section.SectionType, "sectionType"),
        SectionValue(section.StartPageNumber, "startPageNumber"));

    private ResolvedFormattingValue<T?> ResolveParagraphValue<T>(
        T? direct,
        StyleChain chain,
        Func<ParagraphFormattingProperties, T?> selector,
        T? documentDefault,
        string property) where T : struct
    {
        if (direct is not null) return Resolved(direct, FormattingSourceKind.DirectFormatting, property);
        foreach (var layer in chain.Layers)
        {
            var value = selector(layer.Style.ParagraphProperties ?? new());
            if (value is not null) return Resolved(value, layer.SourceKind, property, layer.Style.StyleId, true, chain.DiagnosticCode);
        }
        if (documentDefault is not null) return Resolved(documentDefault, FormattingSourceKind.DocumentDefault, property, inherited: true, diagnosticCode: chain.DiagnosticCode);
        return Missing<T>(property, chain.DiagnosticCode);
    }

    private ResolvedFormattingValue<string?> ResolveParagraphValue(
        string? direct,
        StyleChain chain,
        Func<ParagraphFormattingProperties, string?> selector,
        string? documentDefault,
        string property)
    {
        if (direct is not null) return Resolved(direct, FormattingSourceKind.DirectFormatting, property);
        foreach (var layer in chain.Layers)
        {
            var value = selector(layer.Style.ParagraphProperties ?? new());
            if (value is not null) return Resolved(value, layer.SourceKind, property, layer.Style.StyleId, true, chain.DiagnosticCode);
        }
        if (documentDefault is not null) return Resolved(documentDefault, FormattingSourceKind.DocumentDefault, property, inherited: true, diagnosticCode: chain.DiagnosticCode);
        return Missing(property, chain.DiagnosticCode);
    }

    private ResolvedFormattingValue<T?> ResolveRunValue<T>(
        T? direct,
        StyleChain characterChain,
        StyleChain paragraphChain,
        Func<RunFormattingProperties, T?> selector,
        T? documentDefault,
        string property) where T : struct
    {
        if (direct is not null) return Resolved(direct, FormattingSourceKind.DirectFormatting, property);
        foreach (var layer in characterChain.Layers.Concat(paragraphChain.Layers))
        {
            var value = selector(layer.Style.RunProperties ?? new());
            if (value is not null) return Resolved(value, layer.SourceKind, property, layer.Style.StyleId, true, FirstDiagnostic(characterChain, paragraphChain));
        }
        if (documentDefault is not null) return Resolved(documentDefault, FormattingSourceKind.DocumentDefault, property, inherited: true, diagnosticCode: FirstDiagnostic(characterChain, paragraphChain));
        return Missing<T>(property, FirstDiagnostic(characterChain, paragraphChain));
    }

    private ResolvedFormattingValue<string?> ResolveRunValue(
        string? direct,
        StyleChain characterChain,
        StyleChain paragraphChain,
        Func<RunFormattingProperties, string?> selector,
        string? documentDefault,
        string property)
    {
        if (direct is not null) return Resolved(direct, FormattingSourceKind.DirectFormatting, property);
        foreach (var layer in characterChain.Layers.Concat(paragraphChain.Layers))
        {
            var value = selector(layer.Style.RunProperties ?? new());
            if (value is not null) return Resolved(value, layer.SourceKind, property, layer.Style.StyleId, true, FirstDiagnostic(characterChain, paragraphChain));
        }
        if (documentDefault is not null) return Resolved(documentDefault, FormattingSourceKind.DocumentDefault, property, inherited: true, diagnosticCode: FirstDiagnostic(characterChain, paragraphChain));
        return Missing(property, FirstDiagnostic(characterChain, paragraphChain));
    }

    private ResolvedFormattingValue<bool?> ResolveToggle(
        bool? direct,
        StyleChain characterChain,
        StyleChain paragraphChain,
        Func<RunFormattingProperties, bool?> selector,
        bool? documentDefault,
        string property)
    {
        if (direct is not null) return Resolved(direct, FormattingSourceKind.DirectFormatting, property);

        bool? value = documentDefault;
        FormattingProvenance? provenance = documentDefault is null
            ? null
            : new(FormattingSourceKind.DocumentDefault, property, Inherited: true);
        foreach (var layer in paragraphChain.Layers.Reverse().Concat(characterChain.Layers.Reverse()))
        {
            var toggle = selector(layer.Style.RunProperties ?? new());
            if (toggle != true) continue;
            value = !(value ?? false);
            provenance = new(layer.SourceKind, property, layer.Style.StyleId, true, FirstDiagnostic(characterChain, paragraphChain));
        }

        if (value is not null)
        {
            return new(value, FormattingResolutionState.Resolved,
                provenance! with { DiagnosticCode = provenance.DiagnosticCode ?? FirstDiagnostic(characterChain, paragraphChain) });
        }
        return Missing<bool>(property, FirstDiagnostic(characterChain, paragraphChain));
    }

    private ResolvedFormattingValue<string?> ResolveFont(
        string? directFont,
        string? directTheme,
        StyleChain characterChain,
        StyleChain paragraphChain,
        Func<RunFormattingProperties, string?> fontSelector,
        Func<RunFormattingProperties, string?> themeSelector,
        string? defaultFont,
        string? defaultTheme,
        string property)
    {
        var candidate = FontCandidate(directFont, directTheme, FormattingSourceKind.DirectFormatting, null, false);
        if (candidate is null)
        {
            foreach (var layer in characterChain.Layers.Concat(paragraphChain.Layers))
            {
                candidate = FontCandidate(fontSelector(layer.Style.RunProperties ?? new()), themeSelector(layer.Style.RunProperties ?? new()),
                    layer.SourceKind, layer.Style.StyleId, true);
                if (candidate is not null) break;
            }
        }
        candidate ??= FontCandidate(defaultFont, defaultTheme, FormattingSourceKind.DocumentDefault, null, true);
        if (candidate is null) return Missing(property, FirstDiagnostic(characterChain, paragraphChain));
        if (candidate.Font is not null)
        {
            return Resolved(candidate.Font, candidate.SourceKind, property, candidate.StyleId, candidate.Inherited,
                FirstDiagnostic(characterChain, paragraphChain));
        }

        var resolved = ThemeFont(candidate.Theme!);
        if (resolved is not null)
        {
            return Resolved(resolved, FormattingSourceKind.Theme, property, candidate.StyleId, true,
                FirstDiagnostic(characterChain, paragraphChain), candidate.Theme);
        }

        const string code = "theme-font-unresolved";
        AddDiagnostic(code, "parser.theme_font_unresolved", null, candidate.Theme);
        return new(null, FormattingResolutionState.Unresolved,
            new(FormattingSourceKind.Theme, candidate.Theme!, candidate.StyleId, true, code));
    }

    private string? ThemeFont(string slot) => slot.ToLowerInvariant() switch
    {
        "majorascii" or "majorhansi" => _theme.MajorLatin,
        "minorascii" or "minorhansi" => _theme.MinorLatin,
        "majoreastasia" => _theme.MajorEastAsia,
        "minoreastasia" => _theme.MinorEastAsia,
        "majorbidi" => _theme.MajorComplexScript,
        "minorbidi" => _theme.MinorComplexScript,
        _ => null
    };

    private StyleChain GetChain(string? requestedStyleId, ParsedStyleType expectedType, DocumentElementLocation location, bool useDefault)
    {
        var styleId = requestedStyleId;
        if (string.IsNullOrWhiteSpace(styleId) && useDefault)
        {
            styleId = _styles.FirstOrDefault(style => style.ParsedType == expectedType && style.IsDefault)?.StyleId;
        }
        if (string.IsNullOrWhiteSpace(styleId)) return StyleChain.Empty;

        var cacheKey = $"{expectedType}:{styleId}";
        if (_chainCache.TryGetValue(cacheKey, out var cached)) return cached;

        var layers = new List<StyleLayer>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? diagnosticCode = null;
        var currentId = styleId;
        while (!string.IsNullOrWhiteSpace(currentId))
        {
            if (layers.Count >= _maximumDepth)
            {
                diagnosticCode = "style-inheritance-depth-exceeded";
                AddDiagnostic(diagnosticCode, "parser.style_inheritance_depth_exceeded", location, currentId);
                break;
            }
            if (!visited.Add(currentId))
            {
                diagnosticCode = "style-inheritance-cycle";
                AddDiagnostic(diagnosticCode, "parser.style_inheritance_cycle", location, currentId);
                break;
            }
            if (!_lookup.TryGetValue(currentId, out var style))
            {
                diagnosticCode = "style-based-on-missing";
                AddDiagnostic(diagnosticCode, "parser.style_based_on_missing", location, currentId);
                break;
            }
            if (style.ParsedType != expectedType)
            {
                diagnosticCode = "style-type-mismatch";
                AddDiagnostic(diagnosticCode, "parser.style_type_mismatch", location, currentId);
                break;
            }
            layers.Add(new(style, layers.Count == 0
                ? expectedType == ParsedStyleType.Character ? FormattingSourceKind.CharacterStyle : FormattingSourceKind.ParagraphStyle
                : FormattingSourceKind.BasedOnStyle));
            currentId = style.BasedOnStyleId;
        }

        var result = new StyleChain(layers.ToArray(), diagnosticCode);
        _chainCache[cacheKey] = result;
        return result;
    }

    private void AddDiagnostic(string code, string messageKey, DocumentElementLocation? location, string? styleId)
    {
        var normalized = NormalizeStyleId(styleId);
        var key = $"{code}:{normalized}";
        if (!_diagnosticKeys.Add(key)) return;
        _diagnostics.Add(new(code, ParserDiagnosticSeverity.Warning, messageKey, location,
            normalized is null ? null : [new("styleId", normalized)]));
    }

    private static string? NormalizeStyleId(string? styleId)
    {
        if (string.IsNullOrWhiteSpace(styleId)) return null;
        return new string(styleId.Trim().Take(128)
            .Select(value => char.IsLetterOrDigit(value) || value is '_' or '-' or '.' ? value : '_').ToArray());
    }

    private static ResolvedFormattingValue<T?> SectionValue<T>(T? value, string property) where T : struct => value is null
        ? Missing<T>(property)
        : Resolved(value, FormattingSourceKind.SectionProperties, property);

    private static ResolvedFormattingValue<string?> SectionValue(string? value, string property) => value is null
        ? Missing(property)
        : Resolved(value, FormattingSourceKind.SectionProperties, property);

    private static ResolvedFormattingValue<T?> Missing<T>(string property, string? diagnosticCode = null) where T : struct => new(
        null,
        ResolutionState(diagnosticCode),
        new(diagnosticCode is null ? FormattingSourceKind.Unspecified : FormattingSourceKind.Invalid,
            property, Inherited: false, DiagnosticCode: diagnosticCode));

    private static ResolvedFormattingValue<string?> Missing(string property, string? diagnosticCode = null) => new(
        null,
        ResolutionState(diagnosticCode),
        new(diagnosticCode is null ? FormattingSourceKind.Unspecified : FormattingSourceKind.Invalid,
            property, Inherited: false, DiagnosticCode: diagnosticCode));

    private static ResolvedFormattingValue<T?> Resolved<T>(T? value, FormattingSourceKind source, string property,
        string? styleId = null, bool inherited = false, string? diagnosticCode = null) where T : struct =>
        new(value, FormattingResolutionState.Resolved, new(source, property, styleId, inherited, diagnosticCode));

    private static ResolvedFormattingValue<string?> Resolved(string? value, FormattingSourceKind source, string property,
        string? styleId = null, bool inherited = false, string? diagnosticCode = null, string? sourceProperty = null) =>
        new(value, FormattingResolutionState.Resolved, new(source, sourceProperty ?? property, styleId, inherited, diagnosticCode));

    private static FontValue? FontCandidate(string? font, string? theme, FormattingSourceKind sourceKind, string? styleId, bool inherited) =>
        font is null && theme is null ? null : new(font, theme, sourceKind, styleId, inherited);

    private static string? FirstDiagnostic(StyleChain first, StyleChain second) => first.DiagnosticCode ?? second.DiagnosticCode;

    private static FormattingResolutionState ResolutionState(string? diagnosticCode) => diagnosticCode switch
    {
        null => FormattingResolutionState.Unspecified,
        "style-type-mismatch" => FormattingResolutionState.Invalid,
        _ => FormattingResolutionState.Unresolved
    };

    private sealed record StyleLayer(ParsedStyleReference Style, FormattingSourceKind SourceKind);
    private sealed record StyleChain(IReadOnlyList<StyleLayer> Layers, string? DiagnosticCode)
    {
        public static StyleChain Empty { get; } = new([], null);
    }
    private sealed record FontValue(string? Font, string? Theme, FormattingSourceKind SourceKind, string? StyleId, bool Inherited);
}
