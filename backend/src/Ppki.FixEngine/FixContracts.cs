using Ppki.Application;
using Ppki.DocxEngine;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;

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

public sealed record FixApplyContext(
    string WorkingFilePath,
    ParsedDocument SourceDocument,
    FixPlanFindingSnapshot Finding,
    FixPlanOperation Operation,
    WordprocessingDocument? OpenPackage = null);

public enum FixApplyOutcome { Changed, NoChange }

public interface IFixApplyProvider
{
    string CapabilityId { get; }
    string CapabilityVersion { get; }
    Task<FixApplyOutcome> ApplyAsync(FixApplyContext context, CancellationToken cancellationToken);
}

public sealed class FixApplyCapabilityRegistry : IFixApplyCapabilityResolver
{
    private static readonly Regex Identifier = new("^[a-z0-9][a-z0-9.-]{0,127}$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, IFixApplyProvider> providers;

    public FixApplyCapabilityRegistry(IEnumerable<IFixApplyProvider> values)
    {
        var supplied = values.OrderBy(value => value.CapabilityId, StringComparer.Ordinal)
            .ThenBy(value => value.CapabilityVersion, StringComparer.Ordinal).ToArray();
        if (supplied.Any(value => !Identifier.IsMatch(value.CapabilityId ?? string.Empty)
                || !Identifier.IsMatch(value.CapabilityVersion ?? string.Empty))
            || supplied.GroupBy(Key, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new FixPlanConfigurationException("fix-apply-capability-configuration-invalid");
        Providers = Array.AsReadOnly(supplied);
        providers = supplied.ToDictionary(Key, StringComparer.Ordinal);
    }

    public IReadOnlyList<IFixApplyProvider> Providers { get; }

    public bool CanApply(FixPlanOperation operation) => providers.ContainsKey(Key(operation));

    public bool TryGet(FixPlanOperation operation, out IFixApplyProvider provider) =>
        providers.TryGetValue(Key(operation), out provider!);

    private static string Key(IFixApplyProvider provider) => $"{provider.CapabilityId}\n{provider.CapabilityVersion}";
    private static string Key(FixPlanOperation operation) => $"{operation.CapabilityId}\n{operation.CapabilityVersion}";
}
