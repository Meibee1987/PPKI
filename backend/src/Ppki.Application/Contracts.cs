namespace Ppki.Application;

public interface IFileStorage
{
    Task<StoredFile> SaveAsync(
        Stream source,
        string originalFilename,
        string contentType,
        string bucket,
        string objectPath,
        CancellationToken cancellationToken);

    Task<string> MaterializeToTempFileAsync(
        string bucket,
        string objectPath,
        CancellationToken cancellationToken);

    Task<string> CreateSignedDownloadUrlAsync(
        string bucket,
        string objectPath,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    Task<byte[]> ReadBytesAsync(
        string bucket,
        string objectPath,
        long maximumBytes,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string bucket,
        string objectPath,
        CancellationToken cancellationToken);
}

public enum FileStorageFailureKind
{
    NotFound,
    Conflict,
    Transient,
    Terminal,
    SizeLimit
}

public sealed class FileStorageException(FileStorageFailureKind kind, Exception? innerException = null)
    : Exception("Storage operation failed.", innerException)
{
    public FileStorageFailureKind Kind { get; } = kind;
}

public interface IStorageObjectPathBuilder
{
    string BuildOriginalPath(Guid ownerUserId, Guid documentId, Guid documentVersionId);
    string BuildVersionPath(Guid ownerUserId, Guid documentId, Guid documentVersionId);
    string BuildAuditReportPath(Guid ownerUserId, Guid documentId, Guid auditJobId, string extension);
    string BuildDocumentPreviewPath(Guid ownerUserId, Guid documentId, Guid renderJobId);
    void ValidateStoredPath(string bucket, string objectPath);
}

public sealed record StoredFile(
    string StorageBucket,
    string StorageKey,
    string OriginalFilename,
    string ContentType,
    long SizeBytes,
    string Sha256);
