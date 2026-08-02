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
    public DbSet<AuditFinding> AuditFindings => Set<AuditFinding>();

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
            entity.HasOne(x => x.Document).WithMany(x => x.Versions).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
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
            entity.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.ResolvedRuleSetHash).HasColumnName("resolved_rule_set_hash").HasMaxLength(64);
            entity.Property(x => x.TotalRules).HasColumnName("total_rules");
            entity.Property(x => x.ErrorCount).HasColumnName("error_count");
            entity.Property(x => x.WarningCount).HasColumnName("warning_count");
            entity.Property(x => x.InfoCount).HasColumnName("info_count");
            entity.Property(x => x.Score).HasColumnName("score");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.ErrorMessage).HasColumnName("error_message");
            entity.HasIndex(x => x.DocumentVersionId).HasDatabaseName("ix_audit_jobs_document_version");
            entity.HasOne(x => x.DocumentVersion).WithMany(x => x.Audits).HasForeignKey(x => x.DocumentVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ProfileVersion).WithMany().HasForeignKey(x => x.ProfileVersionId).OnDelete(DeleteBehavior.Restrict);
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
            entity.HasOne(x => x.AuditJob).WithMany(x => x.Findings).HasForeignKey(x => x.AuditJobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Rule).WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void Common<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity) where T : Entity
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasColumnName("id");
        entity.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}
