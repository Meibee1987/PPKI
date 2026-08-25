using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;
using Ppki.RuleEngine.Tests.Fixtures;
using Ppki.Worker;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class FixExecutionWorkspaceTests
{
    [Fact]
    public async Task Workspace_and_filename_are_random_internal_and_ignore_source_metadata()
    {
        var root = TestRoot();
        var source = Path.Combine(root, "uploaded-thesis-title.docx");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var checksum = await ShaAsync(source);
        try
        {
            await using var first = FixExecutionWorkspace.Create(root);
            await using var second = FixExecutionWorkspace.Create(root);
            var firstPath = await first.MaterializeAsync(source, checksum, CancellationToken.None);
            var secondPath = await second.MaterializeAsync(source, checksum, CancellationToken.None);

            Assert.NotEqual(first.WorkspacePath, second.WorkspacePath);
            Assert.NotEqual(firstPath, secondPath);
            Assert.Matches("^ppki-fix-[0-9a-f]{32}$", Path.GetFileName(first.WorkspacePath));
            Assert.Matches("^working-[0-9a-f]{32}\\.docx$", Path.GetFileName(firstPath));
            Assert.DoesNotContain("uploaded", firstPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("thesis-title", firstPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("..", Path.GetRelativePath(root, firstPath), StringComparison.Ordinal);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Golden_docx_clone_is_byte_identical_hash_identical_and_source_stays_unchanged()
    {
        await using var fixture = await DocxFixtureWorkspace.CreateAsync("minimal-invalid-layout");
        var root = TestRoot();
        var beforeBytes = await File.ReadAllBytesAsync(fixture.OriginalPath);
        var beforeSha = await ShaAsync(fixture.OriginalPath);
        string workspacePath;
        try
        {
            await using (var workspace = FixExecutionWorkspace.Create(root))
            {
                workspacePath = workspace.WorkspacePath;
                var clone = await workspace.MaterializeAsync(
                    fixture.OriginalPath, beforeSha, CancellationToken.None);

                Assert.Equal(beforeSha, workspace.SourceSha256);
                Assert.Equal(beforeSha, workspace.CloneSha256);
                Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(clone));
                using var package = WordprocessingDocument.Open(clone, false,
                    new OpenSettings { AutoSave = false });
                Assert.NotNull(package.MainDocumentPart);
            }

            Assert.False(Directory.Exists(workspacePath));
            Assert.Equal(beforeSha, await ShaAsync(fixture.OriginalPath));
            Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(fixture.OriginalPath));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Wrong_checksum_fails_before_clone_and_cleanup_is_idempotent()
    {
        var root = TestRoot();
        var source = Path.Combine(root, "source.docx");
        await File.WriteAllBytesAsync(source, [5, 6, 7, 8]);
        var workspace = FixExecutionWorkspace.Create(root);
        var workspacePath = workspace.WorkspacePath;
        try
        {
            var exception = await Assert.ThrowsAsync<FixExecutionException>(() =>
                workspace.MaterializeAsync(source, new string('0', 64), CancellationToken.None));
            Assert.Equal("source-hash-mismatch", exception.DiagnosticCode);
            Assert.Null(workspace.WorkingFilePath);

            await workspace.DisposeAsync();
            await workspace.DisposeAsync();
            Assert.False(Directory.Exists(workspacePath));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Invalid_package_and_follow_on_failure_release_handles_and_cleanup_workspace()
    {
        var root = TestRoot();
        var source = Path.Combine(root, "invalid.docx");
        await File.WriteAllBytesAsync(source, [9, 10, 11, 12]);
        var workspace = FixExecutionWorkspace.Create(root);
        var workspacePath = workspace.WorkspacePath;
        try
        {
            var clone = await workspace.MaterializeAsync(
                source, await ShaAsync(source), CancellationToken.None);
            Assert.Throws<FixExecutionException>(() => DocxPackageIntegrity.Capture(clone));
            await using (File.Open(clone, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally
        {
            await workspace.DisposeAsync();
            Assert.False(Directory.Exists(workspacePath));
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Cancellation_and_missing_source_failure_leave_no_workspace()
    {
        var root = TestRoot();
        try
        {
            var cancelled = FixExecutionWorkspace.Create(root);
            var cancelledPath = cancelled.WorkspacePath;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.MaterializeAsync(
                Path.Combine(root, "missing.docx"), new string('0', 64), cancellation.Token));
            await cancelled.DisposeAsync();
            Assert.False(Directory.Exists(cancelledPath));

            var missing = FixExecutionWorkspace.Create(root);
            var missingPath = missing.WorkspacePath;
            await Assert.ThrowsAsync<FileNotFoundException>(() => missing.MaterializeAsync(
                Path.Combine(root, "missing.docx"), new string('0', 64), CancellationToken.None));
            await missing.DisposeAsync();
            Assert.False(Directory.Exists(missingPath));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Concurrent_materialization_of_same_source_has_no_collision_and_retry_is_fresh()
    {
        var root = TestRoot();
        var source = Path.Combine(root, "source.docx");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1024 * 128));
        var checksum = await ShaAsync(source);
        try
        {
            var workspaces = Enumerable.Range(0, 8).Select(_ => FixExecutionWorkspace.Create(root)).ToArray();
            var paths = await Task.WhenAll(workspaces.Select(value =>
                value.MaterializeAsync(source, checksum, CancellationToken.None)));
            Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
            Assert.All(workspaces, value => Assert.Equal(checksum, value.CloneSha256));
            foreach (var workspace in workspaces) await workspace.DisposeAsync();

            await using var retry = FixExecutionWorkspace.Create(root);
            Assert.DoesNotContain(retry.WorkspacePath,
                workspaces.Select(value => value.WorkspacePath), StringComparer.Ordinal);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Cleanup_deletes_only_owned_workspace_and_preserves_siblings()
    {
        var root = TestRoot();
        var sentinel = Path.Combine(root, "outside-sentinel.bin");
        await File.WriteAllBytesAsync(sentinel, [1]);
        var workspace = FixExecutionWorkspace.Create(root);
        var workspacePath = workspace.WorkspacePath;
        try
        {
            await workspace.DisposeAsync();
            Assert.False(Directory.Exists(workspacePath));
            Assert.True(File.Exists(sentinel));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task Unix_workspace_and_clone_have_no_group_or_other_permissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = TestRoot();
        var source = Path.Combine(root, "source.docx");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        try
        {
            await using var workspace = FixExecutionWorkspace.Create(root);
            var clone = await workspace.MaterializeAsync(
                source, await ShaAsync(source), CancellationToken.None);
            const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(workspace.WorkspacePath) & forbidden);
            Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(clone) & forbidden);
            Assert.True(workspace.UsesExplicitUnixPermissions);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void Processor_preserves_registry_before_download_and_clone_before_writable_package_ordering()
    {
        var source = File.ReadAllText(Path.Combine(Data.RepositoryRoot(), "backend", "services",
            "Ppki.Worker", "FixExecutionProcessor.cs"));
        var approvedBinding = source.IndexOf("ValidateApprovedSnapshot(source, approved)", StringComparison.Ordinal);
        var registry = source.IndexOf("resolvedOperations = ResolveApprovedOperations", StringComparison.Ordinal);
        var download = source.IndexOf("storage.MaterializeToTempFileAsync(source.StorageBucket", StringComparison.Ordinal);
        var clone = source.IndexOf("workspace.MaterializeAsync", StringComparison.Ordinal);
        var writablePackage = source.IndexOf("WordprocessingDocument.Open(working, true", StringComparison.Ordinal);
        Assert.True(approvedBinding >= 0 && registry > approvedBinding && download > registry
            && clone > download && writablePackage > clone);
        Assert.Contains("materialized, source.SourceSha256", source, StringComparison.Ordinal);
        Assert.Contains("if (operationFailure is null)", source, StringComparison.Ordinal);
        Assert.Contains("workspace-cleanup-failed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Copy(materialized", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FixPlanRecords", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_has_no_storage_persistence_document_version_or_content_logging_dependency()
    {
        var source = File.ReadAllText(Path.Combine(Data.RepositoryRoot(), "backend", "services",
            "Ppki.Worker", "FixExecutionWorkspace.cs"));
        Assert.Contains("FileAccess.Read", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ILogger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OriginalFilename", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storage_materialization_uses_get_and_returns_exact_downloaded_bytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var handler = new StorageHandler(() => new MemoryStream(bytes, writable: false));
        var storage = Storage(handler);
        var path = await storage.MaterializeToTempFileAsync(
            StorageObjectPathBuilder.OriginalBucket, OriginalObjectPath(), CancellationToken.None);
        try
        {
            Assert.Equal(HttpMethod.Get, handler.Method);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Partial_storage_download_failure_deletes_generated_temp_file()
    {
        var before = Directory.EnumerateFiles(Path.GetTempPath(), "ppki-*.docx").ToHashSet(StringComparer.Ordinal);
        var handler = new StorageHandler(() => new ThrowAfterFirstReadStream(RandomNumberGenerator.GetBytes(4096)));
        var storage = Storage(handler);

        await Assert.ThrowsAsync<IOException>(() => storage.MaterializeToTempFileAsync(
            StorageObjectPathBuilder.OriginalBucket, OriginalObjectPath(), CancellationToken.None));

        var after = Directory.EnumerateFiles(Path.GetTempPath(), "ppki-*.docx").ToHashSet(StringComparer.Ordinal);
        Assert.True(before.SetEquals(after));
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    private static string TestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ppki-workspace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static async Task<string> ShaAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static SupabaseFileStorage Storage(StorageHandler handler) => new(
        new TestHttpClientFactory(handler),
        Options.Create(new SupabaseOptions
        {
            Url = "http://127.0.0.1:54321",
            PublishableKey = "synthetic-publishable-key",
            SecretKey = "synthetic-secret-key"
        }),
        new StorageObjectPathBuilder());

    private static string OriginalObjectPath() => new StorageObjectPathBuilder().BuildOriginalPath(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("20000000-0000-0000-0000-000000000001"),
        Guid.Parse("30000000-0000-0000-0000-000000000001"));

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StorageHandler(Func<Stream> stream) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(stream())
            });
        }
    }

    private sealed class ThrowAfterFirstReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private bool read;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read) throw new IOException("synthetic partial download failure");
            read = true;
            return ValueTask.FromResult(Read(buffer.Span[..Math.Min(16, buffer.Length)]));
        }
    }
}
