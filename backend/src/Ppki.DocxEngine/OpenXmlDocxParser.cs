using System.Globalization;
using System.IO.Compression;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Ppki.DocxEngine;

public sealed class OpenXmlDocxParser : IDocxParser
{
    public const string SchemaVersion = "3.0";
    public const string ProjectionVersion = "3.0";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string WordprocessingDrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private readonly DocxParserOptions _options;

    public OpenXmlDocxParser() : this(new DocxParserOptions()) { }

    public OpenXmlDocxParser(DocxParserOptions options)
    {
        options.Validate();
        _options = options;
    }

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) throw new DocxParserException("package-not-found", "DOCX package was not found.");
        if (fileInfo.Length > _options.MaximumInputBytes) throw Limit("input-bytes");

        try
        {
            PreflightPackage(filePath, cancellationToken);
            using var document = WordprocessingDocument.Open(filePath, false, new OpenSettings
            {
                AutoSave = false
            });
            cancellationToken.ThrowIfCancellationRequested();

            var mainPart = document.MainDocumentPart
                ?? throw new DocxParserException("main-part-missing", "DOCX package does not contain a main document part.");
            var body = mainPart.Document?.Body
                ?? throw new DocxParserException("body-missing", "DOCX package does not contain a document body.");

            var context = new ParserContext(_options, cancellationToken);
            var relationshipCounts = CountRelationships(document, context);
            var styles = ParseStyles(mainPart, context);
            var themeFonts = ParseThemeFonts(mainPart);
            var resolver = new OpenXmlFormattingResolver(
                styles.Catalog,
                styles.DocumentDefaults,
                themeFonts,
                _options.MaximumStyleInheritanceDepth);
            var numbering = ParseNumbering(mainPart, context);
            var sections = ParseSections(body, mainPart, context);
            var bodyResult = ParseBody(body, mainPart, styles, numbering, resolver, context);
            var numberingResult = new OpenXmlNumberingResolver(numbering.FullCatalog).Resolve(bodyResult.Paragraphs);
            bodyResult = bodyResult with { Paragraphs = numberingResult.Paragraphs };
            AddDiagnostics(context, numberingResult.Diagnostics);
            var structureResult = new DocumentOutlineBuilder(
                styles.Catalog,
                numbering.FullCatalog,
                _options.MaximumOutlineNodes).Build(bodyResult.Paragraphs);
            AddDiagnostics(context, structureResult.Diagnostics);
            var headerFooters = ParseHeaderFooters(mainPart, sections, styles, numbering, resolver, context);
            foreach (var diagnostic in resolver.Diagnostics)
            {
                context.Diagnostics.Add(diagnostic.Code, diagnostic.Severity, diagnostic.MessageKey,
                    diagnostic.Location, diagnostic.Metadata);
            }

            var counts = new ParsedAggregateCounts(
                sections.Count,
                bodyResult.BodyElements.Count,
                context.ParagraphCount,
                context.RunCount,
                context.TableCount,
                context.Drawings.Count,
                context.Fields.Count,
                headerFooters.Count,
                relationshipCounts.Total,
                relationshipCounts.External,
                context.FootnoteReferenceCount,
                context.EndnoteReferenceCount,
                context.CommentReferenceCount);

            return Task.FromResult(new ParsedDocument(
                Sections: sections,
                Paragraphs: bodyResult.Paragraphs,
                ParserSchemaVersion: SchemaVersion,
                PackageType: PackageType(document.DocumentType),
                BodyElements: bodyResult.BodyElements,
                Tables: context.Tables.ToArray(),
                Drawings: context.Drawings.ToArray(),
                Fields: context.Fields.ToArray(),
                HeaderFooters: headerFooters,
                StyleCatalog: styles.Catalog,
                NumberingCatalog: numbering.Catalog,
                Diagnostics: context.Diagnostics.Items,
                AggregateCounts: counts,
                ProjectionSchemaVersion: ProjectionVersion,
                DocumentDefaults: styles.DocumentDefaults,
                ThemeFonts: themeFonts,
                NumberingDefinitions: numbering.FullCatalog,
                HeadingInventory: structureResult.Headings,
                Outline: structureResult.Outline));
        }
        catch (DocxParserException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or FileFormatException
            or UnauthorizedAccessException
            or OpenXmlPackageException
            or XmlException
            or InvalidDataException)
        {
            throw new DocxParserException("package-invalid", "DOCX package is corrupt or unsupported.");
        }
    }

    private static void AddDiagnostics(ParserContext context, IReadOnlyList<ParserDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            context.Diagnostics.Add(diagnostic.Code, diagnostic.Severity, diagnostic.MessageKey,
                diagnostic.Location, diagnostic.Metadata);
        }
    }

    private void PreflightPackage(string filePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(filePath);
        long expandedBytes = 0;
        var entries = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries++;
            if (entries > _options.MaximumPackageEntries) throw Limit("package-entries");
            try
            {
                expandedBytes = checked(expandedBytes + entry.Length);
            }
            catch (OverflowException)
            {
                throw Limit("expanded-package-bytes");
            }
            if (expandedBytes > _options.MaximumExpandedPackageBytes) throw Limit("expanded-package-bytes");
        }
    }

    private static ParsedPackageType PackageType(WordprocessingDocumentType type) => type switch
    {
        WordprocessingDocumentType.Template => ParsedPackageType.Template,
        WordprocessingDocumentType.MacroEnabledDocument => ParsedPackageType.MacroEnabledDocument,
        WordprocessingDocumentType.MacroEnabledTemplate => ParsedPackageType.MacroEnabledTemplate,
        _ => ParsedPackageType.Document
    };

    private RelationshipCounts CountRelationships(WordprocessingDocument document, ParserContext context)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<OpenXmlPart>();
        foreach (var pair in document.Parts.OrderBy(item => item.OpenXmlPart.Uri.OriginalString, StringComparer.Ordinal))
        {
            queue.Enqueue(pair.OpenXmlPart);
        }

        var total = 0;
        var external = document.ExternalRelationships.Count();
        total += document.Parts.Count() + external;
        while (queue.Count > 0)
        {
            context.CheckCancellation();
            var part = queue.Dequeue();
            if (!visited.Add(part.Uri.OriginalString)) continue;
            var internalParts = part.Parts.OrderBy(item => item.OpenXmlPart.Uri.OriginalString, StringComparer.Ordinal).ToArray();
            var externalParts = part.ExternalRelationships.ToArray();
            total += internalParts.Length + externalParts.Length;
            external += externalParts.Length;
            foreach (var pair in internalParts) queue.Enqueue(pair.OpenXmlPart);
            if (total > _options.MaximumRelationships) throw Limit("relationships");
        }

        if (external > 0)
        {
            context.Diagnostics.Add("external-relationship-ignored", ParserDiagnosticSeverity.Warning, "parser.external_relationship_ignored",
                metadata: [new("count", external.ToString(CultureInfo.InvariantCulture))]);
        }
        return new(total, external);
    }

    private StylesContext ParseStyles(MainDocumentPart mainPart, ParserContext context)
    {
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        var sourceStyles = styles?.Elements<Style>().ToArray() ?? [];
        if (sourceStyles.Length > _options.MaximumStyleCount) throw Limit("styles");

        var catalog = new List<ParsedStyleReference>();
        var lookup = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        for (var declarationOrder = 0; declarationOrder < sourceStyles.Length; declarationOrder++)
        {
            context.CheckCancellation();
            var style = sourceStyles[declarationOrder];
            var styleId = style.StyleId?.Value;
            if (string.IsNullOrWhiteSpace(styleId))
            {
                context.Diagnostics.Add("style-id-missing", ParserDiagnosticSeverity.Warning, "parser.style_id_missing");
                continue;
            }
            if (!lookup.TryAdd(styleId, style))
            {
                context.Diagnostics.Add("style-id-duplicate", ParserDiagnosticSeverity.Warning, "parser.style_id_duplicate",
                    metadata: [new("styleId", NormalizeStyleId(styleId))]);
                continue;
            }

            var rawType = Attribute(style, "type");
            catalog.Add(new ParsedStyleReference(
                styleId,
                style.StyleName?.Val?.Value,
                rawType,
                style.BasedOn?.Val?.Value,
                OnOffAttribute(style, "default") == true,
                OnOffAttribute(style, "customStyle") == true,
                ChildValue(style, "next"),
                ChildValue(style, "link"),
                declarationOrder,
                StyleType(rawType),
                ParseParagraphFormatting(style.StyleParagraphProperties, context, null),
                ParseRunFormatting(style.StyleRunProperties, context, null)));
        }

        var paragraphDefaults = ParseParagraphFormatting(
            styles?.DocDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle,
            context,
            null);
        var runDefaults = ParseRunFormatting(
            styles?.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle,
            context,
            null);
        var documentDefaults = new ParsedDocumentDefaults(paragraphDefaults, runDefaults);
        return new(styles, lookup, catalog.ToArray(), new TextDefaults(
            FirstNonEmpty(runDefaults.FontAscii, runDefaults.FontHighAnsi),
            runDefaults.FontSizeHalfPoints), documentDefaults);
    }

    private static ParsedThemeFontCatalog ParseThemeFonts(MainDocumentPart mainPart)
    {
        var theme = mainPart.ThemePart?.Theme;
        var fontScheme = theme?.Descendants().FirstOrDefault(element => element.LocalName == "fontScheme");
        var major = fontScheme?.ChildElements.FirstOrDefault(element => element.LocalName == "majorFont");
        var minor = fontScheme?.ChildElements.FirstOrDefault(element => element.LocalName == "minorFont");
        return new(
            Typeface(major, "latin"),
            Typeface(minor, "latin"),
            Typeface(major, "ea"),
            Typeface(minor, "ea"),
            Typeface(major, "cs"),
            Typeface(minor, "cs"));
    }

    private static string? Typeface(OpenXmlElement? group, string localName) =>
        Attribute(group?.ChildElements.FirstOrDefault(element => element.LocalName == localName), "typeface");

    private NumberingContext ParseNumbering(MainDocumentPart mainPart, ParserContext context)
    {
        var numbering = mainPart.NumberingDefinitionsPart?.Numbering;
        if (numbering is null)
        {
            context.Diagnostics.Add("numbering-part-missing", ParserDiagnosticSeverity.Information,
                "parser.numbering_part_missing");
            return new([], new([], []));
        }

        var abstractSources = numbering.Elements<AbstractNum>().ToArray();
        if (abstractSources.Length > _options.MaximumAbstractNumberingDefinitions) throw Limit("abstract-numbering-definitions");
        var abstracts = new List<ParsedAbstractNumbering>();
        var abstractIds = new HashSet<int>();
        var totalLevels = 0;
        for (var order = 0; order < abstractSources.Length; order++)
        {
            context.CheckCancellation();
            var source = abstractSources[order];
            var id = NumericInt(source, "abstractNumId", context);
            if (id is null) continue;
            if (!abstractIds.Add(id.Value))
            {
                DuplicateNumberingDiagnostic(context, "abstract", id.Value);
                continue;
            }
            var levels = new List<ParsedNumberingLevel>();
            var levelIds = new HashSet<int>();
            foreach (var level in source.Elements<Level>())
            {
                if (++totalLevels > _options.MaximumNumberingLevels) throw Limit("numbering-levels");
                var parsed = ParseNumberingLevel(level, levels.Count, context);
                if (parsed is null) continue;
                if (!levelIds.Add(parsed.Level))
                {
                    DuplicateNumberingDiagnostic(context, "abstract-level", parsed.Level);
                    continue;
                }
                levels.Add(parsed);
            }
            abstracts.Add(new(
                id.Value,
                ChildValue(source, "multiLevelType"),
                ChildValue(source, "styleLink"),
                ChildValue(source, "numStyleLink"),
                levels.ToArray(),
                order));
        }

        var instanceSources = numbering.Elements<NumberingInstance>().ToArray();
        if (instanceSources.Length > _options.MaximumNumberingInstances) throw Limit("numbering-instances");
        var instances = new List<ParsedNumberingInstance>();
        var instanceIds = new HashSet<int>();
        for (var order = 0; order < instanceSources.Length; order++)
        {
            context.CheckCancellation();
            var source = instanceSources[order];
            var id = NumericInt(source, "numId", context);
            if (id is null) continue;
            if (!instanceIds.Add(id.Value))
            {
                DuplicateNumberingDiagnostic(context, "instance", id.Value);
                continue;
            }
            var overrides = new List<ParsedNumberingLevelOverride>();
            var overrideLevels = new HashSet<int>();
            foreach (var levelOverride in source.Elements<LevelOverride>())
            {
                if (++totalLevels > _options.MaximumNumberingLevels) throw Limit("numbering-levels");
                var level = NumericInt(levelOverride, "ilvl", context);
                if (level is null) continue;
                if (!overrideLevels.Add(level.Value))
                {
                    DuplicateNumberingDiagnostic(context, "level-override", level.Value);
                    continue;
                }
                overrides.Add(new(
                    level.Value,
                    NumericInt(levelOverride.GetFirstChild<StartOverrideNumberingValue>(), "val", context),
                    levelOverride.GetFirstChild<Level>() is { } overrideDefinition
                        ? ParseNumberingLevel(overrideDefinition, overrides.Count, context)
                        : null,
                    overrides.Count));
            }
            instances.Add(new(
                id.Value,
                NumericInt(source.AbstractNumId, "val", context),
                overrides.ToArray(),
                order));
        }

        var fullCatalog = new ParsedNumberingCatalog(abstracts.ToArray(), instances.ToArray());
        var legacy = instances.Select(instance =>
        {
            var abstractNumbering = abstracts.FirstOrDefault(value => value.AbstractNumberingId == instance.AbstractNumberingId);
            var level = abstractNumbering?.Levels.OrderBy(value => value.Level).FirstOrDefault();
            return new ParsedNumberingReference(instance.NumberingId, null, instance.AbstractNumberingId,
                level?.RawFormat, level?.LevelText);
        }).ToArray();
        return new(legacy, fullCatalog);
    }

    private static ParsedNumberingLevel? ParseNumberingLevel(
        Level source,
        int declarationOrder,
        ParserContext context)
    {
        var level = NumericInt(source, "ilvl", context);
        if (level is null) return null;
        var indentation = source.PreviousParagraphProperties?.Indentation;
        var rawFormat = Attribute(source.NumberingFormat, "val");
        return new(
            level.Value,
            NumericInt(source.StartNumberingValue, "val", context),
            NumberingFormat(rawFormat),
            rawFormat,
            source.LevelText?.Val?.Value,
            NumberingSuffix(Attribute(source.GetFirstChild<LevelSuffix>(), "val")),
            Attribute(source.LevelJustification, "val"),
            NumericInt(source.LevelRestart, "val", context),
            OnOff(source.IsLegalNumberingStyle),
            source.ParagraphStyleIdInLevel?.Val?.Value,
            Numeric(indentation, "left", context),
            Numeric(indentation, "hanging", context),
            ParseRunFormatting(source.NumberingSymbolRunProperties, context, null),
            declarationOrder);
    }

    private static ParsedNumberingFormat NumberingFormat(string? value) => value?.ToLowerInvariant() switch
    {
        "decimal" => ParsedNumberingFormat.Decimal,
        "upperroman" => ParsedNumberingFormat.UpperRoman,
        "lowerroman" => ParsedNumberingFormat.LowerRoman,
        "upperletter" => ParsedNumberingFormat.UpperLetter,
        "lowerletter" => ParsedNumberingFormat.LowerLetter,
        "bullet" => ParsedNumberingFormat.Bullet,
        "none" => ParsedNumberingFormat.None,
        _ => ParsedNumberingFormat.Unsupported
    };

    private static ParsedNumberingSuffix NumberingSuffix(string? value) => value?.ToLowerInvariant() switch
    {
        "tab" => ParsedNumberingSuffix.Tab,
        "space" => ParsedNumberingSuffix.Space,
        "nothing" => ParsedNumberingSuffix.Nothing,
        _ => ParsedNumberingSuffix.Unspecified
    };

    private static void DuplicateNumberingDiagnostic(ParserContext context, string kind, int id) =>
        context.Diagnostics.Add("numbering-definition-duplicate", ParserDiagnosticSeverity.Warning,
            "parser.numbering_definition_duplicate", metadata:
            [
                new("id", id.ToString(CultureInfo.InvariantCulture)),
                new("kind", kind)
            ]);

    private static IReadOnlyList<ParsedSection> ParseSections(Body body, MainDocumentPart mainPart, ParserContext context)
    {
        var sections = new List<ParsedSection>();
        var bodyIndex = 0;
        var paragraphIndex = 0;
        foreach (var element in body.Elements())
        {
            context.CheckCancellation();
            if (element is Paragraph paragraph)
            {
                var sectionProperties = paragraph.ParagraphProperties?.SectionProperties;
                if (sectionProperties is not null)
                {
                    sections.Add(ParseSection(sectionProperties, sections.Count, bodyIndex, paragraphIndex, false, mainPart, context));
                }
                paragraphIndex += paragraph.Descendants<Paragraph>().Count() + 1;
            }
            else if (element is Table table)
            {
                paragraphIndex += table.Descendants<Paragraph>().Count();
            }
            bodyIndex++;
        }

        var finalProperties = body.Elements<SectionProperties>().LastOrDefault();
        if (finalProperties is not null)
        {
            var index = Array.FindLastIndex(body.Elements().ToArray(), element => ReferenceEquals(element, finalProperties));
            sections.Add(ParseSection(finalProperties, sections.Count, index < 0 ? body.ChildElements.Count - 1 : index, null, true, mainPart, context));
        }

        if (sections.Count == 0)
        {
            var location = MainLocation(DocumentElementKind.Section, sectionIndex: 0);
            context.Diagnostics.Add("section-properties-missing", ParserDiagnosticSeverity.Information, "parser.section_properties_missing", location);
            var section = new ParsedSection(0, null, null, null, null, null, null, location);
            sections.Add(section with { EffectiveFormatting = OpenXmlFormattingResolver.ResolveSection(section) });
        }
        return sections.ToArray();
    }

    private static ParsedSection ParseSection(
        SectionProperties properties,
        int sectionIndex,
        int bodyElementIndex,
        int? paragraphIndex,
        bool isBodyLevel,
        MainDocumentPart mainPart,
        ParserContext context)
    {
        var location = MainLocation(DocumentElementKind.Section, sectionIndex, bodyElementIndex, paragraphIndex);
        var pageSize = properties.GetFirstChild<PageSize>();
        var pageMargin = properties.GetFirstChild<PageMargin>();
        var columns = properties.GetFirstChild<Columns>();
        var pageNumber = properties.GetFirstChild<PageNumberType>();
        var width = Numeric(pageSize, "w", context, location);
        var height = Numeric(pageSize, "h", context, location);
        var top = Numeric(pageMargin, "top", context, location);
        var right = Numeric(pageMargin, "right", context, location);
        var bottom = Numeric(pageMargin, "bottom", context, location);
        var left = Numeric(pageMargin, "left", context, location);
        var references = properties.ChildElements
            .Where(item => item is HeaderReference or FooterReference)
            .Select(item => HeaderFooterReference(item, mainPart, context, location))
            .Where(item => item is not null)
            .Cast<ParsedHeaderFooterReference>()
            .ToArray();

        var parsed = new ParsedSection(
            sectionIndex,
            TwipsToCm(width),
            TwipsToCm(height),
            TwipsToCm(top),
            TwipsToCm(right),
            TwipsToCm(bottom),
            TwipsToCm(left),
            location,
            width,
            height,
            PageOrientation(Attribute(pageSize, "orient")),
            top,
            right,
            bottom,
            left,
            Numeric(pageMargin, "header", context, location),
            Numeric(pageMargin, "footer", context, location),
            Numeric(pageMargin, "gutter", context, location),
            Attribute(properties.GetFirstChild<SectionType>(), "val"),
            NumericInt(columns, "num", context, location),
            Numeric(columns, "space", context, location),
            NumericInt(pageNumber, "start", context, location),
            references,
            isBodyLevel);
        return parsed with { EffectiveFormatting = OpenXmlFormattingResolver.ResolveSection(parsed) };
    }

    private static ParsedHeaderFooterReference? HeaderFooterReference(
        OpenXmlElement reference,
        MainDocumentPart mainPart,
        ParserContext context,
        DocumentElementLocation location)
    {
        var relationshipId = Attribute(reference, "id", RelationshipsNamespace);
        if (string.IsNullOrWhiteSpace(relationshipId))
        {
            context.Diagnostics.Add("relationship-id-missing", ParserDiagnosticSeverity.Warning, "parser.relationship_id_missing", location);
            return null;
        }
        string? partUri = null;
        try
        {
            partUri = mainPart.GetPartById(relationshipId).Uri.OriginalString;
        }
        catch (ArgumentOutOfRangeException)
        {
            context.Diagnostics.Add("relationship-broken", ParserDiagnosticSeverity.Warning, "parser.relationship_broken", location);
        }
        return new(HeaderFooterType(Attribute(reference, "type")), relationshipId, partUri);
    }

    private static BodyResult ParseBody(
        Body body,
        MainDocumentPart mainPart,
        StylesContext styles,
        NumberingContext numbering,
        OpenXmlFormattingResolver resolver,
        ParserContext context)
    {
        var paragraphs = new List<ParsedParagraph>();
        var bodyElements = new List<ParsedBodyElement>();
        var sectionIndex = 0;
        var bodyIndex = 0;
        foreach (var element in body.Elements())
        {
            context.CheckCancellation();
            switch (element)
            {
                case Paragraph paragraph:
                {
                    var parsed = ParseParagraph(paragraph, mainPart, styles, numbering, resolver, context,
                        MainLocation(DocumentElementKind.Paragraph, sectionIndex, bodyIndex, context.ParagraphCount), false);
                    paragraphs.Add(parsed);
                    bodyElements.Add(new(bodyIndex, ParsedBodyElementKind.Paragraph, parsed.Location!, parsed.Index));
                    if (paragraph.ParagraphProperties?.SectionProperties is not null) sectionIndex++;
                    break;
                }
                case Table table:
                {
                    var parsedTable = ParseTable(table, mainPart, styles, numbering, resolver, context, sectionIndex, bodyIndex, paragraphs);
                    bodyElements.Add(new(bodyIndex, ParsedBodyElementKind.Table, parsedTable.Location, TableIndex: parsedTable.Index));
                    break;
                }
                case SectionProperties:
                    bodyElements.Add(new(bodyIndex, ParsedBodyElementKind.SectionProperties,
                        MainLocation(DocumentElementKind.Section, sectionIndex, bodyIndex), SectionIndex: sectionIndex));
                    break;
                default:
                    bodyElements.Add(new(bodyIndex, ParsedBodyElementKind.Unsupported,
                        MainLocation(DocumentElementKind.Unknown, sectionIndex, bodyIndex)));
                    context.Diagnostics.Add("body-element-unsupported", ParserDiagnosticSeverity.Warning, "parser.body_element_unsupported",
                        MainLocation(DocumentElementKind.Unknown, sectionIndex, bodyIndex), [new("kind", element.LocalName)]);
                    break;
            }
            bodyIndex++;
        }
        return new(paragraphs.ToArray(), bodyElements.ToArray());
    }

    private static ParsedTable ParseTable(
        Table table,
        MainDocumentPart mainPart,
        StylesContext styles,
        NumberingContext numbering,
        OpenXmlFormattingResolver resolver,
        ParserContext context,
        int sectionIndex,
        int bodyIndex,
        ICollection<ParsedParagraph> bodyParagraphs)
    {
        var tableIndex = context.NextTable();
        var location = MainLocation(DocumentElementKind.Table, sectionIndex, bodyIndex, tableIndex: tableIndex);
        var rows = new List<ParsedTableRow>();
        var rowIndex = 0;
        foreach (var row in table.Elements<TableRow>())
        {
            var rowLocation = location with { RowIndex = rowIndex, ElementKind = DocumentElementKind.TableRow };
            var cells = new List<ParsedTableCell>();
            var cellIndex = 0;
            foreach (var cell in row.Elements<TableCell>())
            {
                var cellLocation = rowLocation with { CellIndex = cellIndex, ElementKind = DocumentElementKind.TableCell };
                var paragraphIndexes = new List<int>();
                foreach (var paragraph in cell.Elements<Paragraph>())
                {
                    var paragraphLocation = cellLocation with { ParagraphIndex = context.ParagraphCount, ElementKind = DocumentElementKind.Paragraph };
                    var parsed = ParseParagraph(paragraph, mainPart, styles, numbering, resolver, context, paragraphLocation, true);
                    paragraphIndexes.Add(parsed.Index);
                    bodyParagraphs.Add(parsed);
                }
                if (cell.Descendants<Table>().Any())
                {
                    context.Diagnostics.Add("nested-table-inventory-limited", ParserDiagnosticSeverity.Warning, "parser.nested_table_inventory_limited", cellLocation);
                }
                var width = cell.TableCellProperties?.TableCellWidth;
                cells.Add(new(cellIndex, cellLocation,
                    Numeric(width, "w", context, cellLocation), Attribute(width, "type"), paragraphIndexes.ToArray()));
                cellIndex++;
            }
            rows.Add(new(rowIndex, rowLocation, cells.ToArray()));
            rowIndex++;
        }
        var properties = table.TableProperties;
        var tableWidth = properties?.TableWidth;
        var parsedTable = new ParsedTable(
            tableIndex,
            location,
            properties?.TableStyle?.Val?.Value,
            Numeric(tableWidth, "w", context, location),
            Attribute(tableWidth, "type"),
            table.TableGrid?.Elements<GridColumn>().Select(column => ParseLong(column.Width?.Value)).ToArray() ?? [],
            rows.ToArray());
        context.Tables.Add(parsedTable);
        return parsedTable;
    }

    private static ParsedParagraph ParseParagraph(
        Paragraph paragraph,
        MainDocumentPart mainPart,
        StylesContext styles,
        NumberingContext numbering,
        OpenXmlFormattingResolver resolver,
        ParserContext context,
        DocumentElementLocation location,
        bool isInTable)
    {
        var paragraphIndex = context.NextParagraph();
        location = location with { ParagraphIndex = paragraphIndex };
        var properties = paragraph.ParagraphProperties;
        var styleId = properties?.ParagraphStyleId?.Val?.Value;
        styles.Lookup.TryGetValue(styleId ?? string.Empty, out var style);
        var numberingReference = ParagraphNumbering(properties, numbering);
        var directFormatting = ParseParagraphFormatting(properties, context, location);
        var effectiveFormatting = resolver.ResolveParagraph(directFormatting, styleId, location);
        var fieldStartIndex = context.Fields.Count;
        ParseSimpleFields(paragraph, context, location);
        ParseComplexFields(paragraph, context, location);
        var paragraphFieldIndexes = Enumerable.Range(fieldStartIndex, context.Fields.Count - fieldStartIndex).ToArray();
        var runs = new List<ParsedRun>();
        var runIndex = 0;
        foreach (var run in paragraph.Descendants<Run>())
        {
            var runLocation = location with { RunIndex = runIndex, ElementKind = DocumentElementKind.Run };
            runs.Add(ParseRun(run, mainPart, styles, resolver, styleId, context, runLocation, runIndex, paragraphFieldIndexes));
            runIndex++;
        }

        var directAlignment = Alignment(Attribute(properties?.Justification, "val"));
        var effectiveAlignment = directAlignment ?? Alignment(Attribute(style?.StyleParagraphProperties?.Justification, "val"));
        var directSpacing = properties?.SpacingBetweenLines;
        var effectiveSpacing = directSpacing ?? style?.StyleParagraphProperties?.SpacingBetweenLines;
        var directIndentation = properties?.Indentation;
        var effectiveIndentation = directIndentation ?? style?.StyleParagraphProperties?.Indentation;
        var firstRunProperties = paragraph.Descendants<Run>().Select(run => run.RunProperties).FirstOrDefault(value => value is not null);
        var font = FirstNonEmpty(
            firstRunProperties?.RunFonts?.Ascii?.Value,
            firstRunProperties?.RunFonts?.HighAnsi?.Value,
            style?.StyleRunProperties?.RunFonts?.Ascii?.Value,
            style?.StyleRunProperties?.RunFonts?.HighAnsi?.Value,
            styles.Defaults.FontName);
        var fontSizeHalfPoints = FirstNonNull(
            ParseInt(firstRunProperties?.FontSize?.Val?.Value),
            ParseInt(style?.StyleRunProperties?.FontSize?.Val?.Value),
            styles.Defaults.FontSizeHalfPoints);

        var text = string.Concat(runs.Where(run => !run.IsDeleted).SelectMany(run => run.TextSegments));
        var directLine = Numeric(directSpacing, "line", context, location);
        var directFirstLine = Numeric(directIndentation, "firstLine", context, location);
        var paragraphStyle = styleId is null ? null : new ParsedStyleReference(
            styleId,
            style?.StyleName?.Val?.Value,
            style is null ? null : Attribute(style, "type"),
            style?.BasedOn?.Val?.Value,
            style is not null && OnOffAttribute(style, "default") == true,
            style is not null && OnOffAttribute(style, "customStyle") == true);

        context.FootnoteReferenceCount += paragraph.Descendants<FootnoteReference>().Count();
        context.EndnoteReferenceCount += paragraph.Descendants<EndnoteReference>().Count();
        context.CommentReferenceCount += paragraph.Descendants<CommentReference>().Count();
        var hasTrackedChanges = paragraph.Descendants<InsertedRun>().Any() || paragraph.Descendants<DeletedRun>().Any();
        if (hasTrackedChanges)
        {
            context.Diagnostics.Add("tracked-changes-present", ParserDiagnosticSeverity.Warning, "parser.tracked_changes_present", location);
        }

        return new ParsedParagraph(
            paragraphIndex,
            text,
            styleId,
            styleId?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true,
            isInTable,
            font,
            fontSizeHalfPoints is null ? null : fontSizeHalfPoints.Value / 2m,
            AlignmentName(effectiveAlignment),
            LineSpacingMultiple(effectiveSpacing),
            TwipsToCm(Numeric(effectiveIndentation, "firstLine", context, location)),
            location,
            paragraphStyle,
            numberingReference,
            directAlignment,
            Numeric(directIndentation, "left", context, location),
            Numeric(directIndentation, "right", context, location),
            directFirstLine,
            Numeric(directIndentation, "hanging", context, location),
            Numeric(directSpacing, "before", context, location),
            Numeric(directSpacing, "after", context, location),
            directLine,
            Attribute(directSpacing, "lineRule"),
            OnOff(properties?.KeepNext),
            OnOff(properties?.KeepLines),
            OnOff(properties?.PageBreakBefore),
            NumericInt(properties?.OutlineLevel, "val", context, location),
            runs.ToArray(),
            paragraph.Descendants<TabChar>().Any(),
            paragraph.Descendants<Break>().Any() || paragraph.Descendants<CarriageReturn>().Any(),
            paragraph.Descendants<FieldChar>().Any() || paragraph.Descendants<SimpleField>().Any(),
            paragraph.Descendants<Drawing>().Any(),
            paragraph.Descendants<BookmarkStart>().Any(),
            paragraph.Descendants<Hyperlink>().Any(),
            hasTrackedChanges,
            directFormatting,
            effectiveFormatting);
    }

    private static ParsedRun ParseRun(
        Run run,
        MainDocumentPart mainPart,
        StylesContext styles,
        OpenXmlFormattingResolver resolver,
        string? paragraphStyleId,
        ParserContext context,
        DocumentElementLocation location,
        int runIndex,
        IReadOnlyList<int> paragraphFieldIndexes)
    {
        context.NextRun();
        var segments = new List<string>();
        var breaks = new List<ParsedBreakKind>();
        var drawingIndexes = new List<int>();
        var fieldIndexes = new List<int>();
        var tabCount = 0;
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case Text text:
                    segments.Add(text.Text);
                    break;
                case DeletedText deletedText:
                    segments.Add(deletedText.Text);
                    break;
                case TabChar:
                    tabCount++;
                    segments.Add("\t");
                    break;
                case Break lineBreak:
                    var kind = BreakKind(Attribute(lineBreak, "type"));
                    breaks.Add(kind);
                    segments.Add(kind == ParsedBreakKind.Page ? "\f" : "\n");
                    break;
                case CarriageReturn:
                    breaks.Add(ParsedBreakKind.Line);
                    segments.Add("\n");
                    break;
                case Drawing drawing:
                    drawingIndexes.Add(ParseDrawing(drawing, mainPart, context, location));
                    break;
                case FieldChar:
                case FieldCode:
                    break;
            }
        }

        if ((run.Descendants<FieldChar>().Any() || run.Descendants<FieldCode>().Any() || run.Ancestors<SimpleField>().Any())
            && paragraphFieldIndexes.Count > 0)
        {
            fieldIndexes.AddRange(paragraphFieldIndexes);
        }

        var properties = run.RunProperties;
        var directFormatting = ParseRunFormatting(properties, context, location);
        styles.Lookup.TryGetValue(directFormatting.CharacterStyleId ?? string.Empty, out var characterStyle);
        var characterStyleReference = directFormatting.CharacterStyleId is null ? null : new ParsedStyleReference(
            directFormatting.CharacterStyleId,
            characterStyle?.StyleName?.Val?.Value,
            characterStyle is null ? null : Attribute(characterStyle, "type"),
            characterStyle?.BasedOn?.Val?.Value,
            characterStyle is not null && OnOffAttribute(characterStyle, "default") == true,
            characterStyle is not null && OnOffAttribute(characterStyle, "customStyle") == true);
        var effectiveFormatting = resolver.ResolveRun(directFormatting, paragraphStyleId, location);
        return new ParsedRun(
            runIndex,
            location,
            segments.ToArray(),
            properties?.RunFonts?.Ascii?.Value,
            properties?.RunFonts?.HighAnsi?.Value,
            ParseInt(properties?.FontSize?.Val?.Value),
            OnOff(properties?.Bold),
            OnOff(properties?.Italic),
            Attribute(properties?.Underline, "val"),
            FirstNonEmpty(properties?.Languages?.Val?.Value, properties?.Languages?.EastAsia?.Value),
            Attribute(properties?.VerticalTextAlignment, "val"),
            breaks.ToArray(),
            tabCount,
            fieldIndexes.ToArray(),
            drawingIndexes.ToArray(),
            run.Ancestors<DeletedRun>().Any(),
            run.Ancestors<InsertedRun>().Any(),
            OnOff(properties?.Vanish) == true,
            characterStyleReference,
            directFormatting,
            effectiveFormatting);
    }

    private static int ParseDrawing(Drawing drawing, MainDocumentPart mainPart, ParserContext context, DocumentElementLocation location)
    {
        var index = context.Drawings.Count;
        var drawingLocation = location with { ElementKind = DocumentElementKind.Drawing };
        var blip = drawing.Descendants().FirstOrDefault(element => element.LocalName == "blip" && element.NamespaceUri == DrawingNamespace);
        var relationshipId = Attribute(blip, "embed", RelationshipsNamespace);
        string? contentType = null;
        var external = false;
        if (!string.IsNullOrWhiteSpace(relationshipId))
        {
            try
            {
                contentType = mainPart.GetPartById(relationshipId).ContentType;
            }
            catch (ArgumentOutOfRangeException)
            {
                external = mainPart.ExternalRelationships.Any(item => item.Id == relationshipId);
                context.Diagnostics.Add(external ? "external-drawing-ignored" : "drawing-relationship-broken",
                    ParserDiagnosticSeverity.Warning,
                    external ? "parser.external_drawing_ignored" : "parser.relationship_broken",
                    drawingLocation);
            }
        }
        var extent = drawing.Descendants().FirstOrDefault(element => element.LocalName == "extent" && element.NamespaceUri == WordprocessingDrawingNamespace);
        var kind = drawing.Descendants().Any(element => element.LocalName == "inline" && element.NamespaceUri == WordprocessingDrawingNamespace)
            ? ParsedDrawingKind.Inline
            : drawing.Descendants().Any(element => element.LocalName == "anchor" && element.NamespaceUri == WordprocessingDrawingNamespace)
                ? ParsedDrawingKind.Anchor
                : ParsedDrawingKind.Unknown;
        context.Drawings.Add(new(index, drawingLocation, kind, relationshipId, contentType,
            ParseLong(Attribute(extent, "cx")), ParseLong(Attribute(extent, "cy")), external));
        return index;
    }

    private static void ParseSimpleFields(Paragraph paragraph, ParserContext context, DocumentElementLocation location)
    {
        foreach (var field in paragraph.Descendants<SimpleField>())
        {
            var instruction = field.Instruction?.Value ?? string.Empty;
            context.Fields.Add(new(context.Fields.Count, location with { ElementKind = DocumentElementKind.Field },
                FieldKind(instruction), NormalizeFieldInstruction(instruction), true, true, true));
        }
    }

    private static void ParseComplexFields(Paragraph paragraph, ParserContext context, DocumentElementLocation location)
    {
        var active = false;
        var separated = false;
        var instruction = new List<string>();
        foreach (var element in paragraph.Descendants())
        {
            if (element is FieldChar fieldChar)
            {
                var type = Attribute(fieldChar, "fldCharType");
                if (string.Equals(type, "begin", StringComparison.OrdinalIgnoreCase))
                {
                    active = true;
                    separated = false;
                    instruction.Clear();
                }
                else if (active && string.Equals(type, "separate", StringComparison.OrdinalIgnoreCase))
                {
                    separated = true;
                }
                else if (active && string.Equals(type, "end", StringComparison.OrdinalIgnoreCase))
                {
                    var combined = string.Concat(instruction);
                    context.Fields.Add(new(context.Fields.Count, location with { ElementKind = DocumentElementKind.Field },
                        FieldKind(combined), NormalizeFieldInstruction(combined), true, separated, true));
                    active = false;
                }
            }
            else if (active && element is FieldCode code)
            {
                instruction.Add(code.Text);
            }
        }
        if (active)
        {
            var combined = string.Concat(instruction);
            context.Fields.Add(new(context.Fields.Count, location with { ElementKind = DocumentElementKind.Field },
                FieldKind(combined), NormalizeFieldInstruction(combined), true, separated, false));
            context.Diagnostics.Add("field-unbalanced", ParserDiagnosticSeverity.Warning, "parser.field_unbalanced", location);
        }
    }

    private static IReadOnlyList<ParsedHeaderFooter> ParseHeaderFooters(
        MainDocumentPart mainPart,
        IReadOnlyList<ParsedSection> sections,
        StylesContext styles,
        NumberingContext numbering,
        OpenXmlFormattingResolver resolver,
        ParserContext context)
    {
        var result = new List<ParsedHeaderFooter>();
        var references = sections.SelectMany(section => section.HeaderFooterReferenceList)
            .Where(reference => reference.NormalizedPartUri is not null)
            .GroupBy(reference => reference.NormalizedPartUri!, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(reference => reference.NormalizedPartUri, StringComparer.Ordinal);
        foreach (var reference in references)
        {
            context.CheckCancellation();
            try
            {
                var part = mainPart.GetPartById(reference.RelationshipId);
                OpenXmlCompositeElement? root = part switch
                {
                    HeaderPart headerPart => headerPart.Header,
                    FooterPart footerPart => footerPart.Footer,
                    _ => null
                };
                if (root is null)
                {
                    context.Diagnostics.Add("header-footer-part-invalid", ParserDiagnosticSeverity.Warning, "parser.header_footer_part_invalid");
                    continue;
                }
                var partKind = part is HeaderPart ? DocumentPartKind.Header : DocumentPartKind.Footer;
                var paragraphs = new List<ParsedParagraph>();
                foreach (var paragraph in root.Descendants<Paragraph>())
                {
                    var location = new DocumentElementLocation(partKind, part.Uri.OriginalString,
                        ParagraphIndex: context.ParagraphCount, HeaderFooterType: reference.Type, ElementKind: DocumentElementKind.Paragraph);
                    paragraphs.Add(ParseParagraph(paragraph, mainPart, styles, numbering, resolver, context, location, false));
                }
                result.Add(new(result.Count, reference.Type, partKind, part.Uri.OriginalString, paragraphs.ToArray(), []));
            }
            catch (ArgumentOutOfRangeException)
            {
                context.Diagnostics.Add("relationship-broken", ParserDiagnosticSeverity.Warning, "parser.relationship_broken");
            }
        }
        return result.ToArray();
    }

    private static ParsedNumberingReference? ParagraphNumbering(ParagraphProperties? properties, NumberingContext numbering)
    {
        var id = properties?.NumberingProperties?.NumberingId?.Val?.Value;
        if (id is null) return null;
        var level = properties?.NumberingProperties?.NumberingLevelReference?.Val?.Value;
        var instance = numbering.FullCatalog.Instances.FirstOrDefault(value => value.NumberingId == id.Value);
        var abstractNumbering = numbering.FullCatalog.AbstractDefinitions
            .FirstOrDefault(value => value.AbstractNumberingId == instance?.AbstractNumberingId);
        var abstractLevel = abstractNumbering?.Levels.FirstOrDefault(item => item.Level == level);
        return new(id.Value, level, instance?.AbstractNumberingId,
            abstractLevel?.RawFormat, abstractLevel?.LevelText);
    }

    private static DocumentElementLocation MainLocation(
        DocumentElementKind kind,
        int? sectionIndex = null,
        int? bodyElementIndex = null,
        int? paragraphIndex = null,
        int? runIndex = null,
        int? tableIndex = null) =>
        new(DocumentPartKind.MainDocument, "/word/document.xml", sectionIndex, bodyElementIndex,
            paragraphIndex, runIndex, tableIndex, ElementKind: kind);

    private static ParsedHeaderFooterType HeaderFooterType(string? value) => value?.ToLowerInvariant() switch
    {
        "default" => ParsedHeaderFooterType.Default,
        "first" => ParsedHeaderFooterType.First,
        "even" => ParsedHeaderFooterType.Even,
        _ => ParsedHeaderFooterType.Unknown
    };

    private static ParsedPageOrientation? PageOrientation(string? value) => value?.ToLowerInvariant() switch
    {
        "portrait" => ParsedPageOrientation.Portrait,
        "landscape" => ParsedPageOrientation.Landscape,
        _ => null
    };

    private static ParsedAlignment? Alignment(string? value) => value?.ToLowerInvariant() switch
    {
        "left" => ParsedAlignment.Left,
        "center" => ParsedAlignment.Center,
        "right" => ParsedAlignment.Right,
        "both" => ParsedAlignment.Justified,
        "distribute" => ParsedAlignment.Distributed,
        "start" => ParsedAlignment.Start,
        "end" => ParsedAlignment.End,
        _ => null
    };

    private static string AlignmentName(ParsedAlignment? value) => value switch
    {
        ParsedAlignment.Center => "Center",
        ParsedAlignment.Right => "Right",
        ParsedAlignment.Justified => "Both",
        ParsedAlignment.Distributed => "Distribute",
        ParsedAlignment.Start => "Start",
        ParsedAlignment.End => "End",
        _ => "Left"
    };

    private static ParsedBreakKind BreakKind(string? value) => value?.ToLowerInvariant() switch
    {
        "page" => ParsedBreakKind.Page,
        "column" => ParsedBreakKind.Column,
        "textwrapping" => ParsedBreakKind.TextWrapping,
        null or "" => ParsedBreakKind.Line,
        _ => ParsedBreakKind.Unknown
    };

    private static ParsedFieldKind FieldKind(string instruction) => NormalizeFieldInstruction(instruction) switch
    {
        "PAGE" => ParsedFieldKind.Page,
        "NUMPAGES" => ParsedFieldKind.NumPages,
        "TOC" => ParsedFieldKind.Toc,
        "REF" => ParsedFieldKind.Ref,
        "HYPERLINK" => ParsedFieldKind.Hyperlink,
        "DATE" => ParsedFieldKind.Date,
        "TIME" => ParsedFieldKind.Time,
        _ => ParsedFieldKind.Unknown
    };

    private static ParagraphFormattingProperties ParseParagraphFormatting(
        OpenXmlElement? properties,
        ParserContext context,
        DocumentElementLocation? location)
    {
        var indentation = properties?.GetFirstChild<Indentation>();
        var spacing = properties?.GetFirstChild<SpacingBetweenLines>();
        var numbering = properties?.GetFirstChild<NumberingProperties>();
        return new(
            Alignment(Attribute(properties?.GetFirstChild<Justification>(), "val")),
            Numeric(indentation, "left", context, location),
            Numeric(indentation, "right", context, location),
            Numeric(indentation, "firstLine", context, location),
            Numeric(indentation, "hanging", context, location),
            Numeric(spacing, "before", context, location),
            Numeric(spacing, "after", context, location),
            Numeric(spacing, "line", context, location),
            Attribute(spacing, "lineRule"),
            OnOff(properties?.GetFirstChild<KeepNext>()),
            OnOff(properties?.GetFirstChild<KeepLines>()),
            OnOff(properties?.GetFirstChild<PageBreakBefore>()),
            OnOff(properties?.GetFirstChild<WidowControl>()),
            OnOff(properties?.GetFirstChild<ContextualSpacing>()),
            NumericInt(properties?.GetFirstChild<OutlineLevel>(), "val", context, location),
            NumericInt(numbering?.NumberingId, "val", context, location),
            NumericInt(numbering?.NumberingLevelReference, "val", context, location));
    }

    private static RunFormattingProperties ParseRunFormatting(
        OpenXmlElement? properties,
        ParserContext context,
        DocumentElementLocation? location)
    {
        var fonts = properties?.GetFirstChild<RunFonts>();
        var languages = properties?.GetFirstChild<Languages>();
        return new(
            Attribute(properties?.GetFirstChild<RunStyle>(), "val"),
            Attribute(fonts, "ascii"),
            Attribute(fonts, "hAnsi"),
            Attribute(fonts, "eastAsia"),
            Attribute(fonts, "cs"),
            Attribute(fonts, "asciiTheme"),
            Attribute(fonts, "hAnsiTheme"),
            Attribute(fonts, "eastAsiaTheme"),
            Attribute(fonts, "cstheme"),
            NumericInt(properties?.GetFirstChild<FontSize>(), "val", context, location),
            NumericInt(properties?.GetFirstChild<FontSizeComplexScript>(), "val", context, location),
            OnOff(properties?.GetFirstChild<Bold>()),
            OnOff(properties?.GetFirstChild<Italic>()),
            Attribute(properties?.GetFirstChild<Underline>(), "val"),
            OnOff(properties?.GetFirstChild<Strike>()),
            OnOff(properties?.GetFirstChild<Vanish>()),
            OnOff(properties?.GetFirstChild<Caps>()),
            OnOff(properties?.GetFirstChild<SmallCaps>()),
            Attribute(properties?.GetFirstChild<Color>(), "val"),
            Attribute(languages, "val"),
            Attribute(languages, "eastAsia"),
            Attribute(languages, "bidi"),
            Attribute(properties?.GetFirstChild<VerticalTextAlignment>(), "val"));
    }

    private static ParsedStyleType StyleType(string? value) => value?.ToLowerInvariant() switch
    {
        "paragraph" => ParsedStyleType.Paragraph,
        "character" => ParsedStyleType.Character,
        "table" => ParsedStyleType.Table,
        "numbering" => ParsedStyleType.Numbering,
        _ => ParsedStyleType.Unknown
    };

    private static string? ChildValue(OpenXmlElement parent, string localName) =>
        Attribute(parent.ChildElements.FirstOrDefault(element => element.LocalName == localName), "val");

    private static string NormalizeStyleId(string styleId) => new(styleId.Trim().Take(128)
        .Select(value => char.IsLetterOrDigit(value) || value is '_' or '-' or '.' ? value : '_').ToArray());

    private static string NormalizeFieldInstruction(string instruction)
    {
        var value = instruction.TrimStart();
        var length = 0;
        while (length < value.Length && char.IsLetter(value[length])) length++;
        return length == 0 ? "UNKNOWN" : value[..length].ToUpperInvariant();
    }

    private static decimal? LineSpacingMultiple(SpacingBetweenLines? spacing)
    {
        var value = Numeric(spacing, "line");
        var rule = Attribute(spacing, "lineRule");
        return value is not null && (string.IsNullOrEmpty(rule) || string.Equals(rule, "auto", StringComparison.OrdinalIgnoreCase))
            ? Math.Round(value.Value / 240m, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    private static bool? OnOff(OpenXmlElement? element)
    {
        if (element is null) return null;
        var value = Attribute(element, "val");
        return value?.ToLowerInvariant() switch
        {
            "0" or "false" or "off" => false,
            _ => true
        };
    }

    private static bool? OnOffAttribute(OpenXmlElement element, string name)
    {
        var value = Attribute(element, name);
        if (value is null) return null;
        return value.ToLowerInvariant() is not ("0" or "false" or "off");
    }

    private static string? Attribute(OpenXmlElement? element, string localName)
        => Attribute(element, localName, null);

    private static string? Attribute(OpenXmlElement? element, string localName, string? namespaceUri)
    {
        if (element is null) return null;
        return element.GetAttributes().FirstOrDefault(item =>
            item.LocalName == localName && (namespaceUri is null || item.NamespaceUri == namespaceUri)).Value;
    }

    private static long? Numeric(OpenXmlElement? element, string attributeName, ParserContext? context = null, DocumentElementLocation? location = null)
    {
        var raw = Attribute(element, attributeName);
        if (raw is null) return null;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        context?.Diagnostics.Add("numeric-value-invalid", ParserDiagnosticSeverity.Warning, "parser.numeric_value_invalid", location,
            [new("attribute", attributeName)]);
        return null;
    }

    private static int? NumericInt(OpenXmlElement? element, string attributeName, ParserContext? context = null, DocumentElementLocation? location = null)
    {
        var value = Numeric(element, attributeName, context, location);
        return value is >= int.MinValue and <= int.MaxValue ? checked((int)value.Value) : null;
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? FirstNonNull(params int?[] values) => values.FirstOrDefault(value => value is not null);

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static decimal? TwipsToCm(long? twips) => twips is null
        ? null
        : Math.Round(twips.Value * 2.54m / 1440m, 2, MidpointRounding.AwayFromZero);

    private static DocxParserException Limit(string resource) =>
        new("resource-limit-exceeded", $"DOCX parser resource limit exceeded: {resource}.");

    private sealed class ParserContext(DocxParserOptions options, CancellationToken cancellationToken)
    {
        public int ParagraphCount { get; private set; }
        public int RunCount { get; private set; }
        public int TableCount { get; private set; }
        public int FootnoteReferenceCount { get; set; }
        public int EndnoteReferenceCount { get; set; }
        public int CommentReferenceCount { get; set; }
        public List<ParsedTable> Tables { get; } = [];
        public List<ParsedDrawing> Drawings { get; } = [];
        public List<ParsedField> Fields { get; } = [];
        public DiagnosticCollector Diagnostics { get; } = new(options.MaximumDiagnostics);

        public void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();
        public int NextParagraph()
        {
            CheckCancellation();
            if (ParagraphCount >= options.MaximumParagraphs) throw Limit("paragraphs");
            return ParagraphCount++;
        }
        public void NextRun()
        {
            CheckCancellation();
            if (RunCount >= options.MaximumRuns) throw Limit("runs");
            RunCount++;
        }
        public int NextTable()
        {
            CheckCancellation();
            if (TableCount >= options.MaximumTables) throw Limit("tables");
            return TableCount++;
        }
    }

    private sealed class DiagnosticCollector(int maximum)
    {
        private readonly List<ParserDiagnostic> _items = [];
        private bool _truncated;
        public IReadOnlyList<ParserDiagnostic> Items => _items.ToArray();

        public void Add(string code, ParserDiagnosticSeverity severity, string messageKey,
            DocumentElementLocation? location = null, IReadOnlyList<ParserDiagnosticMetadata>? metadata = null)
        {
            if (_items.Count < maximum)
            {
                _items.Add(new(code, severity, messageKey, location,
                    metadata?.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray()));
                return;
            }
            if (_truncated || maximum == 0) return;
            _items[^1] = new("diagnostics-truncated", ParserDiagnosticSeverity.Warning, "parser.diagnostics_truncated");
            _truncated = true;
        }
    }

    private sealed record StylesContext(
        Styles? Source,
        IReadOnlyDictionary<string, Style> Lookup,
        IReadOnlyList<ParsedStyleReference> Catalog,
        TextDefaults Defaults,
        ParsedDocumentDefaults DocumentDefaults);
    private sealed record TextDefaults(string? FontName, int? FontSizeHalfPoints);
    private sealed record NumberingContext(IReadOnlyList<ParsedNumberingReference> Catalog, ParsedNumberingCatalog FullCatalog);
    private sealed record RelationshipCounts(int Total, int External);
    private sealed record BodyResult(IReadOnlyList<ParsedParagraph> Paragraphs, IReadOnlyList<ParsedBodyElement> BodyElements);
}
