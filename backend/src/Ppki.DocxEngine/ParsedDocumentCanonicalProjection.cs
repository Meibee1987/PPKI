using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ppki.DocxEngine;

public static class ParsedDocumentCanonicalProjection
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(ParsedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var projection = new
        {
            document.ParserSchemaVersion,
            document.ProjectionSchemaVersion,
            document.PackageType,
            document.Counts,
            DocumentDefaults = document.FormattingDefaults,
            ThemeFonts = document.ThemeFontCatalog,
            Sections = document.Sections.Select(section => new
            {
                section.Index,
                Location = section.Location?.ToCompactString(),
                section.PageWidthTwips,
                section.PageHeightTwips,
                section.Orientation,
                section.MarginTopTwips,
                section.MarginRightTwips,
                section.MarginBottomTwips,
                section.MarginLeftTwips,
                section.HeaderDistanceTwips,
                section.FooterDistanceTwips,
                section.GutterTwips,
                section.SectionType,
                section.ColumnCount,
                section.ColumnSpacingTwips,
                section.StartPageNumber,
                section.EffectiveFormatting,
                HeaderFooterReferences = section.HeaderFooterReferenceList.Select(reference => new
                {
                    reference.Type,
                    reference.RelationshipId,
                    reference.NormalizedPartUri
                })
            }),
            BodyElements = document.BodyElementOrder.Select(element => new
            {
                element.Index,
                element.Kind,
                Location = element.Location.ToCompactString(),
                element.ParagraphIndex,
                element.TableIndex,
                element.SectionIndex
            }),
            Paragraphs = document.Paragraphs.Select(paragraph => new
            {
                paragraph.Index,
                Location = paragraph.Location?.ToCompactString(),
                paragraph.StyleId,
                paragraph.NumberingReference,
                paragraph.DirectAlignment,
                paragraph.DirectIndentLeftTwips,
                paragraph.DirectIndentRightTwips,
                paragraph.DirectFirstLineIndentTwips,
                paragraph.DirectHangingIndentTwips,
                paragraph.DirectSpacingBeforeTwips,
                paragraph.DirectSpacingAfterTwips,
                paragraph.DirectLineSpacingValue,
                paragraph.DirectLineSpacingRule,
                paragraph.KeepWithNext,
                paragraph.KeepLinesTogether,
                paragraph.PageBreakBefore,
                paragraph.OutlineLevel,
                paragraph.HasTabs,
                paragraph.HasBreaks,
                paragraph.HasFields,
                paragraph.HasDrawings,
                paragraph.HasBookmarks,
                paragraph.HasHyperlinks,
                paragraph.HasTrackedChanges,
                paragraph.DirectFormatting,
                paragraph.EffectiveFormatting,
                paragraph.EffectiveNumbering,
                Runs = paragraph.RunList.Select(run => new
                {
                    run.Index,
                    Location = run.Location.ToCompactString(),
                    TextSegmentCount = run.TextSegments.Count,
                    run.DirectFontAscii,
                    run.DirectFontHighAnsi,
                    run.DirectFontSizeHalfPoints,
                    run.Bold,
                    run.Italic,
                    run.Underline,
                    run.Language,
                    run.VerticalAlignment,
                    run.Breaks,
                    run.TabCount,
                    run.FieldIndexes,
                    run.DrawingIndexes,
                    run.IsDeleted,
                    run.IsInserted,
                    run.IsHidden,
                    CharacterStyleId = run.CharacterStyleReference?.StyleId,
                    run.DirectFormatting,
                    run.EffectiveFormatting
                })
            }),
            Tables = document.TableInventory,
            Drawings = document.DrawingInventory,
            Fields = document.FieldInventory,
            HeaderFooters = document.HeaderFooterInventory.Select(item => new
            {
                item.Index,
                item.Type,
                item.PartKind,
                item.PartUri,
                Paragraphs = item.Paragraphs.Select(paragraph => new
                {
                    paragraph.Index,
                    Location = paragraph.Location?.ToCompactString(),
                    paragraph.StyleId,
                    RunCount = paragraph.RunList.Count
                })
            }),
            Styles = document.Styles.OrderBy(style => style.DeclarationOrder).Select(style => new
            {
                style.StyleId,
                style.Name,
                style.Type,
                style.ParsedType,
                style.IsDefault,
                style.IsCustom,
                style.BasedOnStyleId,
                style.NextStyleId,
                style.LinkedStyleId,
                style.DeclarationOrder,
                style.ParagraphProperties,
                style.RunProperties
            }),
            Numbering = document.Numbering,
            NumberingCatalog = document.FullNumberingCatalog,
            Headings = document.Headings.Select(heading => new
            {
                heading.Index,
                heading.ParagraphIndex,
                Location = heading.Location.ToCompactString(),
                heading.Level,
                heading.Classification,
                heading.Evidence,
                heading.EffectiveParagraphStyleId,
                heading.OutlineLevel,
                heading.Numbering,
                heading.StartsNewSection,
                heading.Order
            }),
            Outline = document.DocumentOutline,
            Diagnostics = document.ParserDiagnostics.Select(diagnostic => new
            {
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.MessageKey,
                Location = diagnostic.Location?.ToCompactString(),
                diagnostic.Metadata
            })
        };
        return JsonSerializer.Serialize(projection, Options);
    }

    public static string Sha256(ParsedDocument document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(document))));
}
