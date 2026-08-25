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

public sealed class DocumentRenderJob : Entity
{
    public Guid DocumentVersionId { get; set; }
    public DocumentVersion? DocumentVersion { get; set; }
    public required string SourceSha256 { get; set; }
    public required string RendererId { get; set; }
    public required string RendererVersion { get; set; }
    public required string RendererContractVersion { get; set; }
    public required string FontProfileVersion { get; set; }
    public required string PageMapSchemaVersion { get; set; }
    public required string RenderIdentity { get; set; }
    public DocumentRenderState State { get; set; } = DocumentRenderState.Pending;
    public int Priority { get; set; } = 100;
    public Guid? ClaimToken { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? SafeFailureCode { get; set; }
    public DocumentRenderArtifact? Artifact { get; set; }
}

public sealed class DocumentRenderArtifact : Entity
{
    public Guid RenderJobId { get; set; }
    public DocumentRenderJob? RenderJob { get; set; }
    public Guid DocumentVersionId { get; set; }
    public DocumentVersion? DocumentVersion { get; set; }
    public required string StorageBucket { get; set; }
    public required string StorageKey { get; set; }
    public required string PdfSha256 { get; set; }
    public long SizeBytes { get; set; }
    public int PageCount { get; set; }
    public required string RendererId { get; set; }
    public required string RendererVersion { get; set; }
    public required string RendererContractVersion { get; set; }
    public required string FontProfileVersion { get; set; }
    public required string PageMapSchemaVersion { get; set; }
    public required string SourceSha256 { get; set; }
    public required string SourceTextFingerprint { get; set; }
    public List<DocumentPageMapEntry> PageMapEntries { get; set; } = [];
}

public sealed class DocumentPageMapEntry : Entity
{
    public Guid RenderArtifactId { get; set; }
    public DocumentRenderArtifact? RenderArtifact { get; set; }
    public required string StructuralLocation { get; set; }
    public int? SectionIndex { get; set; }
    public int? BodyElementIndex { get; set; }
    public int? ParagraphIndex { get; set; }
    public int? RunIndex { get; set; }
    public int? TableIndex { get; set; }
    public int? RowIndex { get; set; }
    public int? CellIndex { get; set; }
    public PageMapConfidence Confidence { get; set; }
    public int? PageNumber { get; set; }
    public string? SafeReason { get; set; }
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
    public ReviewBlockingPolicy? ReviewBlockingPolicy { get; set; }
    public string? ReadinessPolicyVersion { get; set; }
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
    // Null represents a legacy snapshot whose review-readiness policy is unknown.
    public ReviewBlockingPolicy? ReviewBlockingPolicy { get; set; }
    public string? ReadinessPolicyVersion { get; set; }
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

public sealed class FixPlanRecord : Entity
{
    private readonly List<FixPlanItemRecord> _items = [];

    private FixPlanRecord() { }

    public Guid SourceAuditJobId { get; private set; }
    public AuditJob? SourceAuditJob { get; private set; }
    public Guid SourceDocumentVersionId { get; private set; }
    public DocumentVersion? SourceDocumentVersion { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string RequestHash { get; private set; } = string.Empty;
    public Guid? ApproverUserId { get; private set; }
    public FixPlanLifecycleState State { get; private set; } = FixPlanLifecycleState.Draft;
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset? ApplyingAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public IReadOnlyCollection<FixPlanItemRecord> Items => _items;

    public static FixPlanRecord Create(AuditJob sourceAuditJob, Guid ownerUserId, DateTimeOffset now) =>
        Create(sourceAuditJob, ownerUserId, Guid.NewGuid(), new string('0', 64), now);

    public static FixPlanRecord Create(
        AuditJob sourceAuditJob,
        Guid ownerUserId,
        Guid idempotencyKey,
        string requestHash,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sourceAuditJob);
        if (sourceAuditJob.Id == Guid.Empty) throw new ArgumentException("Source audit job is required.", nameof(sourceAuditJob));
        if (sourceAuditJob.DocumentVersionId == Guid.Empty) throw new ArgumentException("Source document version is required.", nameof(sourceAuditJob));
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerUserId));
        if (idempotencyKey == Guid.Empty) throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        if (requestHash is not { Length: 64 }
            || requestHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Request hash is invalid.", nameof(requestHash));

        return new FixPlanRecord
        {
            SourceAuditJobId = sourceAuditJob.Id,
            SourceAuditJob = sourceAuditJob,
            SourceDocumentVersionId = sourceAuditJob.DocumentVersionId,
            SourceDocumentVersion = sourceAuditJob.DocumentVersion,
            OwnerUserId = ownerUserId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public FixPlanItemRecord AddItem(AuditFinding finding, DateTimeOffset now)
    {
        EnsureDraft();
        ValidateFindingLineage(finding);
        if (_items.Any(item => item.FindingId == finding.Id))
            throw new InvalidOperationException("A finding can appear only once in a fix plan.");

        var item = FixPlanItemRecord.Create(this, finding, now);
        _items.Add(item);
        UpdatedAt = now;
        return item;
    }

    public void RemoveItem(Guid findingId, DateTimeOffset now)
    {
        EnsureDraft();
        var item = _items.SingleOrDefault(value => value.FindingId == findingId)
            ?? throw new InvalidOperationException("Fix plan item was not found.");
        _items.Remove(item);
        UpdatedAt = now;
    }

    public void ReplaceItems(IEnumerable<AuditFinding> findings, DateTimeOffset now)
    {
        EnsureDraft();
        ArgumentNullException.ThrowIfNull(findings);
        var replacements = findings.ToArray();
        foreach (var finding in replacements) ValidateFindingLineage(finding);
        if (replacements.Select(finding => finding.Id).Distinct().Count() != replacements.Length)
            throw new InvalidOperationException("A finding can appear only once in a fix plan.");

        _items.Clear();
        _items.AddRange(replacements.Select(finding => FixPlanItemRecord.Create(this, finding, now)));
        UpdatedAt = now;
    }

    public void Approve(Guid approverUserId, DateTimeOffset now)
    {
        if (State != FixPlanLifecycleState.Draft)
            throw new InvalidOperationException("Only a draft fix plan can be approved.");
        if (approverUserId == Guid.Empty) throw new ArgumentException("Approver is required.", nameof(approverUserId));
        State = FixPlanLifecycleState.Approved;
        ApproverUserId = approverUserId;
        ApprovedAt = now;
        UpdatedAt = now;
    }

    public void BeginApplying(DateTimeOffset now)
    {
        Transition(FixPlanLifecycleState.Approved, FixPlanLifecycleState.Applying, now);
        ApplyingAt = now;
    }

    public void Complete(DateTimeOffset now)
    {
        Transition(FixPlanLifecycleState.Applying, FixPlanLifecycleState.Completed, now);
        CompletedAt = now;
    }

    public void Fail(DateTimeOffset now)
    {
        Transition(FixPlanLifecycleState.Applying, FixPlanLifecycleState.Failed, now);
        FailedAt = now;
    }

    private void Transition(FixPlanLifecycleState expected, FixPlanLifecycleState next, DateTimeOffset now)
    {
        if (State != expected)
            throw new InvalidOperationException($"Fix plan cannot transition from {State} to {next}.");
        State = next;
        UpdatedAt = now;
    }

    private void EnsureDraft()
    {
        if (State != FixPlanLifecycleState.Draft)
            throw new InvalidOperationException("Approved or executing fix plan items are immutable.");
    }

    private void ValidateFindingLineage(AuditFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Id == Guid.Empty) throw new ArgumentException("Finding is required.", nameof(finding));
        if (finding.AuditJobId != SourceAuditJobId)
            throw new InvalidOperationException("Finding belongs to another audit job.");
        if (finding.AuditJob is null)
            throw new InvalidOperationException("Finding audit lineage must be loaded.");
        if (finding.AuditJob.DocumentVersionId != SourceDocumentVersionId)
            throw new InvalidOperationException("Finding belongs to another document version.");
    }
}

public sealed class FixPlanItemRecord : Entity
{
    private FixPlanItemRecord() { }

    public Guid FixPlanId { get; private set; }
    public FixPlanRecord? FixPlan { get; private set; }
    public Guid FindingId { get; private set; }
    public AuditFinding? Finding { get; private set; }

    internal static FixPlanItemRecord Create(FixPlanRecord plan, AuditFinding finding, DateTimeOffset now) => new()
    {
        FixPlanId = plan.Id,
        FixPlan = plan,
        FindingId = finding.Id,
        Finding = finding,
        CreatedAt = now
    };
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
    public FixFailureCategory? FailureCategory { get; set; }
    public Guid? ClaimToken { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset? NextAttemptAt { get; set; }
    public long? ResultObjectSize { get; set; }
    public int? ObjectCreatedByAttempt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AutomaticRemediationOrchestration : Entity
{
    public Guid SourceAuditJobId { get; set; }
    public AuditJob? SourceAuditJob { get; set; }
    public required string OrchestrationType { get; set; }
    public required string PolicyVersion { get; set; }
    public AutomaticRemediationState State { get; set; } = AutomaticRemediationState.Pending;
    public int EligibleFindingCount { get; set; }
    public int OperationCount { get; set; }
    public Guid? FixExecutionId { get; set; }
    public FixExecutionJob? FixExecution { get; set; }
    public Guid? ResultDocumentVersionId { get; set; }
    public DocumentVersion? ResultDocumentVersion { get; set; }
    public Guid? ReauditJobId { get; set; }
    public AuditJob? ReauditJob { get; set; }
    public string? SafeFailureCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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

public sealed class TextCorrectionAnalysis : Entity
{
    public Guid AuditJobId { get; set; }
    public AuditJob? AuditJob { get; set; }
    public Guid DocumentVersionId { get; set; }
    public DocumentVersion? DocumentVersion { get; set; }
    public required string SourceSha256 { get; set; }
    public required string DetectorId { get; set; }
    public required string DetectorVersion { get; set; }
    public required string CatalogVersion { get; set; }
    public TextCorrectionAnalysisState State { get; set; } = TextCorrectionAnalysisState.Pending;
    public int ProposalCount { get; set; }
    public string? SafeFailureCode { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<TextCorrectionProposal> Proposals { get; set; } = [];
}

public sealed class TextCorrectionProposal : Entity
{
    public Guid AnalysisId { get; set; }
    public TextCorrectionAnalysis? Analysis { get; set; }
    public Guid AuditJobId { get; set; }
    public AuditJob? AuditJob { get; set; }
    public Guid DocumentVersionId { get; set; }
    public DocumentVersion? DocumentVersion { get; set; }
    public required string SourceSha256 { get; set; }
    public required string DetectorId { get; set; }
    public required string DetectorVersion { get; set; }
    public required string CatalogVersion { get; set; }
    public required string CatalogRuleId { get; set; }
    public required string Category { get; set; }
    public required string AnchorContractVersion { get; set; }
    public required string AnchorEvidenceJson { get; set; }
    public required string AnchorHash { get; set; }
    public required string SuggestedReplacement { get; set; }
    public required string SuggestionHash { get; set; }
    public required string ProposalIdentity { get; set; }
    public List<TextCorrectionDecisionEvent> Decisions { get; set; } = [];
}

public sealed class TextCorrectionDecisionEvent : Entity
{
    public Guid ProposalId { get; set; }
    public TextCorrectionProposal? Proposal { get; set; }
    public int Sequence { get; set; }
    public Guid ActorUserId { get; set; }
    public TextCorrectionDecisionAction Action { get; set; }
    public Guid SourceDocumentVersionId { get; set; }
    public DocumentVersion? SourceDocumentVersion { get; set; }
    public required string AnchorHash { get; set; }
    public string? ManualReplacement { get; set; }
    public string? ReplacementHash { get; set; }
    public Guid IdempotencyKey { get; set; }
    public required string SemanticHash { get; set; }
}

public sealed class TextCorrectionBatch : Entity
{
    public Guid SourceAuditJobId { get; set; }
    public AuditJob? SourceAuditJob { get; set; }
    public Guid SourceDocumentVersionId { get; set; }
    public DocumentVersion? SourceDocumentVersion { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public required string DecisionSetHash { get; set; }
    public int DecisionCount { get; set; }
    public TextCorrectionBatchState State { get; set; } = TextCorrectionBatchState.Pending;
    public Guid? FixExecutionId { get; set; }
    public FixExecutionJob? FixExecution { get; set; }
    public Guid? ResultDocumentVersionId { get; set; }
    public DocumentVersion? ResultDocumentVersion { get; set; }
    public Guid? ReauditJobId { get; set; }
    public AuditJob? ReauditJob { get; set; }
    public string? SafeFailureCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<TextCorrectionBatchItem> Items { get; set; } = [];
}

public sealed class TextCorrectionBatchItem : Entity
{
    public Guid BatchId { get; set; }
    public TextCorrectionBatch? Batch { get; set; }
    public Guid DecisionEventId { get; set; }
    public TextCorrectionDecisionEvent? DecisionEvent { get; set; }
    public int Ordinal { get; set; }
    public TextCorrectionVerificationState VerificationState { get; set; } = TextCorrectionVerificationState.Applied;
    public DateTimeOffset? VerifiedAt { get; set; }
}
