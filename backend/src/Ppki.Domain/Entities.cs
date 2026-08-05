namespace Ppki.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UserProfile : Entity
{
    public required string Email { get; set; }
    public required string FullName { get; set; }
    public UserRole Role { get; set; } = UserRole.Student;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DocumentType : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public DocumentKind Kind { get; set; }
}

public sealed class FormattingProfile : Entity
{
    public required string Name { get; set; }
    public required string SourceTitle { get; set; }
    public required string Edition { get; set; }
}

public sealed class ProfileVersion : Entity
{
    public Guid ProfileId { get; set; }
    public FormattingProfile? Profile { get; set; }
    public int VersionNo { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? EffectiveAt { get; set; }
    public List<ProfileRule> RuleAssignments { get; set; } = [];
}

public sealed class DocumentRecord : Entity
{
    public Guid OwnerUserId { get; set; }
    public Guid DocumentTypeId { get; set; }
    public DocumentType? DocumentType { get; set; }
    public required string Title { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Active;
    public int CurrentVersionNo { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<DocumentVersion> Versions { get; set; } = [];
}

public sealed class DocumentVersion : Entity
{
    public Guid DocumentId { get; set; }
    public DocumentRecord? Document { get; set; }
    public int VersionNo { get; set; }
    public required string StorageBucket { get; set; }
    public required string StorageKey { get; set; }
    public required string OriginalFilename { get; set; }
    public required string MimeType { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? ParentVersionId { get; set; }
    public List<AuditJob> Audits { get; set; } = [];
}

public sealed class RuleDefinition : Entity
{
    public required string RuleCode { get; set; }
    public required string Domain { get; set; }
    public string? Subdomain { get; set; }
    public required string AppliesTo { get; set; }
    public required string Element { get; set; }
    public required string OfficialRequirement { get; set; }
    public required string ExpectedValuePattern { get; set; }
    public RuleSeverity Severity { get; set; }
    public FixMode FixMode { get; set; }
    public required string ValidationKey { get; set; }
    public bool IsImplemented { get; set; }
    public int? PdfPage { get; set; }
    public string? PrintedPage { get; set; }
    public string? SourceSection { get; set; }
    public List<ProfileRule> ProfileAssignments { get; set; } = [];
}

public sealed class ProfileRule : Entity
{
    public Guid ProfileVersionId { get; set; }
    public ProfileVersion? ProfileVersion { get; set; }
    public Guid RuleId { get; set; }
    public RuleDefinition? Rule { get; set; }
}

public sealed class AuditJob : Entity
{
    public Guid DocumentVersionId { get; set; }
    public DocumentVersion? DocumentVersion { get; set; }
    public Guid ProfileVersionId { get; set; }
    public ProfileVersion? ProfileVersion { get; set; }
    // Nullable for historical rows created before the document kind snapshot was introduced.
    public DocumentKind? DocumentKindSnapshot { get; set; }
    // Nullable only for legacy rows created before S1-T01. New jobs always set this from the authenticated caller.
    public Guid? RequestedByUserId { get; set; }
    // Both lineage fields are null for ordinary/legacy audits and immutable
    // once set for a re-audit created from a completed fix execution.
    public Guid? SourceAuditJobId { get; set; }
    public AuditJob? SourceAuditJob { get; set; }
    public Guid? SourceFixExecutionId { get; set; }
    public FixExecutionJob? SourceFixExecution { get; set; }
    public AuditJobStatus Status { get; set; } = AuditJobStatus.Queued;
    public string? ResolvedRuleSetHash { get; set; }
    public int ApplicableRuleCount { get; set; }
    // Legacy API/storage column retained for endpoint compatibility. New audits
    // use ApplicableRuleCount as the resolved snapshot count.
    public int TotalRules { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public decimal? Score { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public List<AuditRuleSnapshot> RuleSnapshots { get; set; } = [];
    public List<AuditFinding> Findings { get; set; } = [];
}

public sealed class AuditRuleSnapshot : Entity
{
    public Guid AuditJobId { get; set; }
    public AuditJob? AuditJob { get; set; }
    public Guid RuleId { get; set; }
    public RuleDefinition? Rule { get; set; }
    public required string RuleCode { get; set; }
    public required string Domain { get; set; }
    public string? Subdomain { get; set; }
    public required string AppliesTo { get; set; }
    public required string Element { get; set; }
    public required string RequirementJson { get; set; }
    public required string ValidationKey { get; set; }
    public required string ValidationJson { get; set; }
    public RuleSeverity Severity { get; set; }
    public FixMode FixMode { get; set; }
    public required string SourceReferenceJson { get; set; }
    public required string Layer { get; set; }
    public int Precedence { get; set; }
    public int Ordinal { get; set; }
    public int SnapshotSchemaVersion { get; set; } = 1;
}

public sealed class AuditFinding : Entity
{
    public Guid AuditJobId { get; set; }
    public AuditJob? AuditJob { get; set; }
    public Guid RuleId { get; set; }
    public RuleDefinition? Rule { get; set; }
    public RuleSeverity Severity { get; set; }
    public required string RuleCodeSnapshot { get; set; }
    public FixMode FixModeSnapshot { get; set; }
    public string? SourceSectionSnapshot { get; set; }
    public int? PdfPageSnapshot { get; set; }
    public string? PrintedPageSnapshot { get; set; }
    public required string Message { get; set; }
    public required string ActualValueJson { get; set; }
    public required string ExpectedValueJson { get; set; }
    public required string LocationJson { get; set; }
    public decimal? Confidence { get; set; }
    public FindingStatus Status { get; set; } = FindingStatus.Open;
}

public sealed class FixExecutionJob : Entity
{
    public Guid AuditJobId { get; set; }
    public AuditJob? AuditJob { get; set; }
    public Guid SourceDocumentVersionId { get; set; }
    public DocumentVersion? SourceDocumentVersion { get; set; }
    public Guid? ResultDocumentVersionId { get; set; }
    public DocumentVersion? ResultDocumentVersion { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public required string PlanHash { get; set; }
    public required string PlannerVersion { get; set; }
    public required string SelectedFindingIdsJson { get; set; }
    public required string ApprovedPlanSnapshotJson { get; set; }
    public FixExecutionState State { get; set; } = FixExecutionState.Queued;
    public int PlannedOperationCount { get; set; }
    public int CompletedOperationCount { get; set; }
    public int FailedOperationCount { get; set; }
    public string? ResultSha256 { get; set; }
    public string? SafeFailureCode { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class FindingResolutionCase : Entity
{
    public Guid SourceAuditFindingId { get; set; }
    public AuditFinding? SourceAuditFinding { get; set; }
    public Guid SourceAuditJobId { get; set; }
    public AuditJob? SourceAuditJob { get; set; }
    public Guid SourceDocumentVersionId { get; set; }
    public DocumentVersion? SourceDocumentVersion { get; set; }
    public List<FindingResolutionEvent> Events { get; set; } = [];
}

public sealed class FindingResolutionEvent : Entity
{
    public Guid ResolutionCaseId { get; set; }
    public FindingResolutionCase? ResolutionCase { get; set; }
    public int Sequence { get; set; }
    public FindingResolutionEventType EventType { get; set; }
    public Guid? SourceFixExecutionId { get; set; }
    public FixExecutionJob? SourceFixExecution { get; set; }
    public Guid? SourceReauditJobId { get; set; }
    public AuditJob? SourceReauditJob { get; set; }
    public Guid? ResultDocumentVersionId { get; set; }
    public DocumentVersion? ResultDocumentVersion { get; set; }
    public Guid? ResultAuditFindingId { get; set; }
    public AuditFinding? ResultAuditFinding { get; set; }
    public string? ComparisonStatus { get; set; }
    public DateTimeOffset SourceOccurredAt { get; set; }
    public required string SourceEventKey { get; set; }
}

public sealed class FindingReviewCase : Entity
{
    public Guid AuditFindingId { get; set; }
    public AuditFinding? AuditFinding { get; set; }
    public Guid AuditJobId { get; set; }
    public AuditJob? AuditJob { get; set; }
    public Guid SourceDocumentVersionId { get; set; }
    public DocumentVersion? SourceDocumentVersion { get; set; }
    public Guid RequestedByUserId { get; set; }
    public List<FindingReviewEvent> Events { get; set; } = [];
}

public sealed class FindingReviewEvent : Entity
{
    public Guid ReviewCaseId { get; set; }
    public FindingReviewCase? ReviewCase { get; set; }
    public int Sequence { get; set; }
    public FindingReviewEventType EventType { get; set; }
    public FindingReviewRequestedDisposition? RequestedDisposition { get; set; }
    public FindingReviewDecision? Decision { get; set; }
    public Guid ActorUserId { get; set; }
    public string? Note { get; set; }
    public Guid IdempotencyKey { get; set; }
    public required string SourceEventKey { get; set; }
}
