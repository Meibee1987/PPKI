using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Ppki.Application;

namespace Ppki.FixEngine;

public sealed class DocxPackageIntegritySnapshot
{
    internal DocxPackageIntegritySnapshot(
        IReadOnlyDictionary<string, string> partHashes,
        IReadOnlyList<ContentTypeIdentity> contentTypes,
        IReadOnlyList<RelationshipIdentity> relationships)
    {
        PartHashes = partHashes;
        ContentTypes = contentTypes;
        Relationships = relationships;
    }

    internal IReadOnlyDictionary<string, string> PartHashes { get; }
    internal IReadOnlyList<ContentTypeIdentity> ContentTypes { get; }
    internal IReadOnlyList<RelationshipIdentity> Relationships { get; }
}

internal sealed record ContentTypeIdentity(string Kind, string Name, string ContentType);

internal sealed record RelationshipIdentity(
    string OwningPart,
    string RelationshipId,
    string RelationshipType,
    string TargetUri,
    string TargetMode);

public static class DocxPackageIntegrity
{
    private const string MutablePart = "word/document.xml";
    private static readonly XNamespace ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    public static DocxPackageIntegritySnapshot Capture(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();
            var partHashes = entries.ToDictionary(
                entry => Normalize(entry.FullName),
                entry => Hash(entry),
                StringComparer.Ordinal);
            var contentTypesEntry = entries.Single(entry =>
                string.Equals(Normalize(entry.FullName), "[Content_Types].xml", StringComparison.Ordinal));
            var contentTypes = ReadContentTypes(contentTypesEntry);
            var relationships = entries
                .Where(entry => Normalize(entry.FullName).EndsWith(".rels", StringComparison.Ordinal))
                .SelectMany(ReadRelationships)
                .OrderBy(value => value.OwningPart, StringComparer.Ordinal)
                .ThenBy(value => value.RelationshipId, StringComparer.Ordinal)
                .ThenBy(value => value.RelationshipType, StringComparer.Ordinal)
                .ThenBy(value => value.TargetUri, StringComparer.Ordinal)
                .ThenBy(value => value.TargetMode, StringComparer.Ordinal)
                .ToArray();
            return new(partHashes, contentTypes, relationships);
        }
        catch (FixExecutionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
            or XmlException or InvalidOperationException or ArgumentException)
        {
            throw new FixExecutionException("fix-execution-package-integrity-failed");
        }
    }

    public static void ValidateMutation(DocxPackageIntegritySnapshot before, string mutatedPath)
    {
        var after = Capture(mutatedPath);
        if (!before.ContentTypes.SequenceEqual(after.ContentTypes)
            || !before.Relationships.SequenceEqual(after.Relationships)
            || !before.PartHashes.Keys.SequenceEqual(after.PartHashes.Keys, StringComparer.Ordinal)
            || before.PartHashes.Any(part => !string.Equals(part.Key, MutablePart, StringComparison.Ordinal)
                && !string.Equals(part.Value, after.PartHashes[part.Key], StringComparison.Ordinal)))
        {
            throw new FixExecutionException("fix-execution-package-integrity-failed");
        }
    }

    private static IReadOnlyList<ContentTypeIdentity> ReadContentTypes(ZipArchiveEntry entry)
    {
        var document = LoadXml(entry);
        return document.Root?.Elements()
            .Select(element => element.Name == ContentTypesNamespace + "Default"
                ? new ContentTypeIdentity("Default", Required(element, "Extension"), Required(element, "ContentType"))
                : element.Name == ContentTypesNamespace + "Override"
                    ? new ContentTypeIdentity("Override", Required(element, "PartName"), Required(element, "ContentType"))
                    : throw new InvalidDataException("Unsupported content type declaration."))
            .OrderBy(value => value.Kind, StringComparer.Ordinal)
            .ThenBy(value => value.Name, StringComparer.Ordinal)
            .ThenBy(value => value.ContentType, StringComparer.Ordinal)
            .ToArray() ?? throw new InvalidDataException("Missing content types root.");
    }

    private static IEnumerable<RelationshipIdentity> ReadRelationships(ZipArchiveEntry entry)
    {
        var owningPart = OwningPart(Normalize(entry.FullName));
        var document = LoadXml(entry);
        return document.Root?.Elements(RelationshipsNamespace + "Relationship")
            .Select(element => new RelationshipIdentity(
                owningPart,
                Required(element, "Id"),
                Required(element, "Type"),
                Required(element, "Target"),
                element.Attribute("TargetMode")?.Value ?? "Internal"))
            .ToArray() ?? throw new InvalidDataException("Missing relationships root.");
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string OwningPart(string relationshipPart)
    {
        if (string.Equals(relationshipPart, "_rels/.rels", StringComparison.Ordinal)) return "/";
        var marker = "/_rels/";
        var markerIndex = relationshipPart.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0 || !relationshipPart.EndsWith(".rels", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid relationship part name.");
        var directory = relationshipPart[..markerIndex];
        var sourceName = relationshipPart[(markerIndex + marker.Length)..^".rels".Length];
        return $"/{directory}/{sourceName}";
    }

    private static string Required(XElement element, string attribute) =>
        element.Attribute(attribute)?.Value is { Length: > 0 } value
            ? value
            : throw new InvalidDataException("Required package attribute is missing.");

    private static string Hash(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Normalize(string value) => value.Replace('\\', '/');
}
