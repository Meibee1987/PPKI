using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed class BodyJustifiedFixProvider : IFixPreviewProvider, IFixApplyProvider
{
    public const string Id = "body-justified-direct-paragraph";
    public const string Version = "1.0";
    public string CapabilityId => Id;
    public string CapabilityVersion => Version;
    public IReadOnlySet<string> ValidationKeys { get; } = new HashSet<string>(["body.justified"], StringComparer.Ordinal);

    public bool TryCreate(FixPlanFindingSnapshot finding, out FixOperationDraft operation, out string diagnosticCode)
    {
        operation = null!;
        diagnosticCode = "fix-preview-provider-rejected-snapshot";
        if (finding.ValidationKey != "body.justified" || finding.RuleCode != "PPKI-LAY-019"
            || finding.FixMode != FixMode.Auto || finding.FindingState != FindingStatus.Open)
            return false;
        try
        {
            using var actual = JsonDocument.Parse(finding.ActualJson);
            using var expected = JsonDocument.Parse(finding.ExpectedJson);
            using var location = JsonDocument.Parse(finding.LocationJson);
            if (Text(actual.RootElement, "property") != "alignment"
                || string.Equals(Text(actual.RootElement, "normalizedValue"), "Justified", StringComparison.OrdinalIgnoreCase)
                || Text(expected.RootElement, "property") != "alignment"
                || Text(expected.RootElement, "validationKey") != "body.justified"
                || !ArrayContains(expected.RootElement, "acceptedValues", "Justified")
                || !FormattingFixSnapshot.ParagraphLocation(location.RootElement,
                    out var sectionIndex, out var bodyIndex, out var paragraphIndex))
                return false;

            operation = new(new("main-document-paragraph", bodyIndex, sectionIndex, paragraphIndex, null),
                "paragraph.alignment", new("enum-code", "justified"),
                "source-finding-snapshot-must-match", "set-paragraph-alignment-justified");
            diagnosticCode = "fix-operation-planned";
            return true;
        }
        catch (JsonException) { return false; }
    }

    public Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = context.Operation;
        if (!TryCreate(context.Finding, out var approvedDraft, out _))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");
        if (approvedDraft.Target != operation.Target)
            throw new FixExecutionException("fix-operation-target-precondition-failed");
        if (approvedDraft.PropertyIdentifier != operation.PropertyIdentifier
            || approvedDraft.Expected != operation.Expected)
            throw new FixExecutionException("fix-operation-contract-invalid");
        if (operation.CapabilityId != Id || operation.CapabilityVersion != Version
            || operation.ValidationKey != "body.justified" || operation.RuleCode != "PPKI-LAY-019"
            || operation.OperationKind != FixOperationKind.SetProperty
            || operation.PropertyIdentifier != "paragraph.alignment"
            || operation.Expected != new FixExpectedValueDescriptor("enum-code", "justified")
            || operation.Target.Scope != "main-document-paragraph"
            || operation.Target.BodyElementIndex is null || operation.Target.ParagraphIndex is null)
            throw new FixExecutionException("fix-operation-contract-invalid");

        var parsed = FormattingFixSnapshot.NormalBodyParagraph(context);

        using var ownedPackage = context.OpenPackage is null
            ? WordprocessingDocument.Open(context.WorkingFilePath, true, new OpenSettings { AutoSave = false })
            : null;
        var package = context.OpenPackage ?? ownedPackage!;
        var main = package.MainDocumentPart ?? throw new FixExecutionException("fix-operation-main-part-missing");
        var document = main.Document ?? throw new FixExecutionException("fix-operation-document-missing");
        var body = document.Body ?? throw new FixExecutionException("fix-operation-body-missing");
        var element = body.Elements().ElementAtOrDefault(operation.Target.BodyElementIndex.Value);
        if (element is not Paragraph paragraph)
            throw new FixExecutionException("fix-operation-target-precondition-failed");
        if (paragraph.ParagraphProperties?.Justification?.Val?.Value == JustificationValues.Both)
            return Task.FromResult(FixApplyOutcome.NoChange);

        using var actual = JsonDocument.Parse(context.Finding.ActualJson);
        var snapshotValue = Text(actual.RootElement, "normalizedValue");
        var currentValue = parsed.EffectiveFormatting?.Alignment.Value?.ToString() ?? parsed.Alignment;
        if (Text(actual.RootElement, "property") != "alignment"
            || !string.Equals(snapshotValue, currentValue, StringComparison.OrdinalIgnoreCase))
            throw new FixExecutionException("fix-operation-source-snapshot-mismatch");

        cancellationToken.ThrowIfCancellationRequested();
        paragraph.ParagraphProperties ??= new ParagraphProperties();
        paragraph.ParagraphProperties.Justification = new Justification { Val = JustificationValues.Both };
        if (ownedPackage is not null) document.Save();
        return Task.FromResult(FixApplyOutcome.Changed);
    }

    private static string Text(JsonElement root, string name)
    {
        var property = root.EnumerateObject().FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : string.Empty;
    }

    private static bool ArrayContains(JsonElement root, string name, string expected)
    {
        var property = root.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind == JsonValueKind.Array
            && property.Value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), expected, StringComparison.OrdinalIgnoreCase));
    }
}

public static class ProductionFixCapabilities
{
    public static RemediationCapabilityRegistry CreatePreviewRegistry()
    {
        var justified = new BodyJustifiedFixProvider();
        var bodyFont = new BodyFontFixProvider();
        var bodySpacing = new BodyLineSpacingFixProvider();
        var firstLine = new BodyFirstLineIndentFixProvider();
        var abstractSpacing = new AbstractParagraphSpacingFixProvider();
        var chapterCentered = new ChapterCenteredFixProvider();
        var pageSize = new SectionPageSizeFixProvider();
        var margins = new SectionMarginFixProvider();
        return new RemediationCapabilityRegistry([
            new(BodyJustifiedFixProvider.Id, BodyJustifiedFixProvider.Version, "body.justified",
                FixOperationKind.SetProperty, ["actual", "expected", "location"], false, true,
                "body-justified-preview", "set-paragraph-alignment-justified", true, justified),
            Descriptor(bodyFont, "body.font-times-new-roman-12", "body-font-preview", "set-run-font-format"),
            Descriptor(bodySpacing, "body.line-spacing-single", "body-line-spacing-preview", "set-paragraph-line-spacing"),
            Descriptor(firstLine, "body.first-line-indent-1cm", "body-first-line-indent-preview", "set-paragraph-first-line-indent"),
            Descriptor(abstractSpacing, "abstract.skripsi-single-spacing-zero-paragraph-spacing", "abstract-spacing-preview", "set-abstract-paragraph-spacing"),
            Descriptor(abstractSpacing, "abstract-summary-single-spacing-zero-paragraph-spacing", "abstract-summary-spacing-preview", "set-abstract-paragraph-spacing"),
            Descriptor(chapterCentered, "heading.chapter-centered", "chapter-centered-preview", "set-heading-alignment-centered"),
            Descriptor(pageSize, "section.page-size-a4", "section-page-size-preview", "set-section-page-size-a4"),
            Descriptor(margins, "section.margin-left-4cm", "section-margin-left-preview", "set-section-margin"),
            Descriptor(margins, "section.margin-right-3cm", "section-margin-right-preview", "set-section-margin"),
            Descriptor(margins, "section.margin-top-3cm", "section-margin-top-preview", "set-section-margin"),
            Descriptor(margins, "section.margin-bottom-3cm", "section-margin-bottom-preview", "set-section-margin")
        ]);
    }

    public static FixApplyCapabilityRegistry CreateApplyRegistry()
    {
        var registry = new FixApplyCapabilityRegistry([
            new BodyJustifiedFixProvider(), new BodyFontFixProvider(), new BodyLineSpacingFixProvider(),
            new BodyFirstLineIndentFixProvider(), new AbstractParagraphSpacingFixProvider(),
            new ChapterCenteredFixProvider(), new SectionPageSizeFixProvider(), new SectionMarginFixProvider()
        ]);
        if (CreatePreviewRegistry().Capabilities.Any(capability => capability.DocumentMutationImplementationExists
            && registry.GetAvailability(capability.ValidationKey, capability.CapabilityId,
                capability.CapabilityVersion) != FixApplyProviderAvailability.Available))
            throw new FixPlanConfigurationException("fix-apply-capability-configuration-invalid");
        return registry;
    }

    private static RemediationCapability Descriptor(
        IFixApplyProvider applyProvider, string validationKey, string previewId, string description) =>
        new(applyProvider.CapabilityId, applyProvider.CapabilityVersion, validationKey,
            FixOperationKind.SetProperty, ["actual", "expected", "location"], false, true,
            previewId, description, true, (IFixPreviewProvider)applyProvider);
}
