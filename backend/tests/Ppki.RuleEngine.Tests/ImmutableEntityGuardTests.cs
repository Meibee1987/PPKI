using Microsoft.EntityFrameworkCore;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;

namespace Ppki.RuleEngine.Tests;

public sealed class ImmutableEntityGuardTests
{
    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task Document_version_update_and_delete_are_blocked_before_database_access(EntityState state)
    {
        await using var db = Context();
        var version = Version();
        db.Attach(version);
        db.Entry(version).State = state;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Document versions are insert-only.", error.Message);
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task Rule_snapshot_update_and_delete_are_blocked_before_database_access(EntityState state)
    {
        await using var db = Context();
        var snapshot = Snapshot();
        db.Attach(snapshot);
        db.Entry(snapshot).State = state;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Audit rule snapshots are insert-only.", error.Message);
    }

    private static PpkiDbContext Context() => new(new DbContextOptionsBuilder<PpkiDbContext>()
        .UseNpgsql("Host=localhost;Database=immutable_offline_test")
        .Options);

    private static DocumentVersion Version() => new()
    {
        DocumentId = Guid.NewGuid(),
        VersionNo = 1,
        StorageBucket = "documents-original",
        StorageKey = "owner/document/version/original.docx",
        OriginalFilename = "original.docx",
        MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        SizeBytes = 1,
        Sha256 = new string('a', 64),
        CreatedByUserId = Guid.NewGuid()
    };

    private static AuditRuleSnapshot Snapshot() => new()
    {
        AuditJobId = Guid.NewGuid(),
        RuleId = Guid.NewGuid(),
        RuleCode = "RULE-A",
        Domain = "Layout",
        AppliesTo = "Document",
        Element = "Page",
        RequirementJson = "{}",
        ValidationKey = "section.page-size-a4",
        ValidationJson = "{}",
        Severity = RuleSeverity.Error,
        FixMode = FixMode.Report,
        SourceReferenceJson = "{}",
        Layer = "profile",
        Ordinal = 1,
        SnapshotSchemaVersion = 1
    };
}
