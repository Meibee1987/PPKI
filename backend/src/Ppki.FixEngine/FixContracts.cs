namespace Ppki.FixEngine;

public sealed record ApprovedFix(Guid FindingId, Guid ApprovedByUserId);

public sealed record FixResult(
    bool Applied,
    string Message,
    object? Before,
    object? After);

public interface IFixEngine
{
    Task<FixResult> ApplyAsync(ApprovedFix fix, CancellationToken cancellationToken);
}

public sealed class NotImplementedFixEngine : IFixEngine
{
    public Task<FixResult> ApplyAsync(ApprovedFix fix, CancellationToken cancellationToken) =>
        Task.FromResult(new FixResult(
            false,
            "Fix engine is scaffolded but not enabled in the first vertical slice.",
            null,
            null));
}
