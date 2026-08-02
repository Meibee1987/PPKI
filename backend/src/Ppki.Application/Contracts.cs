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

    Task DeleteAsync(
        string bucket,
        string objectPath,
        CancellationToken cancellationToken);
}

public interface IStorageObjectPathBuilder
{
    string BuildOriginalPath(Guid ownerUserId, Guid documentId, Guid documentVersionId);
    string BuildVersionPath(Guid ownerUserId, Guid documentId, Guid documentVersionId);
    string BuildAuditReportPath(Guid ownerUserId, Guid documentId, Guid auditJobId, string extension);
    void ValidateStoredPath(string bucket, string objectPath);
}

public sealed record StoredFile(
    string StorageBucket,
    string StorageKey,
    string OriginalFilename,
    string ContentType,
    long SizeBytes,
    string Sha256);
