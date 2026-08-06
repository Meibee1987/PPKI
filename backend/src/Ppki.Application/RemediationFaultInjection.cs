namespace Ppki.Application;

public enum RemediationCheckpoint
{
    AfterClaim,
    BeforeSourceDownload,
    AfterSourceDownload,
    BeforeApply,
    AfterApply,
    BeforeResultUpload,
    AfterResultUpload,
    BeforeDatabaseFinalization,
    AfterDatabaseFinalization,
    BeforeOrphanCleanup
}

public interface IRemediationFaultInjector
{
    ValueTask CheckpointAsync(RemediationCheckpoint checkpoint, Guid executionId,
        int attemptNumber, CancellationToken cancellationToken);
}

public sealed class NoopRemediationFaultInjector : IRemediationFaultInjector
{
    public ValueTask CheckpointAsync(RemediationCheckpoint checkpoint, Guid executionId,
        int attemptNumber, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
