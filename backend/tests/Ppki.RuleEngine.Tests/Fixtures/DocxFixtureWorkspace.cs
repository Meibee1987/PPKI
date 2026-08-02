using System.Security.Cryptography;
using System.Text.Json;

namespace Ppki.RuleEngine.Tests.Fixtures;

public sealed class DocxFixtureWorkspace : IAsyncDisposable
{
    private DocxFixtureWorkspace(string originalPath, string workingPath, string temporaryDirectory, string originalChecksum)
    {
        OriginalPath = originalPath;
        WorkingPath = workingPath;
        _temporaryDirectory = temporaryDirectory;
        OriginalChecksum = originalChecksum;
    }

    private readonly string _temporaryDirectory;

    public string OriginalPath { get; }
    public string WorkingPath { get; }
    public string OriginalChecksum { get; }

    public static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "fixtures", "docx");

    public static async Task<DocxFixtureWorkspace> CreateAsync(string fixtureId, string? fixtureRoot = null, CancellationToken cancellationToken = default)
    {
        var root = fixtureRoot ?? FixtureRoot;
        var manifest = await DocxFixtureManifest.LoadAsync(root, cancellationToken);
        var fixture = manifest.Fixtures.SingleOrDefault(item => string.Equals(item.FixtureId, fixtureId, StringComparison.Ordinal));
        if (fixture is null)
        {
            throw new ArgumentException($"Unknown DOCX fixture id '{fixtureId}'.", nameof(fixtureId));
        }

        var originalPath = Path.Combine(root, "generated", fixture.Filename);
        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException($"DOCX fixture file is missing for id '{fixtureId}'.", originalPath);
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "ppki-docx-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var workingPath = Path.Combine(temporaryDirectory, fixture.Filename);
        var checksum = await ComputeSha256Async(originalPath, cancellationToken);

        await using (var source = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(workingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        return new DocxFixtureWorkspace(originalPath, workingPath, temporaryDirectory, checksum);
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(await sha256.ComputeHashAsync(stream, cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var finalChecksum = await ComputeSha256Async(OriginalPath);
            if (!string.Equals(OriginalChecksum, finalChecksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The original DOCX fixture changed during the test.");
            }
        }
        finally
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
    }
}

public sealed record DocxFixtureManifest(
    int SchemaVersion,
    string GeneratorVersion,
    IReadOnlyList<DocxFixtureDefinition> Fixtures)
{
    public static async Task<DocxFixtureManifest> LoadAsync(string fixtureRoot, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(fixtureRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("DOCX fixture manifest is missing.", manifestPath);
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<DocxFixtureManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }, cancellationToken) ?? throw new InvalidDataException("DOCX fixture manifest is invalid.");
    }
}

public sealed record DocxFixtureDefinition(
    string FixtureId,
    string Filename,
    string Description,
    bool Synthetic,
    JsonElement IntendedProperties,
    JsonElement ExpectedDocumentProperties,
    bool ContainsPersonalData,
    string GeneratorVersion);
