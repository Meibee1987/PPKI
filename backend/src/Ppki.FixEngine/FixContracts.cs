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
    IReadOnlySet<string> ValidationKeys { get; }
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
                || !Identifier.IsMatch(value.CapabilityVersion ?? string.Empty)
                || value.ValidationKeys is null || value.ValidationKeys.Count == 0
                || value.ValidationKeys.Any(key => !Identifier.IsMatch(key ?? string.Empty)))
            || supplied.GroupBy(ProviderKey, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || supplied.SelectMany(provider => provider.ValidationKeys.Select(validationKey => new { provider, validationKey }))
                .GroupBy(value => Key(value.validationKey, value.provider.CapabilityId, value.provider.CapabilityVersion),
                    StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new FixPlanConfigurationException("fix-apply-capability-configuration-invalid");
        Providers = Array.AsReadOnly(supplied);
        providers = supplied.SelectMany(provider => provider.ValidationKeys.Select(validationKey =>
                new KeyValuePair<string, IFixApplyProvider>(
                    Key(validationKey, provider.CapabilityId, provider.CapabilityVersion), provider)))
            .ToDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyList<IFixApplyProvider> Providers { get; }

    public bool CanApply(FixPlanOperation operation) => providers.ContainsKey(Key(operation));

    public bool TryGet(FixPlanOperation operation, out IFixApplyProvider provider) =>
        providers.TryGetValue(Key(operation), out provider!);

    public FixApplyProviderAvailability GetAvailability(
        string validationKey, string capabilityId, string capabilityVersion)
    {
        if (providers.ContainsKey(Key(validationKey, capabilityId, capabilityVersion)))
            return FixApplyProviderAvailability.Available;
        return Providers.Any(value => value.ValidationKeys.Contains(validationKey)
                && string.Equals(value.CapabilityId, capabilityId, StringComparison.Ordinal))
            ? FixApplyProviderAvailability.VersionIncompatible
            : FixApplyProviderAvailability.NotRegistered;
    }

    private static string Key(FixPlanOperation operation) =>
        Key(operation.ValidationKey, operation.CapabilityId, operation.CapabilityVersion);
    private static string ProviderKey(IFixApplyProvider provider) =>
        $"{provider.CapabilityId}\n{provider.CapabilityVersion}";
    private static string Key(string validationKey, string capabilityId, string capabilityVersion) =>
        $"{validationKey}\n{capabilityId}\n{capabilityVersion}";
}

public enum FixApplyProviderAvailability
{
    Available,
    NotRegistered,
    VersionIncompatible
}
