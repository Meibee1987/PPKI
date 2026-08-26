using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using Ppki.Application;
using Ppki.DocxEngine;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed record ValidatedDocxOutput(
    string FilePath,
    long SizeBytes,
    string Sha256,
    ParsedDocument ParsedDocument);

public sealed class FinalDocxOutputValidator(IDocxParser parser)
{
    private const long MaximumBytes = 50L * 1024 * 1024;

    public async Task<ValidatedDocxOutput> ValidateMutationAsync(
        DocxPackageIntegritySnapshot sourcePackage,
        string finalizedPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePackage);
        try { DocxPackageIntegrity.ValidateMutation(sourcePackage, finalizedPath); }
        catch (FixExecutionException exception)
        {
            throw new FixExecutionException(FixFailureCategory.InvalidSource,
                "fix-result-package-invalid", exception);
        }
        return await ValidateStandaloneAsync(finalizedPath, cancellationToken);
    }

    public async Task<ValidatedDocxOutput> ValidatePublishedAsync(
        string publishedPath,
        string expectedSha256,
        long expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        var result = await ValidateStandaloneAsync(publishedPath, cancellationToken);
        if (result.SizeBytes != expectedSizeBytes
            || !string.Equals(result.Sha256, expectedSha256, StringComparison.Ordinal))
            throw new FixExecutionException(FixFailureCategory.Conflict, "fix-result-object-conflict");
        return result;
    }

    private async Task<ValidatedDocxOutput> ValidateStandaloneAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        try
        {
            var before = new FileInfo(fullPath);
            if (!before.Exists || before.Length is <= 0 or > MaximumBytes)
                throw new FixExecutionException(FixFailureCategory.InvalidSource,
                    "fix-execution-result-size-invalid");

            using (var package = WordprocessingDocument.Open(fullPath, false,
                       new OpenSettings { AutoSave = false }))
            {
                if (package.MainDocumentPart?.Document?.Body is null)
                    throw new FixExecutionException(FixFailureCategory.InvalidSource,
                        "fix-result-package-invalid");
            }

            var parsed = await parser.ParseAsync(fullPath, cancellationToken);
            if (parsed.ParserSchemaVersion != OpenXmlDocxParser.SchemaVersion)
                throw new FixExecutionException(FixFailureCategory.InvalidSource,
                    "fix-execution-parser-schema-mismatch");

            long sizeBytes;
            string sha256;
            await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sizeBytes = stream.Length;
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            }
            var after = new FileInfo(fullPath);
            if (!after.Exists || after.Length != sizeBytes || sizeBytes is <= 0 or > MaximumBytes)
                throw new FixExecutionException(FixFailureCategory.InvalidSource,
                    "fix-execution-result-size-invalid");
            return new(fullPath, sizeBytes, sha256, parsed);
        }
        catch (OperationCanceledException) { throw; }
        catch (FixExecutionException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FixExecutionException(FixFailureCategory.InvalidSource,
                "fix-result-package-invalid", exception);
        }
    }
}
