using Microsoft.EntityFrameworkCore;
using Ppki.Domain;
using Ppki.Infrastructure;
using Xunit;
using PreviewFixPlanItem = Ppki.Application.FixPlanItem;
using PreviewFixPlanState = Ppki.Application.FixPlanState;

namespace Ppki.RuleEngine.Tests;

public sealed class FixPlanRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Draft_plan_binds_one_audit_and_version()
    {
        var audit = Audit();
        var plan = FixPlanRecord.Create(audit, Id(10), Now);

        Assert.Equal(audit.Id, plan.SourceAuditJobId);
        Assert.Equal(audit.DocumentVersionId, plan.SourceDocumentVersionId);
        Assert.Equal(FixPlanLifecycleState.Draft, plan.State);
        Assert.Equal(Now, plan.CreatedAt);
        Assert.Equal(Now, plan.UpdatedAt);
    }

    [Fact]
    public void Multiple_findings_from_same_lineage_are_accepted()
    {
        var audit = Audit();
        var plan = FixPlanRecord.Create(audit, Id(10), Now);

        plan.AddItem(Finding(audit, 1), Now.AddMinutes(1));
        plan.AddItem(Finding(audit, 2), Now.AddMinutes(2));

        Assert.Equal(2, plan.Items.Count);
    }

    [Fact]
    public void Duplicate_finding_is_rejected()
    {
        var audit = Audit();
        var finding = Finding(audit, 1);
        var plan = FixPlanRecord.Create(audit, Id(10), Now);
        plan.AddItem(finding, Now.AddMinutes(1));

        var error = Assert.Throws<InvalidOperationException>(() => plan.AddItem(finding, Now.AddMinutes(2)));
        Assert.Contains("only once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finding_from_another_audit_is_rejected()
    {
        var source = Audit(1, 1);
        var foreign = Audit(2, 1);
        var plan = FixPlanRecord.Create(source, Id(10), Now);

        var error = Assert.Throws<InvalidOperationException>(() => plan.AddItem(Finding(foreign, 1), Now));
        Assert.Contains("another audit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finding_from_another_document_version_is_rejected()
    {
        var source = Audit(1, 1);
        var inconsistentAudit = Audit(1, 2);
        var plan = FixPlanRecord.Create(source, Id(10), Now);

        var error = Assert.Throws<InvalidOperationException>(() => plan.AddItem(Finding(inconsistentAudit, 1), Now));
        Assert.Contains("another document version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_audit_object_is_required() =>
        Assert.Throws<ArgumentNullException>(() => FixPlanRecord.Create(null!, Id(10), Now));

    [Fact]
    public void Source_audit_identity_is_required()
    {
        var audit = Audit();
        audit.Id = Guid.Empty;
        Assert.Throws<ArgumentException>(() => FixPlanRecord.Create(audit, Id(10), Now));
    }

    [Fact]
    public void Source_document_version_is_required()
    {
        var audit = Audit();
        audit.DocumentVersionId = Guid.Empty;
        Assert.Throws<ArgumentException>(() => FixPlanRecord.Create(audit, Id(10), Now));
    }

    [Fact]
    public void Owner_is_required() =>
        Assert.Throws<ArgumentException>(() => FixPlanRecord.Create(Audit(), Guid.Empty, Now));

    [Fact]
    public void Draft_has_nullable_approval_metadata()
    {
        var plan = Plan();
        Assert.Null(plan.ApproverUserId);
        Assert.Null(plan.ApprovedAt);
    }

    [Fact]
    public void Approval_requires_approver_and_sets_metadata()
    {
        var plan = Plan();
        Assert.Throws<ArgumentException>(() => plan.Approve(Guid.Empty, Now.AddMinutes(1)));

        plan.Approve(Id(11), Now.AddMinutes(1));
        Assert.Equal(FixPlanLifecycleState.Approved, plan.State);
        Assert.Equal(Id(11), plan.ApproverUserId);
        Assert.Equal(Now.AddMinutes(1), plan.ApprovedAt);
    }

    [Fact]
    public async Task Source_audit_is_immutable_in_persistence_guard() =>
        await AssertBlockedIdentityChange(nameof(FixPlanRecord.SourceAuditJobId), Id(90));

    [Fact]
    public async Task Source_version_is_immutable_in_persistence_guard() =>
        await AssertBlockedIdentityChange(nameof(FixPlanRecord.SourceDocumentVersionId), Id(91));

    [Fact]
    public async Task Owner_is_immutable_in_persistence_guard() =>
        await AssertBlockedIdentityChange(nameof(FixPlanRecord.OwnerUserId), Id(92));

    [Fact]
    public void Approved_plan_rejects_item_addition()
    {
        var audit = Audit();
        var plan = FixPlanRecord.Create(audit, Id(10), Now);
        plan.Approve(Id(11), Now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => plan.AddItem(Finding(audit, 1), Now.AddMinutes(2)));
    }

    [Fact]
    public void Approved_plan_rejects_item_removal()
    {
        var audit = Audit();
        var finding = Finding(audit, 1);
        var plan = FixPlanRecord.Create(audit, Id(10), Now);
        plan.AddItem(finding, Now.AddMinutes(1));
        plan.Approve(Id(11), Now.AddMinutes(2));
        Assert.Throws<InvalidOperationException>(() => plan.RemoveItem(finding.Id, Now.AddMinutes(3)));
    }

    [Fact]
    public void Approved_plan_rejects_item_replacement()
    {
        var audit = Audit();
        var plan = FixPlanRecord.Create(audit, Id(10), Now);
        plan.AddItem(Finding(audit, 1), Now.AddMinutes(1));
        plan.Approve(Id(11), Now.AddMinutes(2));
        Assert.Throws<InvalidOperationException>(() => plan.ReplaceItems([Finding(audit, 2)], Now.AddMinutes(3)));
    }

    [Fact]
    public void Arbitrary_forward_and_backward_transitions_are_rejected()
    {
        var draft = Plan();
        Assert.Throws<InvalidOperationException>(() => draft.BeginApplying(Now.AddMinutes(1)));

        var approved = Plan();
        approved.Approve(Id(11), Now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => approved.Complete(Now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => approved.Fail(Now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => approved.Approve(Id(12), Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData(FixPlanLifecycleState.Applying)]
    [InlineData(FixPlanLifecycleState.Completed)]
    [InlineData(FixPlanLifecycleState.Failed)]
    public async Task Non_draft_lifecycle_cannot_return_to_draft(FixPlanLifecycleState state)
    {
        var plan = PlanInState(state);
        await using var db = Context();
        db.Attach(plan);
        db.Entry(plan).Property(value => value.State).CurrentValue = FixPlanLifecycleState.Draft;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Invalid fix plan lifecycle transition.", error.Message);
    }

    [Fact]
    public async Task Approved_plan_cannot_be_deleted_by_persistence_guard()
    {
        var plan = PlanInState(FixPlanLifecycleState.Approved);
        await using var db = Context();
        db.Attach(plan);
        db.Remove(plan);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Approved or historical fix plans cannot be deleted.", error.Message);
    }

    [Fact]
    public void Lifecycle_timestamps_follow_the_valid_progression()
    {
        var plan = Plan();
        plan.Approve(Id(11), Now.AddMinutes(1));
        plan.BeginApplying(Now.AddMinutes(2));
        plan.Complete(Now.AddMinutes(3));

        Assert.Equal(Now.AddMinutes(1), plan.ApprovedAt);
        Assert.Equal(Now.AddMinutes(2), plan.ApplyingAt);
        Assert.Equal(Now.AddMinutes(3), plan.CompletedAt);
        Assert.Null(plan.FailedAt);
    }

    [Fact]
    public void Failure_is_supported_only_from_applying()
    {
        var plan = Plan();
        plan.Approve(Id(11), Now.AddMinutes(1));
        plan.BeginApplying(Now.AddMinutes(2));
        plan.Fail(Now.AddMinutes(3));

        Assert.Equal(FixPlanLifecycleState.Failed, plan.State);
        Assert.Equal(Now.AddMinutes(3), plan.FailedAt);
        Assert.Null(plan.CompletedAt);
    }

    [Fact]
    public void Persisted_identity_properties_do_not_expose_public_setters()
    {
        foreach (var name in new[] { nameof(FixPlanRecord.SourceAuditJobId), nameof(FixPlanRecord.SourceDocumentVersionId), nameof(FixPlanRecord.OwnerUserId) })
            Assert.False(typeof(FixPlanRecord).GetProperty(name)!.SetMethod!.IsPublic);
        foreach (var name in new[] { nameof(FixPlanItemRecord.FixPlanId), nameof(FixPlanItemRecord.FindingId) })
            Assert.False(typeof(FixPlanItemRecord).GetProperty(name)!.SetMethod!.IsPublic);
    }

    private static async Task AssertBlockedIdentityChange(string propertyName, Guid value)
    {
        var plan = PlanInState(FixPlanLifecycleState.Approved);
        await using var db = Context();
        db.Attach(plan);
        db.Entry(plan).Property(propertyName).CurrentValue = value;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Fix plan source identity is immutable.", error.Message);
    }

    private static FixPlanRecord Plan() => FixPlanRecord.Create(Audit(), Id(10), Now);

    private static FixPlanRecord PlanInState(FixPlanLifecycleState state)
    {
        var plan = Plan();
        plan.Approve(Id(11), Now.AddMinutes(1));
        if (state == FixPlanLifecycleState.Approved) return plan;
        plan.BeginApplying(Now.AddMinutes(2));
        if (state == FixPlanLifecycleState.Applying) return plan;
        if (state == FixPlanLifecycleState.Completed) plan.Complete(Now.AddMinutes(3));
        else plan.Fail(Now.AddMinutes(3));
        return plan;
    }

    private static AuditJob Audit(int audit = 1, int version = 1) => new()
    {
        Id = Id(audit),
        DocumentVersionId = Id(100 + version),
        ProfileVersionId = Id(200)
    };

    private static AuditFinding Finding(AuditJob audit, int finding) => new()
    {
        Id = Id(300 + finding),
        AuditJobId = audit.Id,
        AuditJob = audit,
        RuleId = Id(400),
        RuleCodeSnapshot = $"RULE-{finding}",
        Message = "safe diagnostic",
        ActualValueJson = "{}",
        ExpectedValueJson = "{}",
        LocationJson = "{}"
    };

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private static PpkiDbContext Context() => new(new DbContextOptionsBuilder<PpkiDbContext>()
        .UseNpgsql("Host=localhost;Database=fix_plan_offline_test")
        .Options);
}

public sealed class FixPlanRecordSchemaTests
{
    private const string MigrationName = "202608250001_fix_plan_records.sql";

    [Fact]
    public void Ef_mapping_uses_expected_tables_columns_and_enum_conversion()
    {
        using var db = Context();
        var plan = db.Model.FindEntityType(typeof(FixPlanRecord))!;
        var item = db.Model.FindEntityType(typeof(FixPlanItemRecord))!;

        Assert.Equal("fix_plans", plan.GetTableName());
        Assert.Equal("fix_plan_items", item.GetTableName());
        Assert.NotNull(plan.FindProperty(nameof(FixPlanRecord.State))!.GetTypeMapping().Converter);
        Assert.True(plan.FindProperty(nameof(FixPlanRecord.ApproverUserId))!.IsNullable);
        Assert.True(plan.FindProperty(nameof(FixPlanRecord.ApprovedAt))!.IsNullable);
        Assert.Equal("timestamp with time zone", plan.FindProperty(nameof(FixPlanRecord.UpdatedAt))!.GetColumnType());
    }

    [Fact]
    public void Ef_mapping_has_unique_membership_and_safe_delete_behaviors()
    {
        using var db = Context();
        var plan = db.Model.FindEntityType(typeof(FixPlanRecord))!;
        var item = db.Model.FindEntityType(typeof(FixPlanItemRecord))!;

        Assert.Contains(item.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(FixPlanItemRecord.FixPlanId), nameof(FixPlanItemRecord.FindingId)]));
        Assert.Contains(plan.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(AuditJob)
            && key.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(plan.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(DocumentVersion)
            && key.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(item.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(AuditFinding)
            && key.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(item.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(FixPlanRecord)
            && key.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Fact]
    public void Migration_declares_required_schema_constraints_and_indexes()
    {
        var sql = Sql();
        Assert.Contains("create table public.fix_plans", sql, StringComparison.Ordinal);
        Assert.Contains("create table public.fix_plan_items", sql, StringComparison.Ordinal);
        Assert.Contains("('Draft','Approved','Applying','Completed','Failed')", sql, StringComparison.Ordinal);
        Assert.Contains("constraint uq_fix_plan_items_plan_finding unique (fix_plan_id, finding_id)", sql, StringComparison.Ordinal);
        Assert.Contains("ix_fix_plans_source_audit", sql, StringComparison.Ordinal);
        Assert.Contains("ix_fix_plans_source_version", sql, StringComparison.Ordinal);
        Assert.Contains("ix_fix_plans_owner_state_created", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_enforces_transactional_lineage_without_denormalizing_items()
    {
        var sql = Sql();
        Assert.Contains("Fix plan source audit/version lineage is invalid.", sql, StringComparison.Ordinal);
        Assert.Contains("Fix plan item lineage is invalid.", sql, StringComparison.Ordinal);
        Assert.Contains("join public.audit_jobs audit on audit.id = finding.audit_job_id", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("source_audit_job_id uuid", ItemTableSql(sql), StringComparison.Ordinal);
        Assert.DoesNotContain("source_document_version_id uuid", ItemTableSql(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_protects_approved_membership_and_lifecycle()
    {
        var sql = Sql();
        Assert.Contains("old.state = 'Draft' and new.state = 'Approved'", sql, StringComparison.Ordinal);
        Assert.Contains("old.state = 'Approved' and new.state = 'Applying'", sql, StringComparison.Ordinal);
        Assert.Contains("old.state = 'Applying' and new.state in ('Completed','Failed')", sql, StringComparison.Ordinal);
        Assert.Contains("Approved or executing fix plan items are immutable.", sql, StringComparison.Ordinal);
        Assert.Contains("Approved or historical fix plans cannot be deleted.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_restricts_historical_parents_and_only_cascades_draft_item_cleanup()
    {
        var sql = Sql();
        Assert.Contains("source_audit_job_id uuid not null references public.audit_jobs(id) on delete restrict", sql, StringComparison.Ordinal);
        Assert.Contains("source_document_version_id uuid not null references public.document_versions(id) on delete restrict", sql, StringComparison.Ordinal);
        Assert.Contains("finding_id uuid not null references public.audit_findings(id) on delete restrict", sql, StringComparison.Ordinal);
        Assert.Contains("owner_user_id uuid not null references auth.users(id) on delete restrict", sql, StringComparison.Ordinal);
        Assert.Contains("approver_user_id uuid null references auth.users(id) on delete restrict", sql, StringComparison.Ordinal);
        Assert.Contains("fix_plan_id uuid not null references public.fix_plans(id) on delete cascade", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_applies_repository_security_and_privacy_conventions()
    {
        var sql = Sql();
        Assert.Contains("alter table public.fix_plans enable row level security", sql, StringComparison.Ordinal);
        Assert.Contains("alter table public.fix_plan_items enable row level security", sql, StringComparison.Ordinal);
        Assert.Contains("owner_user_id = (select auth.uid())", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("supabase.co", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signed_url", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("document_text", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Existing_preview_names_and_semantics_are_preserved()
    {
        Assert.Equal(["Ready", "PartiallyReady", "NotAvailable", "InvalidSelection", "InvalidSnapshot", "Conflict", "AuditIncomplete", "InvalidConfiguration"],
            Enum.GetNames<PreviewFixPlanState>());
        var item = new PreviewFixPlanItem(Guid.NewGuid(), "RULE", "key", 1,
            Ppki.Application.FixPlanItemDisposition.Planned, "ready");
        Assert.Equal("RULE", item.RuleCode);
        Assert.NotEqual(typeof(FixPlanItemRecord), typeof(PreviewFixPlanItem));
    }

    [Fact]
    public void Existing_fix_execution_mapping_remains_available()
    {
        using var db = Context();
        var execution = db.Model.FindEntityType(typeof(FixExecutionJob))!;
        Assert.Equal("fix_execution_jobs", execution.GetTableName());
        Assert.NotNull(execution.FindProperty(nameof(FixExecutionJob.PlanHash)));
        Assert.NotNull(execution.FindProperty(nameof(FixExecutionJob.PlannerVersion)));
        Assert.NotNull(execution.FindProperty(nameof(FixExecutionJob.SelectedFindingIdsJson)));
        Assert.NotNull(execution.FindProperty(nameof(FixExecutionJob.ApprovedPlanSnapshotJson)));
    }

    private static string ItemTableSql(string sql)
    {
        var start = sql.IndexOf("create table public.fix_plan_items", StringComparison.Ordinal);
        var end = sql.IndexOf("create index ix_fix_plan_items_finding", start, StringComparison.Ordinal);
        return sql[start..end];
    }

    private static string Sql() => File.ReadAllText(Path.Combine(RepositoryRoot(), "supabase", "migrations", MigrationName));

    private static PpkiDbContext Context() => new(new DbContextOptionsBuilder<PpkiDbContext>()
        .UseNpgsql("Host=localhost;Database=fix_plan_schema_test")
        .Options);

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var candidate = new DirectoryInfo(start); candidate is not null; candidate = candidate.Parent)
                if (Directory.Exists(Path.Combine(candidate.FullName, "supabase", "migrations"))) return candidate.FullName;
        throw new DirectoryNotFoundException("Repository root with Supabase migrations was not found.");
    }
}
