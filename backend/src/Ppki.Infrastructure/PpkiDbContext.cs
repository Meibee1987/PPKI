using Microsoft.EntityFrameworkCore;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class PpkiDbContext(DbContextOptions<PpkiDbContext> options) : DbContext(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<FormattingProfile> FormattingProfiles => Set<FormattingProfile>();
    public DbSet<ProfileVersion> ProfileVersions => Set<ProfileVersion>();
    public DbSet<ProfileRule> ProfileRules => Set<ProfileRule>();
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<RuleDefinition> Rules => Set<RuleDefinition>();
    public DbSet<AuditJob> AuditJobs => Set<AuditJob>();
    public DbSet<AuditRuleSnapshot> AuditRuleSnapshots => Set<AuditRuleSnapshot>();
    public DbSet<AuditFinding> AuditFindings => Set<AuditFinding>();
    public DbSet<FixExecutionJob> FixExecutionJobs => Set<FixExecutionJob>();
    public DbSet<AuditTrailEvent> AuditTrailEvents => Set<AuditTrailEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<UserProfile>(entity =>
        {
            entity.ToTable("user_profiles");
            Common(entity);
            entity.Property(x => x.Email).HasColumnName("email").IsRequired();
            entity.Property(x => x.FullName).HasColumnName("full_name").IsRequired();
            entity.Property(x => x.Role).HasColumnName("role").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        builder.Entity<DocumentType>(entity =>
        {
            entity.ToTable("document_types");
            Common(entity);
            entity.Property(x => x.Code).HasColumnName("code").IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").IsRequired();
            entity.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>();
            entity.HasIndex(x => x.Code).IsUnique();
        });

        builder.Entity<FormattingProfile>(entity =>
        {
            entity.ToTable("formatting_profiles");
            Common(entity);
            entity.Property(x => x.Name).HasColumnName("name").IsRequired();
            entity.Property(x => x.SourceTitle).HasColumnName("source_title").IsRequired();
            entity.Property(x => x.Edition).HasColumnName("edition").IsRequired();
        });

        builder.Entity<ProfileVersion>(entity =>
        {
            entity.ToTable("profile_versions");
            Common(entity);
            entity.Property(x => x.ProfileId).HasColumnName("profile_id");
            entity.Property(x => x.VersionNo).HasColumnName("version_no");
            entity.Property(x => x.Status).HasColumnName("status").IsRequired();
            entity.Property(x => x.EffectiveAt).HasColumnName("effective_at");
            entity.HasIndex(x => new { x.ProfileId, x.VersionNo }).IsUnique();
            entity.HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DocumentRecord>(entity =>
        {
            entity.ToTable("documents");
            Common(entity);
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id").IsRequired();
            entity.Property(x => x.DocumentTypeId).HasColumnName("document_type_id");
            entity.Property(x => x.Title).HasColumnName("title").HasMaxLength(512).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.CurrentVersionNo).HasColumnName("current_version_no");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.OwnerUserId).HasDatabaseName("ix_documents_owner");
            entity.HasOne(x => x.DocumentType).WithMany().HasForeignKey(x => x.DocumentTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DocumentVersion>(entity =>
        {
            entity.ToTable("document_versions");
            Common(entity);
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.VersionNo).HasColumnName("version_no");
            entity.Property(x => x.StorageBucket).HasColumnName("storage_bucket").IsRequired();
            entity.Property(x => x.StorageKey).HasColumnName("storage_key").IsRequired();
            entity.Property(x => x.OriginalFilename).HasColumnName("original_filename").IsRequired();
            entity.Property(x => x.MimeType).HasColumnName("mime_type").IsRequired();
            entity.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            entity.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            entity.Property(x => x.ParentVersionId).HasColumnName("parent_version_id");
            entity.HasIndex(x => new { x.DocumentId, x.VersionNo }).IsUnique();
            entity.HasIndex(x => x.DocumentId).HasDatabaseName("ix_document_versions_document");
            entity.HasOne(x => x.Document).WithMany(x => x.Versions).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.ParentVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RuleDefinition>(entity =>
        {
            entity.ToTable("rules");
            Common(entity);
            entity.Property(x => x.RuleCode).HasColumnName("rule_code").IsRequired();
            entity.Property(x => x.Domain).HasColumnName("domain").IsRequired();
            entity.Property(x => x.Subdomain).HasColumnName("subdomain");
            entity.Property(x => x.AppliesTo).HasColumnName("applies_to").IsRequired();
            entity.Property(x => x.Element).HasColumnName("element").IsRequired();
            entity.Property(x => x.OfficialRequirement).HasColumnName("official_requirement").IsRequired();
            entity.Property(x => x.ExpectedValuePattern).HasColumnName("expected_value_pattern").IsRequired();
            entity.Property(x => x.Severity).HasColumnName("severity").HasConversion<string>();
            entity.Property(x => x.FixMode).HasColumnName("fix_mode").HasConversion<string>();
            entity.Property(x => x.ValidationKey).HasColumnName("validation_key").IsRequired();
            entity.Property(x => x.IsImplemented).HasColumnName("is_implemented");
            entity.Property(x => x.PdfPage).HasColumnName("pdf_page");
            entity.Property(x => x.PrintedPage).HasColumnName("printed_page");
            entity.Property(x => x.SourceSection).HasColumnName("source_section");
            entity.HasIndex(x => x.RuleCode).IsUnique();
        });

        builder.Entity<ProfileRule>(entity =>
        {
            entity.ToTable("profile_rules");
            Common(entity);
            entity.Property(x => x.ProfileVersionId).HasColumnName("profile_version_id");
            entity.Property(x => x.RuleId).HasColumnName("rule_id");
            entity.HasIndex(x => new { x.ProfileVersionId, x.RuleId }).IsUnique();
            entity.HasOne(x => x.ProfileVersion).WithMany(x => x.RuleAssignments).HasForeignKey(x => x.ProfileVersionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Rule).WithMany(x => x.ProfileAssignments).HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditJob>(entity =>
        {
            entity.ToTable("audit_jobs");
            Common(entity);
            entity.Property(x => x.DocumentVersionId).HasColumnName("document_version_id");
            entity.Property(x => x.ProfileVersionId).HasColumnName("profile_version_id");
            entity.Property(x => x.DocumentKindSnapshot).HasColumnName("document_kind_snapshot").HasConversion<string>();
            entity.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id");
            entity.Property(x => x.SourceAuditJobId).HasColumnName("source_audit_job_id");
            entity.Property(x => x.SourceFixExecutionId).HasColumnName("source_fix_execution_id");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.ResolvedRuleSetHash).HasColumnName("resolved_rule_set_hash").HasMaxLength(64);
            entity.Property(x => x.ApplicableRuleCount).HasColumnName("applicable_rule_count");
            entity.Property(x => x.TotalRules).HasColumnName("total_rules");
            entity.Property(x => x.ErrorCount).HasColumnName("error_count");
            entity.Property(x => x.WarningCount).HasColumnName("warning_count");
            entity.Property(x => x.InfoCount).HasColumnName("info_count");
            entity.Property(x => x.Score).HasColumnName("score");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.ErrorMessage).HasColumnName("error_message");
            entity.HasIndex(x => x.DocumentVersionId).HasDatabaseName("ix_audit_jobs_document_version");
            entity.HasIndex(x => x.SourceAuditJobId).HasDatabaseName("ix_audit_jobs_source_audit");
            entity.HasIndex(x => x.SourceFixExecutionId).IsUnique()
                .HasDatabaseName("uq_audit_jobs_source_fix_execution")
                .HasFilter("source_fix_execution_id is not null");
            entity.HasOne(x => x.DocumentVersion).WithMany(x => x.Audits).HasForeignKey(x => x.DocumentVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ProfileVersion).WithMany().HasForeignKey(x => x.ProfileVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SourceAuditJob).WithMany().HasForeignKey(x => x.SourceAuditJobId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SourceFixExecution).WithMany().HasForeignKey(x => x.SourceFixExecutionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditRuleSnapshot>(entity =>
        {
            entity.ToTable("audit_rule_snapshots");
            Common(entity);
            entity.Property(x => x.AuditJobId).HasColumnName("audit_job_id");
            entity.Property(x => x.RuleId).HasColumnName("rule_id");
            entity.Property(x => x.RuleCode).HasColumnName("rule_code").IsRequired();
            entity.Property(x => x.Domain).HasColumnName("domain").IsRequired();
            entity.Property(x => x.Subdomain).HasColumnName("subdomain");
            entity.Property(x => x.AppliesTo).HasColumnName("applies_to").IsRequired();
            entity.Property(x => x.Element).HasColumnName("element").IsRequired();
            entity.Property(x => x.RequirementJson).HasColumnName("requirement_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ValidationKey).HasColumnName("validation_key").IsRequired();
            entity.Property(x => x.ValidationJson).HasColumnName("validation_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Severity).HasColumnName("severity").HasConversion<string>();
            entity.Property(x => x.FixMode).HasColumnName("fix_mode").HasConversion<string>();
            entity.Property(x => x.SourceReferenceJson).HasColumnName("source_reference_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Layer).HasColumnName("layer").IsRequired();
            entity.Property(x => x.Precedence).HasColumnName("precedence");
            entity.Property(x => x.Ordinal).HasColumnName("ordinal");
            entity.Property(x => x.SnapshotSchemaVersion).HasColumnName("snapshot_schema_version");
            entity.HasIndex(x => x.AuditJobId).HasDatabaseName("ix_audit_rule_snapshots_audit_job");
            entity.HasIndex(x => new { x.AuditJobId, x.RuleCode }).IsUnique();
            entity.HasIndex(x => new { x.AuditJobId, x.Ordinal }).IsUnique();
            entity.HasOne(x => x.AuditJob).WithMany(x => x.RuleSnapshots).HasForeignKey(x => x.AuditJobId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Rule).WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditFinding>(entity =>
        {
            entity.ToTable("audit_findings");
            Common(entity);
            entity.Property(x => x.AuditJobId).HasColumnName("audit_job_id");
            entity.Property(x => x.RuleId).HasColumnName("rule_id");
            entity.Property(x => x.Severity).HasColumnName("severity").HasConversion<string>();
            entity.Property(x => x.RuleCodeSnapshot).HasColumnName("rule_code_snapshot").IsRequired();
            entity.Property(x => x.FixModeSnapshot).HasColumnName("fix_mode_snapshot").HasConversion<string>();
            entity.Property(x => x.SourceSectionSnapshot).HasColumnName("source_section_snapshot");
            entity.Property(x => x.PdfPageSnapshot).HasColumnName("pdf_page_snapshot");
            entity.Property(x => x.PrintedPageSnapshot).HasColumnName("printed_page_snapshot");
            entity.Property(x => x.Message).HasColumnName("message").IsRequired();
            entity.Property(x => x.ActualValueJson).HasColumnName("actual_value").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ExpectedValueJson).HasColumnName("expected_value").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.LocationJson).HasColumnName("location").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Confidence).HasColumnName("confidence");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.HasIndex(x => x.AuditJobId).HasDatabaseName("ix_audit_findings_audit_job");
            entity.HasOne(x => x.AuditJob).WithMany(x => x.Findings).HasForeignKey(x => x.AuditJobId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Rule).WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<FixExecutionJob>(entity =>
        {
            entity.ToTable("fix_execution_jobs");
            Common(entity);
            entity.Property(x => x.AuditJobId).HasColumnName("audit_job_id");
            entity.Property(x => x.SourceDocumentVersionId).HasColumnName("source_document_version_id");
            entity.Property(x => x.ResultDocumentVersionId).HasColumnName("result_document_version_id");
            entity.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id");
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(x => x.PlanHash).HasColumnName("plan_hash").HasMaxLength(64).IsRequired();
            entity.Property(x => x.PlannerVersion).HasColumnName("planner_version").HasMaxLength(64).IsRequired();
            entity.Property(x => x.SelectedFindingIdsJson).HasColumnName("selected_finding_ids").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ApprovedPlanSnapshotJson).HasColumnName("approved_plan_snapshot").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.State).HasColumnName("state").HasConversion<string>();
            entity.Property(x => x.PlannedOperationCount).HasColumnName("planned_operation_count");
            entity.Property(x => x.CompletedOperationCount).HasColumnName("completed_operation_count");
            entity.Property(x => x.FailedOperationCount).HasColumnName("failed_operation_count");
            entity.Property(x => x.ResultSha256).HasColumnName("result_sha256").HasMaxLength(64);
            entity.Property(x => x.SafeFailureCode).HasColumnName("safe_failure_code").HasMaxLength(128);
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(x => new { x.AuditJobId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.SourceDocumentVersionId, x.PlanHash }).IsUnique();
            entity.HasIndex(x => new { x.State, x.CreatedAt }).HasDatabaseName("ix_fix_execution_jobs_worker_queue");
            entity.HasOne(x => x.AuditJob).WithMany().HasForeignKey(x => x.AuditJobId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SourceDocumentVersion).WithMany().HasForeignKey(x => x.SourceDocumentVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ResultDocumentVersion).WithMany().HasForeignKey(x => x.ResultDocumentVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditTrailEvent>(entity =>
        {
            entity.ToTable("audit_trail_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
            entity.Property(x => x.ActorType).HasColumnName("actor_type")
                .HasConversion(value => value.ToString().ToLowerInvariant(), value => Enum.Parse<AuditActorType>(value, true));
            entity.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(x => x.ActorService).HasColumnName("actor_service");
            entity.Property(x => x.Action).HasColumnName("action").HasMaxLength(128).IsRequired();
            entity.Property(x => x.ResourceType).HasColumnName("resource_type").HasMaxLength(64).IsRequired();
            entity.Property(x => x.ResourceId).HasColumnName("resource_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            entity.Property(x => x.CausationId).HasColumnName("causation_id");
            entity.Property(x => x.RequestId).HasColumnName("request_id").HasMaxLength(128);
            entity.Property(x => x.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.EventSchemaVersion).HasColumnName("event_schema_version");
            entity.Property(x => x.EventSource).HasColumnName("event_source")
                .HasConversion(
                    value => value == AuditEventSource.DatabaseTrigger ? "database_trigger" : "application",
                    value => value == "database_trigger" ? AuditEventSource.DatabaseTrigger : AuditEventSource.Application);
            entity.HasIndex(x => x.OccurredAt).HasDatabaseName("ix_audit_trail_occurred_at");
            entity.HasIndex(x => x.CorrelationId).HasDatabaseName("ix_audit_trail_correlation_id");
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId }).HasDatabaseName("ix_audit_trail_resource");
            entity.HasIndex(x => new { x.OwnerUserId, x.OccurredAt }).HasDatabaseName("ix_audit_trail_owner_occurred");
            entity.HasIndex(x => new { x.ActorUserId, x.OccurredAt }).HasDatabaseName("ix_audit_trail_actor_occurred");
            entity.HasIndex(x => new { x.Action, x.ResourceType, x.ResourceId, x.CorrelationId })
                .HasDatabaseName("uq_audit_trail_semantic_event")
                .IsUnique()
                .HasFilter("resource_id is not null");
        });
    }

    private static void Common<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity) where T : Entity
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasColumnName("id");
        entity.Property(x => x.CreatedAt).HasColumnName("created_at");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectImmutableEntityMutations();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectImmutableEntityMutations();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RejectImmutableEntityMutations()
    {
        var immutableAuditProperties = new[]
        {
            nameof(AuditJob.DocumentVersionId), nameof(AuditJob.ProfileVersionId),
            nameof(AuditJob.DocumentKindSnapshot), nameof(AuditJob.RequestedByUserId),
            nameof(AuditJob.SourceAuditJobId), nameof(AuditJob.SourceFixExecutionId),
            nameof(AuditJob.CreatedAt)
        };
        if (ChangeTracker.Entries<AuditJob>().Any(entry => entry.State == EntityState.Modified
            && (immutableAuditProperties.Any(name => entry.Property(name).IsModified)
                || entry.Property(item => item.ResolvedRuleSetHash).IsModified
                    && entry.Property(item => item.ResolvedRuleSetHash).OriginalValue is not null
                || entry.Property(item => item.ApplicableRuleCount).IsModified
                    && entry.Property(item => item.ResolvedRuleSetHash).OriginalValue is not null)))
        {
            throw new InvalidOperationException("Audit job identity and resolved context are immutable.");
        }

        if (ChangeTracker.Entries<DocumentVersion>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Document versions are insert-only.");
        }

        var immutableFixProperties = new[]
        {
            nameof(FixExecutionJob.AuditJobId), nameof(FixExecutionJob.SourceDocumentVersionId),
            nameof(FixExecutionJob.RequestedByUserId), nameof(FixExecutionJob.IdempotencyKey),
            nameof(FixExecutionJob.PlanHash), nameof(FixExecutionJob.PlannerVersion),
            nameof(FixExecutionJob.SelectedFindingIdsJson), nameof(FixExecutionJob.ApprovedPlanSnapshotJson),
            nameof(FixExecutionJob.PlannedOperationCount), nameof(FixExecutionJob.CreatedAt)
        };
        if (ChangeTracker.Entries<FixExecutionJob>().Any(entry => entry.State == EntityState.Deleted
            || entry.State == EntityState.Modified && immutableFixProperties.Any(name => entry.Property(name).IsModified)))
        {
            throw new InvalidOperationException("Fix execution request and approved plan snapshot are immutable.");
        }

        if (ChangeTracker.Entries<AuditRuleSnapshot>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit rule snapshots are insert-only.");
        }

        if (ChangeTracker.Entries<AuditTrailEvent>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit trail events are append-only.");
        }
    }
}
