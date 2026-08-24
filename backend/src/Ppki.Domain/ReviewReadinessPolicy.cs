namespace Ppki.Domain;

/// <summary>
/// Versioned PPKI Smart Formatter product policy used to decide review
/// readiness. It is deliberately separate from official PPKI/IPB source data.
/// </summary>
public static class ReviewReadinessPolicy
{
    public const string Version = "ppki-ipb-2019-review-readiness-v1";
    public const string Authority = "PPKI Smart Formatter product policy";

    public static ReviewBlockingPolicy ParseCatalogValue(string? value) => value switch
    {
        "Blocking" => ReviewBlockingPolicy.Blocking,
        "NonBlocking" => ReviewBlockingPolicy.NonBlocking,
        "PendingApproval" => ReviewBlockingPolicy.PendingApproval,
        _ => throw new InvalidOperationException("Rule review_blocking_policy must be exactly Blocking, NonBlocking, or PendingApproval.")
    };

    public static bool IsPolicyAwareSnapshot(AuditRuleSnapshot snapshot) =>
        snapshot.SnapshotSchemaVersion >= 2
        && snapshot.ReviewBlockingPolicy is ReviewBlockingPolicy.Blocking or ReviewBlockingPolicy.NonBlocking
        && !string.IsNullOrWhiteSpace(snapshot.ReadinessPolicyVersion);
}
