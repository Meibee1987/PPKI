using Ppki.Application;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class StorageObjectPathBuilderTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Document = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
    private static readonly Guid Version = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
    private static readonly Guid Audit = Guid.Parse("12345678-1234-1234-1234-123456789abc");
    private readonly IStorageObjectPathBuilder _builder = new StorageObjectPathBuilder();

    [Fact]
    public void Builds_canonical_original_and_version_paths_without_user_filename()
    {
        var original = _builder.BuildOriginalPath(Owner, Document, Version);
        var version = _builder.BuildVersionPath(Owner, Document, Version);

        Assert.Equal("11111111-2222-3333-4444-555555555555/66666666-7777-8888-9999-aaaaaaaaaaaa/bbbbbbbb-cccc-dddd-eeee-ffffffffffff/original.docx", original);
        Assert.Equal("11111111-2222-3333-4444-555555555555/66666666-7777-8888-9999-aaaaaaaaaaaa/bbbbbbbb-cccc-dddd-eeee-ffffffffffff/document.docx", version);
        _builder.ValidateStoredPath(StorageObjectPathBuilder.OriginalBucket, original);
        _builder.ValidateStoredPath(StorageObjectPathBuilder.VersionBucket, version);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData(".JSON")]
    public void Builds_allowed_audit_reports(string extension)
    {
        var path = _builder.BuildAuditReportPath(Owner, Document, Audit, extension);

        Assert.EndsWith(extension.Contains("JSON", StringComparison.Ordinal) ? ".json" : ".pdf", path, StringComparison.Ordinal);
        _builder.ValidateStoredPath(StorageObjectPathBuilder.ReportBucket, path);
    }

    [Theory]
    [InlineData("../original.docx")]
    [InlineData("\\owner/document/version/original.docx")]
    [InlineData("/owner/document/version/original.docx")]
    [InlineData("https://example.invalid/object.docx")]
    [InlineData("owner//version/original.docx")]
    [InlineData("11111111-2222-3333-4444-555555555555/66666666-7777-8888-9999-AAAAAAAAAAAA/original.docx")]
    public void Rejects_noncanonical_or_unsafe_stored_paths(string path)
    {
        Assert.Throws<ArgumentException>(() => _builder.ValidateStoredPath(StorageObjectPathBuilder.OriginalBucket, path));
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("exe")]
    [InlineData("pdf?download=1")]
    public void Rejects_unsupported_report_extensions(string extension)
    {
        Assert.Throws<ArgumentException>(() => _builder.BuildAuditReportPath(Owner, Document, Audit, extension));
    }
}
