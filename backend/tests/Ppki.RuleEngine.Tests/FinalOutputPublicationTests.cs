using System.IO.Compression;
using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ppki.DocxEngine;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.RuleEngine.Tests.Fixtures;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FinalDocxOutputValidatorTests
{
    [Fact]
    public async Task Final_closed_clone_opens_reparses_and_hashes_exact_final_bytes()
    {
        await using var fixture = await DocxFixtureWorkspace.CreateAsync("safe-heading-fixers-mvp");
        var validator = new FinalDocxOutputValidator(new OpenXmlDocxParser());
        var baseline = DocxPackageIntegrity.Capture(fixture.WorkingPath);

        var result = await validator.ValidateMutationAsync(
            baseline, fixture.WorkingPath, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(fixture.WorkingPath);
        Assert.Equal(bytes.LongLength, result.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(OpenXmlDocxParser.SchemaVersion, result.ParsedDocument.ParserSchemaVersion);
        using var readOnly = WordprocessingDocument.Open(fixture.WorkingPath, false,
            new OpenSettings { AutoSave = false });
        Assert.NotNull(readOnly.MainDocumentPart?.Document?.Body);
    }

    [Fact]
    public async Task Exact_published_bytes_validate_to_same_checksum_size_and_structure()
    {
        await using var fixture = await DocxFixtureWorkspace.CreateAsync("paragraph-format-fixers");
        var validator = new FinalDocxOutputValidator(new OpenXmlDocxParser());
        var bytes = await File.ReadAllBytesAsync(fixture.WorkingPath);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var published = await validator.ValidatePublishedAsync(
            fixture.WorkingPath, sha, bytes.LongLength, CancellationToken.None);

        Assert.Equal(sha, published.Sha256);
        Assert.Equal(bytes.LongLength, published.SizeBytes);
        Assert.NotEmpty(published.ParsedDocument.Paragraphs);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing-main")]
    [InlineData("missing-body")]
    public async Task Corrupt_or_incomplete_package_fails_before_publication_metadata(string kind)
    {
        var root = TestRoot();
        var path = Path.Combine(root, $"{kind}.docx");
        try
        {
            if (kind == "corrupt") await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            else
            {
                using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
                if (kind == "missing-body")
                {
                    var main = package.AddMainDocumentPart();
                    main.Document = new Document();
                    main.Document.Save();
                }
            }
            var bytes = await File.ReadAllBytesAsync(path);
            var error = await Assert.ThrowsAsync<Ppki.Application.FixExecutionException>(() =>
                new FinalDocxOutputValidator(new OpenXmlDocxParser()).ValidatePublishedAsync(
                    path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength,
                    CancellationToken.None));
            Assert.Equal("fix-result-package-invalid", error.DiagnosticCode);
            Assert.Equal(FixFailureCategory.InvalidSource, error.Category);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Published_checksum_or_size_mismatch_fails_closed(bool wrongHash, bool wrongSize)
    {
        await using var fixture = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        var bytes = await File.ReadAllBytesAsync(fixture.WorkingPath);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var error = await Assert.ThrowsAsync<Ppki.Application.FixExecutionException>(() =>
            new FinalDocxOutputValidator(new OpenXmlDocxParser()).ValidatePublishedAsync(
                fixture.WorkingPath, wrongHash ? new string('0', 64) : sha,
                wrongSize ? bytes.LongLength + 1 : bytes.LongLength, CancellationToken.None));
        Assert.Equal("fix-result-object-conflict", error.DiagnosticCode);
    }

    [Fact]
    public async Task Cancellation_before_validation_does_not_open_or_hash_output()
    {
        await using var fixture = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FinalDocxOutputValidator(new OpenXmlDocxParser()).ValidatePublishedAsync(
                fixture.WorkingPath, new string('0', 64), 1, cancellation.Token));
    }

    private static string TestRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ppki-final-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class OutputPublicationArchitectureTests
{
    [Fact]
    public void Pipeline_validates_closed_clone_then_uploads_and_revalidates_published_bytes_before_commit()
    {
        var source = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        var validateFinal = source.IndexOf("outputValidator.ValidateMutationAsync", StringComparison.Ordinal);
        var hash = source.IndexOf("validatedOutput.Sha256", validateFinal, StringComparison.Ordinal);
        var upload = source.IndexOf("storage.SaveAsync(stream", hash, StringComparison.Ordinal);
        var downloadPublished = source.IndexOf("publishedResult = await storage.MaterializeToTempFileAsync", upload, StringComparison.Ordinal);
        var validatePublished = source.IndexOf("outputValidator.ValidatePublishedAsync", downloadPublished, StringComparison.Ordinal);
        var finalize = source.IndexOf("CompleteWithVersion", validatePublished, StringComparison.Ordinal);

        Assert.True(validateFinal >= 0 && hash > validateFinal && upload > hash
            && downloadPublished > upload && validatePublished > downloadPublished && finalize > validatePublished);
        Assert.Contains("using var package = WordprocessingDocument.Open(working, true", source, StringComparison.Ordinal);
        Assert.Contains("var resultId = claim.ExecutionId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Publication_identity_path_lineage_and_transaction_are_exact_and_retry_safe()
    {
        var processor = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        var paths = Source("backend", "src", "Ppki.Infrastructure", "StorageObjectPathBuilder.cs");
        Assert.Contains("BuildVersionPath(source.OwnerUserId, source.DocumentId, resultId)", processor, StringComparison.Ordinal);
        Assert.Contains("x-upsert", Source("backend", "src", "Ppki.Infrastructure", "SupabaseFileStorage.cs"), StringComparison.Ordinal);
        Assert.Contains("Id = resultId", processor, StringComparison.Ordinal);
        Assert.Contains("ParentVersionId = source.SourceVersionId", processor, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.Serializable", processor, StringComparison.Ordinal);
        Assert.Contains("for update", processor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("document.CurrentVersionNo != source.SourceVersionNo", processor, StringComparison.Ordinal);
        Assert.Contains("document.CurrentVersionNo = nextVersion", processor, StringComparison.Ordinal);
        Assert.Contains("/document.docx", paths, StringComparison.Ordinal);
        Assert.DoesNotContain("source.StorageKey", processor[processor.IndexOf("var objectPath", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_cleanup_is_bounded_and_ambiguous_commit_preserves_canonical_object()
    {
        var processor = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        Assert.Contains("ownsUploadedObject", processor, StringComparison.Ordinal);
        Assert.Contains("IsCanonicalResultAsync", processor, StringComparison.Ordinal);
        Assert.Contains("storage.DeleteAsync(uploaded.StorageBucket, uploaded.StorageKey", processor, StringComparison.Ordinal);
        Assert.Contains("Database commit made this object canonical", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAsync(source.StorageBucket", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAsync(source.StorageKey", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void New_version_and_current_pointer_commit_together_while_historical_version_is_never_updated()
    {
        var processor = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        Assert.Contains("db.DocumentVersions.Add(resultVersion)", processor, StringComparison.Ordinal);
        Assert.Contains("document.CurrentVersionNo = nextVersion", processor, StringComparison.Ordinal);
        Assert.Contains("job.State = FixExecutionState.Completed", processor, StringComparison.Ordinal);
        Assert.Contains("transaction.CommitAsync", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("db.DocumentVersions.Update", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("source.SourceDocumentVersion", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void Nochange_creates_no_version_but_persists_s8_t08_results_without_reaudit()
    {
        var processor = Source("backend", "services", "Ppki.Worker", "FixExecutionProcessor.cs");
        var noChangeStart = processor.IndexOf("private async Task CompleteNoChangeAsync", StringComparison.Ordinal);
        var noChangeEnd = processor.IndexOf("private async Task<SourceRow?>", noChangeStart, StringComparison.Ordinal);
        var noChange = processor[noChangeStart..noChangeEnd];
        Assert.Contains("job.State = FixExecutionState.NoChange", noChange, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentVersions.Add", noChange, StringComparison.Ordinal);
        Assert.DoesNotContain("FixAction", processor, StringComparison.Ordinal);
        Assert.Contains("AddResults", noChange, StringComparison.Ordinal);
        Assert.Contains("FixItemResult", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("Reaudit", processor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FindingStatus.Fixed", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_schema_already_supplies_version_uniqueness_immutability_and_result_lineage()
    {
        var initial = Source("supabase", "migrations", "202608010001_initial_schema.sql");
        var immutable = Source("supabase", "migrations", "202608020004_audit_immutability.sql");
        var jobs = Source("supabase", "migrations", "202608060002_remediation_failure_conflict_hardening.sql");
        Assert.Contains("unique(document_id,version_no), unique(storage_bucket,storage_key)", initial,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trg_document_versions_reject_update", immutable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trg_document_versions_reject_delete", immutable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fix execution result lineage is invalid", jobs, StringComparison.Ordinal);
        Assert.Contains("result_document, result_parent", jobs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parent_version_id", jobs, StringComparison.OrdinalIgnoreCase);
    }

    private static string Source(params string[] segments) =>
        File.ReadAllText(Path.Combine([Data.RepositoryRoot(), .. segments]));
}
