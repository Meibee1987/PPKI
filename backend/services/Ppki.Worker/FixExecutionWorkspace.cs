using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.Worker;

internal sealed class FixExecutionWorkspace : IAsyncDisposable
{
    private const int BufferSize = 81920;
    private static readonly Regex OwnedDirectoryName = new(
        "^ppki-fix-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private readonly string temporaryRoot;
    private bool disposed;
    private bool prepared;

    private FixExecutionWorkspace(string temporaryRoot, string workspacePath)
    {
        this.temporaryRoot = temporaryRoot;
        WorkspacePath = workspacePath;
    }

    internal string WorkspacePath { get; }
    internal string? WorkingFilePath { get; private set; }
    internal string? SourceSha256 { get; private set; }
    internal string? CloneSha256 { get; private set; }
    internal bool UsesExplicitUnixPermissions => !OperatingSystem.IsWindows();

    internal static FixExecutionWorkspace Create(string? temporaryRoot = null)
    {
        var root = Path.GetFullPath(temporaryRoot ?? Path.GetTempPath());
        Directory.CreateDirectory(root);
        var workspace = Path.GetFullPath(Path.Combine(root, $"ppki-fix-{Guid.NewGuid():N}"));
        if (!IsDirectChild(root, workspace) || !OwnedDirectoryName.IsMatch(Path.GetFileName(workspace)))
            throw new FixExecutionException(FixFailureCategory.TerminalInfrastructure,
                "workspace-path-invalid");
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(workspace);
        else
            Directory.CreateDirectory(workspace,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new(root, workspace);
    }

    internal async Task<string> MaterializeAsync(
        string downloadedSourcePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (prepared) throw new InvalidOperationException("The workspace is already prepared.");
        prepared = true;
        cancellationToken.ThrowIfCancellationRequested();

        SourceSha256 = await HashAsync(downloadedSourcePath, cancellationToken);
        if (!string.Equals(SourceSha256, expectedSha256, StringComparison.Ordinal))
            throw new FixExecutionException(FixFailureCategory.InvalidSource, "source-hash-mismatch");

        var workingPath = Path.Combine(WorkspacePath, $"working-{Guid.NewGuid():N}.docx");
        var destinationOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
            destinationOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        string copiedSourceSha256;
        await using (var source = new FileStream(downloadedSourcePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(workingPath, destinationOptions))
        {
            copiedSourceSha256 = await CopyAndHashAsync(source, destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        CloneSha256 = await HashAsync(workingPath, cancellationToken);
        if (!string.Equals(copiedSourceSha256, SourceSha256, StringComparison.Ordinal)
            || !string.Equals(CloneSha256, SourceSha256, StringComparison.Ordinal))
            throw new FixExecutionException(FixFailureCategory.InvalidSource,
                "fix-execution-clone-hash-mismatch");
        WorkingFilePath = workingPath;
        return workingPath;
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        if (!IsDirectChild(temporaryRoot, WorkspacePath)
            || !OwnedDirectoryName.IsMatch(Path.GetFileName(WorkspacePath)))
            throw new FixExecutionException(FixFailureCategory.TerminalInfrastructure,
                "workspace-path-invalid");
        try
        {
            if (Directory.Exists(WorkspacePath)) Directory.Delete(WorkspacePath, recursive: true);
            disposed = true;
            return ValueTask.CompletedTask;
        }
        catch (DirectoryNotFoundException)
        {
            disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
    }

    private static async Task<string> CopyAndHashAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsDirectChild(string root, string candidate) => string.Equals(
        Directory.GetParent(Path.TrimEndingDirectorySeparator(candidate))?.FullName,
        Path.TrimEndingDirectorySeparator(root),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
