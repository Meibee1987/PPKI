using Microsoft.EntityFrameworkCore;
using Ppki.Domain;

namespace Ppki.Infrastructure;

public sealed class PpkiDbContext(DbContextOptions<PpkiDbContext> options) : DbContext(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<FormattingProfile> FormattingProfiles => Set<FormattingProfile>();
    public DbSet<ProfileVersion> ProfileVersions => Set<ProfileVersion>();
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<RuleDefinition> Rules => Set<RuleDefinition>();
    public DbSet<AuditJob> AuditJobs => Set<AuditJob>();
    public DbSet<AuditFinding> AuditFindings => Set<AuditFinding>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserProfile>(e => { e.ToTable("user_profiles"); Common(e); e.Property(x=>x.Email).HasColumnName("email"); e.Property(x=>x.FullName).HasColumnName("full_name"); e.Property(x=>x.Role).HasColumnName("role"); e.Property(x=>x.UpdatedAt).HasColumnName("updated_at"); });
        b.Entity<DocumentType>(e => { e.ToTable("document_types"); Common(e); e.Property(x=>x.Code).HasColumnName("code"); e.Property(x=>x.Name).HasColumnName("name"); e.Property(x=>x.Kind).HasColumnName("kind").HasConversion<string>(); e.HasIndex(x=>x.Code).IsUnique(); });
        b.Entity<FormattingProfile>(e => { e.ToTable("formatting_profiles"); Common(e); e.Property(x=>x.Name).HasColumnName("name"); e.Property(x=>x.SourceTitle).HasColumnName("source_title"); e.Property(x=>x.Edition).HasColumnName("edition"); });
        b.Entity<ProfileVersion>(e => { e.ToTable("profile_versions"); Common(e); e.Property(x=>x.ProfileId).HasColumnName("profile_id"); e.Property(x=>x.VersionNo).HasColumnName("version_no"); e.Property(x=>x.Status).HasColumnName("status"); e.Property(x=>x.EffectiveAt).HasColumnName("effective_at"); e.HasIndex(x=>new{x.ProfileId,x.VersionNo}).IsUnique(); });
        b.Entity<DocumentRecord>(e => { e.ToTable("documents"); Common(e); e.Property(x=>x.OwnerUserId).HasColumnName("owner_user_id"); e.Property(x=>x.DocumentTypeId).HasColumnName("document_type_id"); e.Property(x=>x.Title).HasColumnName("title"); e.Property(x=>x.CurrentVersionNo).HasColumnName("current_version_no"); e.Property(x=>x.UpdatedAt).HasColumnName("updated_at"); });
        b.Entity<DocumentVersion>(e => { e.ToTable("document_versions"); Common(e); e.Property(x=>x.DocumentId).HasColumnName("document_id"); e.Property(x=>x.VersionNo).HasColumnName("version_no"); e.Property(x=>x.StorageBucket).HasColumnName("storage_bucket"); e.Property(x=>x.StorageKey).HasColumnName("storage_key"); e.Property(x=>x.OriginalFilename).HasColumnName("original_filename"); e.Property(x=>x.MimeType).HasColumnName("mime_type"); e.Property(x=>x.SizeBytes).HasColumnName("size_bytes"); e.Property(x=>x.Sha256).HasColumnName("sha256"); e.Property(x=>x.CreatedByUserId).HasColumnName("created_by_user_id"); e.Property(x=>x.ParentVersionId).HasColumnName("parent_version_id"); e.HasIndex(x=>new{x.DocumentId,x.VersionNo}).IsUnique(); });
        b.Entity<RuleDefinition>(e => { e.ToTable("rules"); Common(e); e.Property(x=>x.RuleCode).HasColumnName("rule_code"); e.Property(x=>x.Domain).HasColumnName("domain"); e.Property(x=>x.Subdomain).HasColumnName("subdomain"); e.Property(x=>x.AppliesTo).HasColumnName("applies_to"); e.Property(x=>x.Element).HasColumnName("element"); e.Property(x=>x.OfficialRequirement).HasColumnName("official_requirement"); e.Property(x=>x.ExpectedValuePattern).HasColumnName("expected_value_pattern"); e.Property(x=>x.Severity).HasColumnName("severity").HasConversion<string>(); e.Property(x=>x.FixMode).HasColumnName("fix_mode").HasConversion<string>(); e.Property(x=>x.ValidationKey).HasColumnName("validation_key"); e.Property(x=>x.IsImplemented).HasColumnName("is_implemented"); e.Property(x=>x.PdfPage).HasColumnName("pdf_page"); e.Property(x=>x.PrintedPage).HasColumnName("printed_page"); e.Property(x=>x.SourceSection).HasColumnName("source_section"); e.HasIndex(x=>x.RuleCode).IsUnique(); });
        b.Entity<AuditJob>(e => { e.ToTable("audit_jobs"); Common(e); e.Property(x=>x.DocumentVersionId).HasColumnName("document_version_id"); e.Property(x=>x.ProfileVersionId).HasColumnName("profile_version_id"); e.Property(x=>x.Status).HasColumnName("status").HasConversion<string>(); e.Property(x=>x.ResolvedRuleSetHash).HasColumnName("resolved_rule_set_hash"); e.Property(x=>x.TotalRules).HasColumnName("total_rules"); e.Property(x=>x.ErrorCount).HasColumnName("error_count"); e.Property(x=>x.WarningCount).HasColumnName("warning_count"); e.Property(x=>x.InfoCount).HasColumnName("info_count"); e.Property(x=>x.Score).HasColumnName("score"); e.Property(x=>x.StartedAt).HasColumnName("started_at"); e.Property(x=>x.CompletedAt).HasColumnName("completed_at"); e.Property(x=>x.ErrorMessage).HasColumnName("error_message"); });
        b.Entity<AuditFinding>(e => { e.ToTable("audit_findings"); Common(e); e.Property(x=>x.AuditJobId).HasColumnName("audit_job_id"); e.Property(x=>x.RuleId).HasColumnName("rule_id"); e.Property(x=>x.Severity).HasColumnName("severity").HasConversion<string>(); e.Property(x=>x.Message).HasColumnName("message"); e.Property(x=>x.ActualValueJson).HasColumnName("actual_value").HasColumnType("jsonb"); e.Property(x=>x.ExpectedValueJson).HasColumnName("expected_value").HasColumnType("jsonb"); e.Property(x=>x.LocationJson).HasColumnName("location").HasColumnType("jsonb"); e.Property(x=>x.Confidence).HasColumnName("confidence"); e.Property(x=>x.Status).HasColumnName("status").HasConversion<string>(); });

        b.Entity<DocumentRecord>().HasMany(x=>x.Versions).WithOne(x=>x.Document).HasForeignKey(x=>x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DocumentVersion>().HasMany(x=>x.Audits).WithOne(x=>x.DocumentVersion).HasForeignKey(x=>x.DocumentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AuditJob>().HasMany(x=>x.Findings).WithOne(x=>x.AuditJob).HasForeignKey(x=>x.AuditJobId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void Common<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : Entity
    { e.HasKey(x=>x.Id); e.Property(x=>x.Id).HasColumnName("id"); e.Property(x=>x.CreatedAt).HasColumnName("created_at"); }
}
