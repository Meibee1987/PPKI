using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class AuditTrailTests
{
    [Fact]
    public void Registered_actions_and_resource_types_are_stable_lowercase_values()
    {
        Assert.Contains(AuditActions.DocumentCreated, AuditActions.All);
        Assert.Contains(AuditActions.AuditCompleted, AuditActions.All);
        Assert.All(AuditActions.All, action => Assert.Matches("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", action));
        Assert.Contains(AuditResourceTypes.DocumentVersion, AuditResourceTypes.All);
        Assert.All(AuditResourceTypes.All, resource => Assert.Matches("^[a-z][a-z0-9_]*$", resource));
    }

    [Fact]
    public void Event_rejects_unregistered_action_and_resource_type()
    {
        var context = AuditEventContext.System(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => AuditTrailEvent.Create(context, new AuditEventData("arbitrary.action", AuditResourceTypes.Document, null, null, AuditEventMetadata.Empty)));
        Assert.Throws<ArgumentException>(() => AuditTrailEvent.Create(context, new AuditEventData(AuditActions.DocumentCreated, "arbitrary_resource", null, null, AuditEventMetadata.Empty)));
    }

    [Fact]
    public void Actor_contract_requires_trusted_user_or_allowed_service_identity()
    {
        var userId = Guid.NewGuid();
        var user = AuditEventContext.User(userId, Guid.NewGuid());
        var worker = AuditEventContext.Service("worker", Guid.NewGuid());

        Assert.Equal(userId, user.ActorUserId);
        Assert.Null(user.ActorService);
        Assert.Equal("worker", worker.ActorService);
        Assert.Null(worker.ActorUserId);
        Assert.Throws<ArgumentException>(() => AuditEventContext.User(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => AuditEventContext.Service("browser", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => AuditEventContext.System(Guid.Empty));
    }

    [Fact]
    public void Metadata_must_be_an_object_and_rejects_every_forbidden_key()
    {
        Assert.Throws<ArgumentException>(() => AuditEventMetadata.FromJson("[]"));
        foreach (var key in new[] { "token", "secret", "connectionString", "signedUrl", "storagePath", "documentText", "exception", "stackTrace" })
        {
            Assert.Throws<ArgumentException>(() => AuditEventMetadata.FromJson($"{{\"{key}\":\"forbidden\"}}"));
        }
    }

    [Fact]
    public void Safe_scalar_metadata_is_accepted()
    {
        var metadata = AuditEventMetadata.FromJson("{\"finding_count\":2,\"audit_status\":\"Completed\",\"mime_type\":null}");
        Assert.Contains("finding_count", metadata.Json, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => AuditEventMetadata.FromJson("{\"finding_count\":{\"nested\":2}}"));
    }

    [Fact]
    public void Event_actor_comes_from_context_and_correlation_is_required()
    {
        var actor = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var context = AuditEventContext.User(actor, correlation);
        var data = new AuditEventData(AuditActions.AuditRequested, AuditResourceTypes.AuditJob, Guid.NewGuid(), actor, AuditEventMetadata.Empty);
        var auditEvent = AuditTrailEvent.Create(context, data);

        Assert.Equal(actor, auditEvent.ActorUserId);
        Assert.Equal(correlation, auditEvent.CorrelationId);
        Assert.DoesNotContain(typeof(AuditEventData).GetProperties(), property => property.Name.Contains("Actor", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => AuditEventContext.User(actor, Guid.Empty));
    }

    [Fact]
    public void Failure_metadata_only_accepts_generic_category()
    {
        var metadata = AuditEventMetadata.Create(("failure_category", "processing_error"));
        Assert.Contains("processing_error", metadata.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("exception", metadata.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", metadata.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audit_event_has_no_public_mutation_surface_and_ef_has_no_cascade_relationship()
    {
        var mutableProperties = typeof(AuditTrailEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod?.IsPublic == true)
            .ToArray();
        Assert.Empty(mutableProperties);

        using var db = Context();
        var entity = db.Model.FindEntityType(typeof(AuditTrailEvent))!;
        Assert.DoesNotContain(entity.GetForeignKeys(), foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task Ef_guard_rejects_event_update_and_delete_before_database_access(EntityState state)
    {
        await using var db = Context();
        var context = AuditEventContext.System(Guid.NewGuid());
        var auditEvent = AuditTrailEvent.Create(context, new AuditEventData(AuditActions.DocumentCreated, AuditResourceTypes.Document, null, null, AuditEventMetadata.Empty));
        db.Attach(auditEvent);
        db.Entry(auditEvent).State = state;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Audit trail events are append-only.", error.Message);
    }

    [Fact]
    public void Worker_uses_audit_id_as_one_correlation_and_terminal_trigger_is_idempotent()
    {
        var root = RepositoryRoot();
        var runner = File.ReadAllText(Path.Combine(root, "backend", "src", "Ppki.RuleEngine", "AuditRunner.cs"));
        var worker = File.ReadAllText(Path.Combine(root, "backend", "services", "Ppki.Worker", "QueuedAuditWorker.cs"));
        var migration = File.ReadAllText(Path.Combine(root, "supabase", "migrations", "202608030001_append_only_audit_trail.sql"));

        Assert.Contains("AuditEventContext.Service(\"worker\", auditJobId)", runner, StringComparison.Ordinal);
        Assert.Contains("AuditEventContext.Service(\"worker\", queuedId.Value)", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid.NewGuid", runner, StringComparison.Ordinal);
        Assert.Contains("uq_audit_trail_semantic_event", migration, StringComparison.Ordinal);
        Assert.Contains("old.status is distinct from new.status", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Api_orphan_cleanup_uses_api_service_actor_and_request_correlation()
    {
        var api = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "backend", "services", "Ppki.Api", "Program.cs"));

        Assert.Contains("AuditEventContext.Service(\"api\",context.CorrelationId", api, StringComparison.Ordinal);
        Assert.Contains("AuditActions.StorageOrphanCleanup", api, StringComparison.Ordinal);
    }

    private static PpkiDbContext Context() => new(new DbContextOptionsBuilder<PpkiDbContext>()
        .UseNpgsql("Host=localhost;Database=audit_trail_offline_test")
        .Options);

    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Directory.GetCurrentDirectory()); candidate is not null; candidate = candidate.Parent)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, "supabase", "migrations"))) return candidate.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
