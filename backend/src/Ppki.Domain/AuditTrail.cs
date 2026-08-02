using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ppki.Domain;

public enum AuditActorType
{
    User,
    Service,
    System
}

public enum AuditEventSource
{
    Application,
    DatabaseTrigger
}

public static class AuditActions
{
    public const string DocumentCreated = "document.created";
    public const string DocumentStatusChanged = "document.status_changed";
    public const string DocumentVersionCreated = "document.version_created";
    public const string DocumentUploadCompleted = "document.upload_completed";
    public const string DocumentDownloadAuthorized = "document.download_authorized";
    public const string StorageOrphanCleanup = "storage.orphan_cleanup";
    public const string AuditRequested = "audit.requested";
    public const string AuditProcessingStarted = "audit.processing_started";
    public const string AuditRuleSnapshotCreated = "audit.rule_snapshot_created";
    public const string AuditCompleted = "audit.completed";
    public const string AuditFailed = "audit.failed";
    public const string AuditCancelled = "audit.cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentCreated,
        DocumentStatusChanged,
        DocumentVersionCreated,
        DocumentUploadCompleted,
        DocumentDownloadAuthorized,
        StorageOrphanCleanup,
        AuditRequested,
        AuditProcessingStarted,
        AuditRuleSnapshotCreated,
        AuditCompleted,
        AuditFailed,
        AuditCancelled
    };
}

public static class AuditResourceTypes
{
    public const string Document = "document";
    public const string DocumentVersion = "document_version";
    public const string AuditJob = "audit_job";
    public const string AuditRuleSnapshot = "audit_rule_snapshot";
    public const string AuditFinding = "audit_finding";
    public const string StorageObject = "storage_object";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Document,
        DocumentVersion,
        AuditJob,
        AuditRuleSnapshot,
        AuditFinding,
        StorageObject
    };
}

public sealed class AuditEventMetadata
{
    private static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "version_number",
        "previous_status",
        "new_status",
        "audit_status",
        "applicable_rule_count",
        "finding_count",
        "file_size_bytes",
        "mime_type",
        "failure_category",
        "cleanup_reason",
        "download_kind"
    };

    private AuditEventMetadata(string json) => Json = json;

    public string Json { get; }

    public static AuditEventMetadata Empty { get; } = new("{}");

    public static AuditEventMetadata Create(params (string Key, object? Value)[] values)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (!dictionary.TryAdd(key, value)) throw new ArgumentException("Audit metadata keys must be unique.", nameof(values));
        }
        return FromJson(JsonSerializer.Serialize(dictionary));
    }

    public static AuditEventMetadata FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Audit metadata must be a JSON object.", nameof(json));
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!AllowedKeys.Contains(property.Name))
            {
                throw new ArgumentException("Audit metadata contains a forbidden key.", nameof(json));
            }

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
            {
                throw new ArgumentException("Audit metadata values must be scalar.", nameof(json));
            }
        }

        return new AuditEventMetadata(document.RootElement.GetRawText());
    }
}

public sealed record AuditEventContext
{
    private static readonly IReadOnlySet<string> AllowedServices = new HashSet<string>(StringComparer.Ordinal)
    {
        "api", "worker", "database", "maintenance"
    };
    private static readonly Regex SafeRequestId = new("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant);

    private AuditEventContext(
        AuditActorType actorType,
        Guid? actorUserId,
        string? actorService,
        Guid correlationId,
        Guid? causationId,
        string? requestId)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("Correlation ID is required.", nameof(correlationId));
        if (causationId == Guid.Empty) throw new ArgumentException("Causation ID cannot be empty.", nameof(causationId));
        if (requestId is not null && !SafeRequestId.IsMatch(requestId)) throw new ArgumentException("Request ID is invalid.", nameof(requestId));
        if (actorType == AuditActorType.User && (actorUserId is null || actorUserId == Guid.Empty || actorService is not null))
            throw new ArgumentException("User audit actors require only an actor user ID.");
        if (actorType == AuditActorType.Service && (actorUserId is not null || actorService is null || !AllowedServices.Contains(actorService)))
            throw new ArgumentException("Service audit actors require an allowed service.");
        if (actorType == AuditActorType.System && (actorUserId is not null || actorService is not null))
            throw new ArgumentException("System audit actors cannot identify a user or service.");

        ActorType = actorType;
        ActorUserId = actorUserId;
        ActorService = actorService;
        CorrelationId = correlationId;
        CausationId = causationId;
        RequestId = requestId;
    }

    public AuditActorType ActorType { get; }
    public Guid? ActorUserId { get; }
    public string? ActorService { get; }
    public Guid CorrelationId { get; }
    public Guid? CausationId { get; }
    public string? RequestId { get; }

    public static AuditEventContext User(Guid actorUserId, Guid correlationId, Guid? causationId = null, string? requestId = null) =>
        new(AuditActorType.User, actorUserId, null, correlationId, causationId, requestId);

    public static AuditEventContext Service(string actorService, Guid correlationId, Guid? causationId = null) =>
        new(AuditActorType.Service, null, actorService, correlationId, causationId, null);

    public static AuditEventContext System(Guid correlationId, Guid? causationId = null) =>
        new(AuditActorType.System, null, null, correlationId, causationId, null);
}

public sealed record AuditEventData(
    string Action,
    string ResourceType,
    Guid? ResourceId,
    Guid? OwnerUserId,
    AuditEventMetadata Metadata);

public sealed class AuditTrailEvent
{
    private AuditTrailEvent() { }

    public Guid Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public AuditActorType ActorType { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string? ActorService { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string ResourceType { get; private set; } = string.Empty;
    public Guid? ResourceId { get; private set; }
    public Guid? OwnerUserId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public Guid? CausationId { get; private set; }
    public string? RequestId { get; private set; }
    public string MetadataJson { get; private set; } = "{}";
    public int EventSchemaVersion { get; private set; }
    public AuditEventSource EventSource { get; private set; }

    public static AuditTrailEvent Create(AuditEventContext context, AuditEventData data)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(data);
        if (!AuditActions.All.Contains(data.Action)) throw new ArgumentException("Audit action is not registered.", nameof(data));
        if (!AuditResourceTypes.All.Contains(data.ResourceType)) throw new ArgumentException("Audit resource type is not registered.", nameof(data));
        if (data.ResourceId is null && context.ActorType != AuditActorType.System)
            throw new ArgumentException("Runtime audit events require a resource ID.", nameof(data));

        return new AuditTrailEvent
        {
            Id = Guid.NewGuid(),
            ActorType = context.ActorType,
            ActorUserId = context.ActorUserId,
            ActorService = context.ActorService,
            Action = data.Action,
            ResourceType = data.ResourceType,
            ResourceId = data.ResourceId,
            OwnerUserId = data.OwnerUserId,
            CorrelationId = context.CorrelationId,
            CausationId = context.CausationId,
            RequestId = context.RequestId,
            MetadataJson = data.Metadata.Json,
            EventSchemaVersion = 1,
            EventSource = AuditEventSource.Application
        };
    }
}
