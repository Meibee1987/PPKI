using System.Text.RegularExpressions;
using Ppki.Application;
using Ppki.Domain;

namespace Ppki.FixEngine;

public sealed record FixPlanMutationCandidate(
    Guid SourceDocumentVersionId,
    Guid ItemId,
    Guid FindingId,
    FixMode FixMode,
    FixPlanDraftPreviewItemState PreviewState,
    string PreviewReasonCode,
    RemediationCapability? Capability,
    FixOperationDraft? Operation);

public interface IFixPlanConflictAnalyzer
{
    FixPlanMutationAnalysisDto Analyze(
        Guid sourceDocumentVersionId,
        IReadOnlyList<FixPlanMutationCandidate> candidates);
}

public sealed class DeterministicFixPlanConflictAnalyzer : IFixPlanConflictAnalyzer
{
    public const string SchemaVersion = "fix-plan-mutation-analysis/1.0";
    private static readonly Regex SafeIdentifier = new(
        "^[a-z0-9][a-z0-9.-]{0,127}$", RegexOptions.CultureInvariant);

    public FixPlanMutationAnalysisDto Analyze(
        Guid sourceDocumentVersionId,
        IReadOnlyList<FixPlanMutationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var results = new Dictionary<Guid, MutableResult>();
        var valid = new List<AnalyzableCandidate>();

        foreach (var candidate in candidates.OrderBy(value => value.ItemId))
        {
            if (candidate.ItemId == Guid.Empty || candidate.FindingId == Guid.Empty
                || results.ContainsKey(candidate.ItemId))
            {
                if (candidate.ItemId != Guid.Empty)
                    results[candidate.ItemId] = Mutable(candidate, FixPlanMutationItemStatus.Unavailable,
                        "fix-mutation-analysis-identity-invalid");
                continue;
            }
            if (candidate.PreviewState != FixPlanDraftPreviewItemState.Previewable
                || candidate.Capability is null || candidate.Operation is null)
            {
                results[candidate.ItemId] = Mutable(candidate,
                    candidate.PreviewState == FixPlanDraftPreviewItemState.Ineligible
                        ? FixPlanMutationItemStatus.Ineligible : FixPlanMutationItemStatus.Unavailable,
                    candidate.PreviewReasonCode);
                continue;
            }
            if (candidate.SourceDocumentVersionId != sourceDocumentVersionId)
            {
                results[candidate.ItemId] = Mutable(candidate, FixPlanMutationItemStatus.Stale,
                    "fix-mutation-source-version-mismatch");
                continue;
            }
            if (!TryKey(sourceDocumentVersionId, candidate.Operation, out var key, out var reason))
            {
                results[candidate.ItemId] = Mutable(candidate,
                    reason == "fix-mutation-anchor-missing" ? FixPlanMutationItemStatus.Stale
                        : FixPlanMutationItemStatus.Unavailable, reason);
                continue;
            }

            var value = Mutable(candidate, FixPlanMutationItemStatus.Independent,
                "fix-mutation-independent", key);
            results.Add(candidate.ItemId, value);
            valid.Add(new(candidate, key, Canonical(key), Meaning(candidate)));
        }

        var conflicts = new List<FixPlanMutationConflictDto>();
        var relationships = new List<FixPlanMutationRelationshipDto>();
        var nodes = new List<MutationNode>();
        foreach (var group in valid.GroupBy(value => value.CanonicalKey, StringComparer.Ordinal)
                     .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var values = group.OrderBy(value => value.Candidate.ItemId).ToArray();
            if (values.Select(value => value.Meaning).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                Conflict(values, "fix-mutation-contradictory-outcome", results, conflicts, relationships);
                continue;
            }

            if (values.Length > 1)
            {
                var mergeable = values.All(value => value.Candidate.Capability!.AllowsIdenticalOperationMerge)
                    && values.Select(value => $"{value.Candidate.Capability!.CapabilityId}\n{value.Candidate.Capability.CapabilityVersion}")
                        .Distinct(StringComparer.Ordinal).Count() == 1;
                if (!mergeable)
                {
                    Conflict(values, "fix-mutation-duplicate-not-mergeable", results, conflicts, relationships);
                    continue;
                }

                var ids = values.Select(value => value.Candidate.ItemId).Order().ToArray();
                foreach (var value in values)
                {
                    var result = results[value.Candidate.ItemId];
                    result.Status = FixPlanMutationItemStatus.DuplicateEquivalent;
                    result.ReasonCode = "fix-mutation-duplicate-equivalent";
                    result.Related.UnionWith(ids.Where(id => id != value.Candidate.ItemId));
                }
                for (var left = 0; left < ids.Length; left++)
                    for (var right = left + 1; right < ids.Length; right++)
                        relationships.Add(new(ids[left], ids[right],
                            FixPlanMutationRelationshipKind.DuplicateEquivalent, null, null,
                            "fix-mutation-duplicate-equivalent"));
            }
            nodes.Add(new(values[0].CanonicalKey, values));
        }

        var edges = Dependencies(nodes, relationships);
        var cyclic = nodes.Where(node => Reaches(node, node, edges, new HashSet<string>(StringComparer.Ordinal)))
            .Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        if (cyclic.Count > 0)
        {
            var cycleItems = nodes.Where(node => cyclic.Contains(node.Key)).SelectMany(node => node.Values)
                .Select(value => value.Candidate.ItemId).Order().ToArray();
            foreach (var id in cycleItems)
            {
                results[id].Status = FixPlanMutationItemStatus.DependencyCycle;
                results[id].ReasonCode = "fix-mutation-dependency-cycle";
                results[id].Related.UnionWith(cycleItems.Where(value => value != id));
            }
            conflicts.Add(new(null, cycleItems, "fix-mutation-dependency-cycle"));
        }
        else
        {
            AssignOrder(nodes, edges, results);
        }

        var finalItems = results.Values.OrderBy(value => value.Key is null ? "~" : Canonical(value.Key))
            .ThenBy(value => value.ItemId).Select(value => value.Dto()).ToArray();
        var finalRelationships = relationships.OrderBy(value => value.ItemId).ThenBy(value => value.RelatedItemId)
            .ThenBy(value => value.Kind).ToArray();
        var finalConflicts = conflicts.OrderBy(value => value.MutationKey is null ? "~" : Canonical(value.MutationKey))
            .ThenBy(value => value.ItemIds.FirstOrDefault()).ToArray();
        var hasConflict = finalItems.Any(value => value.Status is FixPlanMutationItemStatus.Conflicting
            or FixPlanMutationItemStatus.DependencyCycle);
        var hasStale = finalItems.Any(value => value.Status == FixPlanMutationItemStatus.Stale);
        var analyzable = finalItems.Count(value => value.MutationKey is not null
            && value.Status is not (FixPlanMutationItemStatus.Conflicting or FixPlanMutationItemStatus.DependencyCycle));
        var unavailable = finalItems.Any(value => value.Status is FixPlanMutationItemStatus.Unavailable
            or FixPlanMutationItemStatus.Ineligible);
        var state = hasConflict ? FixPlanMutationAnalysisState.Conflict
            : hasStale ? FixPlanMutationAnalysisState.Stale
            : analyzable == 0 ? FixPlanMutationAnalysisState.Unavailable
            : unavailable ? FixPlanMutationAnalysisState.PartiallyAvailable
            : FixPlanMutationAnalysisState.Ready;
        var reasons = finalItems.Select(value => value.ReasonCode)
            .Concat(finalConflicts.Select(value => value.ReasonCode))
            .Concat(finalRelationships.Select(value => value.ReasonCode))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        return new(SchemaVersion, state, analyzable,
            finalItems.Count(value => value.Status == FixPlanMutationItemStatus.Independent),
            finalItems.Count(value => value.Status == FixPlanMutationItemStatus.Ordered),
            finalItems.Count(value => value.Status == FixPlanMutationItemStatus.DuplicateEquivalent),
            finalItems.Count(value => value.Status is FixPlanMutationItemStatus.Conflicting or FixPlanMutationItemStatus.DependencyCycle),
            finalItems.Count(value => value.Status == FixPlanMutationItemStatus.Stale),
            finalItems, finalRelationships, finalConflicts, reasons);
    }

    private static IReadOnlyDictionary<string, HashSet<string>> Dependencies(
        IReadOnlyList<MutationNode> nodes,
        ICollection<FixPlanMutationRelationshipDto> relationships)
    {
        var edges = nodes.ToDictionary(value => value.Key,
            _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var source in nodes)
        foreach (var requirement in source.Values[0].Candidate.Capability!.Dependencies ?? [])
        foreach (var target in nodes.Where(value => value.Key != source.Key
                     && value.Values[0].Candidate.Capability!.CapabilityId == requirement.CapabilityId
                     && value.Values[0].Candidate.Capability!.CapabilityVersion == requirement.CapabilityVersion))
        {
            var before = requirement.Kind == FixCapabilityDependencyKind.RequiresBefore ? source : target;
            var after = requirement.Kind == FixCapabilityDependencyKind.RequiresBefore ? target : source;
            if (!edges[before.Key].Add(after.Key)) continue;
            foreach (var sourceValue in source.Values)
            foreach (var targetValue in target.Values)
                relationships.Add(new(sourceValue.Candidate.ItemId, targetValue.Candidate.ItemId,
                    requirement.Kind == FixCapabilityDependencyKind.RequiresBefore
                        ? FixPlanMutationRelationshipKind.RequiresBefore
                        : FixPlanMutationRelationshipKind.RequiresAfter,
                    requirement.Kind == FixCapabilityDependencyKind.RequiresBefore
                        ? sourceValue.Candidate.ItemId : targetValue.Candidate.ItemId,
                    requirement.Kind == FixCapabilityDependencyKind.RequiresBefore
                        ? targetValue.Candidate.ItemId : sourceValue.Candidate.ItemId,
                    requirement.Kind == FixCapabilityDependencyKind.RequiresBefore
                        ? "fix-mutation-requires-before" : "fix-mutation-requires-after"));
        }
        return edges;
    }

    private static void AssignOrder(IReadOnlyList<MutationNode> nodes,
        IReadOnlyDictionary<string, HashSet<string>> edges,
        IReadOnlyDictionary<Guid, MutableResult> results)
    {
        var indegree = nodes.ToDictionary(value => value.Key, _ => 0, StringComparer.Ordinal);
        foreach (var targets in edges.Values)
        foreach (var target in targets) indegree[target]++;
        var ready = new SortedSet<string>(indegree.Where(value => value.Value == 0).Select(value => value.Key), StringComparer.Ordinal);
        var ordinal = 0;
        while (ready.Count > 0)
        {
            var key = ready.Min!; ready.Remove(key);
            var node = nodes.Single(value => value.Key == key);
            ordinal++;
            var dependent = edges[key].Count > 0 || edges.Values.Any(value => value.Contains(key));
            foreach (var value in node.Values)
            {
                var result = results[value.Candidate.ItemId];
                result.Ordinal = ordinal;
                if (dependent && result.Status == FixPlanMutationItemStatus.Independent)
                {
                    result.Status = FixPlanMutationItemStatus.Ordered;
                    result.ReasonCode = "fix-mutation-dependency-ordered";
                }
            }
            foreach (var target in edges[key].Order(StringComparer.Ordinal))
                if (--indegree[target] == 0) ready.Add(target);
        }
    }

    private static bool Reaches(MutationNode start, MutationNode current,
        IReadOnlyDictionary<string, HashSet<string>> edges, ISet<string> visited)
    {
        foreach (var target in edges[current.Key])
        {
            if (target == start.Key) return true;
            if (visited.Add(target) && Reaches(start,
                    new(target, []), edges, visited)) return true;
        }
        return false;
    }

    private static void Conflict(IReadOnlyList<AnalyzableCandidate> values, string reason,
        IReadOnlyDictionary<Guid, MutableResult> results,
        ICollection<FixPlanMutationConflictDto> conflicts,
        ICollection<FixPlanMutationRelationshipDto> relationships)
    {
        var ids = values.Select(value => value.Candidate.ItemId).Order().ToArray();
        foreach (var value in values)
        {
            var result = results[value.Candidate.ItemId];
            result.Status = FixPlanMutationItemStatus.Conflicting;
            result.ReasonCode = reason;
            result.Related.UnionWith(ids.Where(id => id != value.Candidate.ItemId));
        }
        for (var left = 0; left < ids.Length; left++)
            for (var right = left + 1; right < ids.Length; right++)
                relationships.Add(new(ids[left], ids[right], FixPlanMutationRelationshipKind.Conflicting,
                    null, null, reason));
        conflicts.Add(new(values[0].Key, ids, reason));
    }

    private static bool TryKey(Guid sourceDocumentVersionId, FixOperationDraft operation,
        out FixPlanMutationKeyDto key, out string reason)
    {
        key = null!; reason = "fix-mutation-target-unsupported";
        if (operation.Target is null || operation.PropertyIdentifier is null
            || operation.Expected is null) return false;
        var target = operation.Target;
        if (!NonNegative(target.BodyElementIndex) || !NonNegative(target.SectionIndex)
            || !NonNegative(target.ParagraphIndex) || !NonNegative(target.RunIndex)
            || !SafeIdentifier.IsMatch(operation.PropertyIdentifier)) return false;
        var knownScope = target.Scope is "main-document-section" or "main-document-paragraph" or "main-document-run";
        var valid = target.Scope switch
        {
            "main-document-section" => target.SectionIndex is not null
                && target.ParagraphIndex is null && target.RunIndex is null,
            "main-document-paragraph" => target.BodyElementIndex is not null
                && target.ParagraphIndex is not null && target.RunIndex is null,
            "main-document-run" => target.BodyElementIndex is not null
                && target.ParagraphIndex is not null && target.RunIndex is not null,
            _ => false
        };
        if (!valid)
        {
            reason = knownScope ? "fix-mutation-anchor-missing" : "fix-mutation-target-unsupported";
            return false;
        }
        if (!ValidExpected(operation.Expected)
            || !SafeIdentifier.IsMatch(operation.PreconditionCode ?? string.Empty)) return false;
        key = new(sourceDocumentVersionId, target.Scope, target.BodyElementIndex, target.SectionIndex,
            target.ParagraphIndex, target.RunIndex, operation.PropertyIdentifier);
        return true;
    }

    private static bool NonNegative(int? value) => value is null or >= 0;
    private static bool ValidExpected(FixExpectedValueDescriptor value)
    {
        if (value.Value is not { Length: > 0 and <= 128 }
            || value.Value.Any(char.IsControl)) return false;
        return value.Type switch
        {
            "boolean" => value.Value is "true" or "false",
            "integer" or "twips" or "half-points" => long.TryParse(value.Value,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "decimal" => decimal.TryParse(value.Value,
                System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "string-code" => true,
            "enum-code" => SafeIdentifier.IsMatch(value.Value),
            _ => false
        };
    }
    private static string Meaning(FixPlanMutationCandidate value) => string.Join("\n",
        value.Capability!.OperationKind, value.Operation!.Expected.Type,
        value.Operation.Expected.Value, value.Operation.PreconditionCode);
    private static string Canonical(FixPlanMutationKeyDto value) => string.Join("/",
        value.SourceDocumentVersionId.ToString("D"), value.Scope,
        Number(value.BodyElementIndex), Number(value.SectionIndex), Number(value.ParagraphIndex),
        Number(value.RunIndex), value.PropertyIdentifier);
    private static string Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
    private static MutableResult Mutable(FixPlanMutationCandidate value, FixPlanMutationItemStatus status,
        string reason, FixPlanMutationKeyDto? key = null) => new(value.ItemId, value.FindingId, status, reason, key);

    private sealed record AnalyzableCandidate(FixPlanMutationCandidate Candidate,
        FixPlanMutationKeyDto Key, string CanonicalKey, string Meaning);
    private sealed record MutationNode(string Key, IReadOnlyList<AnalyzableCandidate> Values);
    private sealed class MutableResult(Guid itemId, Guid findingId, FixPlanMutationItemStatus status,
        string reasonCode, FixPlanMutationKeyDto? key)
    {
        public Guid ItemId { get; } = itemId;
        public Guid FindingId { get; } = findingId;
        public FixPlanMutationItemStatus Status { get; set; } = status;
        public string ReasonCode { get; set; } = reasonCode;
        public FixPlanMutationKeyDto? Key { get; } = key;
        public int? Ordinal { get; set; }
        public SortedSet<Guid> Related { get; } = [];
        public FixPlanMutationAnalysisItemDto Dto() => new(ItemId, FindingId, Status, ReasonCode,
            Key, Ordinal, Related.ToArray());
    }
}
