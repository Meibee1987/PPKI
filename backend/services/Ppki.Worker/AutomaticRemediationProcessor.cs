using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Ppki.Application;
using Ppki.Domain;
using Ppki.FixEngine;
using Ppki.Infrastructure;

namespace Ppki.Worker;

public sealed class AutomaticRemediationProcessor(
    IDbContextFactory<PpkiDbContext> dbFactory,
    IFixPlanSourceReader sourceReader,
    IFixPlanPreviewPlanner planner,
    IFixExecutionService executions,
    IReauditService reaudits,
    IFindingResolutionService resolutions,
    TimeProvider timeProvider)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var orchestration = await db.AutomaticRemediationOrchestrations.FromSqlRaw("""
            select orchestration.* from public.automatic_remediation_orchestrations as orchestration
            join public.audit_jobs as audit on audit.id = orchestration.source_audit_job_id
            where orchestration.state in ('Queued','Processing','ReauditPending')
               or orchestration.state = 'Pending' and audit.status in ('Completed','Failed','Cancelled')
            order by orchestration.updated_at, orchestration.created_at
            for update of orchestration skip locked
            limit 1
            """).SingleOrDefaultAsync(cancellationToken);
        if (orchestration is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await AdvanceAsync(db, orchestration, cancellationToken);
        orchestration.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task AdvanceAsync(
        PpkiDbContext db,
        AutomaticRemediationOrchestration orchestration,
        CancellationToken cancellationToken)
    {
        var audit = await db.AuditJobs.AsNoTracking()
            .SingleAsync(value => value.Id == orchestration.SourceAuditJobId, cancellationToken);
        if (audit.SourceFixExecutionId is not null)
        {
            Fail(orchestration, AutomaticRemediationState.Conflict, "automatic-source-lineage-invalid");
            return;
        }

        switch (orchestration.State)
        {
            case AutomaticRemediationState.Pending:
                await PlanAsync(db, orchestration, audit, cancellationToken);
                break;
            case AutomaticRemediationState.Queued:
            case AutomaticRemediationState.Processing:
                await ObserveExecutionAsync(db, orchestration, audit, cancellationToken);
                break;
            case AutomaticRemediationState.ReauditPending:
                await ObserveReauditAsync(db, orchestration, audit, cancellationToken);
                break;
        }
    }

    private async Task PlanAsync(
        PpkiDbContext db,
        AutomaticRemediationOrchestration orchestration,
        AuditJob audit,
        CancellationToken cancellationToken)
    {
        if (audit.Status != AuditJobStatus.Completed)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-source-audit-not-completed");
            return;
        }
        if (audit.RequestedByUserId is null)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-source-context-invalid");
            return;
        }

        var snapshots = await (
            from finding in db.AuditFindings.AsNoTracking()
            join snapshot in db.AuditRuleSnapshots.AsNoTracking()
                on new { finding.AuditJobId, RuleCode = finding.RuleCodeSnapshot }
                equals new { snapshot.AuditJobId, RuleCode = snapshot.RuleCode }
            where finding.AuditJobId == audit.Id
            select new FixPlanFindingSnapshot(
                finding.Id, snapshot.Ordinal, snapshot.RuleCode, snapshot.Domain, snapshot.Element,
                snapshot.ValidationKey, snapshot.Severity, snapshot.FixMode, finding.Status,
                finding.ActualValueJson, finding.ExpectedValueJson, finding.LocationJson,
                snapshot.SnapshotSchemaVersion)).ToListAsync(cancellationToken);
        var eligible = snapshots.Where(value => AutomaticRemediationPolicy.Classify(value)
                == AutomaticRemediationPolicyOutcome.AutoApply)
            .OrderBy(value => value.RuleOrdinal).ThenBy(value => value.RuleCode, StringComparer.Ordinal)
            .ThenBy(value => value.LocationJson, StringComparer.Ordinal).ThenBy(value => value.FindingId)
            .ToArray();
        if (eligible.Length == 0)
        {
            orchestration.State = AutomaticRemediationState.NoAction;
            return;
        }
        if (eligible.Length > AuditFindingQuery.MaximumFindingCount)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-selection-limit-exceeded");
            return;
        }

        var selection = new FixPlanSelection(eligible.Select(value => value.FindingId).Order().ToArray());
        var source = await sourceReader.LoadAsync(audit.Id, audit.RequestedByUserId.Value, selection, cancellationToken);
        if (source is null)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-source-snapshot-invalid");
            return;
        }
        var preview = planner.Create(source);
        var contextuallyManual = ContextuallyManualFindingIds(preview.Operations);
        if (contextuallyManual.Count > 0)
        {
            eligible = eligible.Where(value => !contextuallyManual.Contains(value.FindingId)).ToArray();
            selection = new FixPlanSelection(eligible.Select(value => value.FindingId).Order().ToArray());
            source = await sourceReader.LoadAsync(audit.Id, audit.RequestedByUserId.Value, selection, cancellationToken);
            if (source is null)
            {
                Fail(orchestration, AutomaticRemediationState.Failed, "automatic-source-snapshot-invalid");
                return;
            }
            preview = planner.Create(source);
        }
        orchestration.EligibleFindingCount = eligible.Length;
        orchestration.OperationCount = preview.Operations.Count;
        if (preview.State == FixPlanState.Conflict)
        {
            Fail(orchestration, AutomaticRemediationState.Conflict, "automatic-operation-conflict");
            return;
        }
        if (preview.State != FixPlanState.Ready || preview.Operations.Count == 0
            || !MatchesPolicy(eligible, preview.Operations))
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-plan-not-ready");
            return;
        }

        try
        {
            var accepted = await executions.AcceptAsync(audit.Id, audit.RequestedByUserId.Value,
                CanonicalGuid(audit.Id, AutomaticRemediationPolicy.Version), selection,
                preview.PlanHash, cancellationToken);
            if (accepted is null)
            {
                Fail(orchestration, AutomaticRemediationState.Failed, "automatic-source-context-invalid");
                return;
            }
            orchestration.FixExecutionId = accepted.Id;
            orchestration.State = AutomaticRemediationState.Queued;
        }
        catch (FixExecutionException exception)
        {
            var conflict = exception.DiagnosticCode == "fix-source-version-superseded";
            Fail(orchestration, conflict ? AutomaticRemediationState.Conflict : AutomaticRemediationState.Failed,
                conflict ? "automatic-source-superseded" : "automatic-execution-create-failed");
        }
    }

    private async Task ObserveExecutionAsync(
        PpkiDbContext db,
        AutomaticRemediationOrchestration orchestration,
        AuditJob audit,
        CancellationToken cancellationToken)
    {
        if (orchestration.FixExecutionId is null || audit.RequestedByUserId is null)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-execution-lineage-missing");
            return;
        }
        var execution = await db.FixExecutionJobs.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == orchestration.FixExecutionId, cancellationToken);
        if (execution is null || execution.AuditJobId != audit.Id)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-execution-lineage-invalid");
            return;
        }
        if (execution.State == FixExecutionState.Queued) return;
        if (execution.State == FixExecutionState.Processing)
        {
            orchestration.State = AutomaticRemediationState.Processing;
            return;
        }
        if (execution.State == FixExecutionState.Failed)
        {
            Fail(orchestration, execution.FailureCategory == FixFailureCategory.Conflict
                ? AutomaticRemediationState.Conflict : AutomaticRemediationState.Failed,
                execution.SafeFailureCode == "fix-source-version-superseded"
                    ? "automatic-source-superseded" : "automatic-execution-failed");
            return;
        }
        if (execution.State == FixExecutionState.NoChange)
        {
            orchestration.State = AutomaticRemediationState.Completed;
            return;
        }
        if (execution.State != FixExecutionState.Completed || execution.ResultDocumentVersionId is null)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-execution-result-invalid");
            return;
        }

        try
        {
            var accepted = await reaudits.CreateAsync(execution.Id, audit.RequestedByUserId.Value, cancellationToken);
            if (accepted is null)
            {
                Fail(orchestration, AutomaticRemediationState.Failed, "automatic-reaudit-create-failed");
                return;
            }
            orchestration.ResultDocumentVersionId = execution.ResultDocumentVersionId;
            orchestration.ReauditJobId = accepted.AuditId;
            orchestration.State = AutomaticRemediationState.ReauditPending;
        }
        catch (ReauditException)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-reaudit-create-failed");
        }
    }

    private async Task ObserveReauditAsync(
        PpkiDbContext db,
        AutomaticRemediationOrchestration orchestration,
        AuditJob sourceAudit,
        CancellationToken cancellationToken)
    {
        if (orchestration.ReauditJobId is null || orchestration.FixExecutionId is null
            || sourceAudit.RequestedByUserId is null)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-reaudit-lineage-missing");
            return;
        }
        var reaudit = await db.AuditJobs.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == orchestration.ReauditJobId, cancellationToken);
        if (reaudit is null || reaudit.SourceFixExecutionId != orchestration.FixExecutionId)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-reaudit-lineage-invalid");
            return;
        }
        if (reaudit.Status is AuditJobStatus.Queued or AuditJobStatus.Processing) return;
        if (reaudit.Status != AuditJobStatus.Completed)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-reaudit-failed");
            return;
        }
        try
        {
            var reconciled = await resolutions.ReconcileAsync(orchestration.FixExecutionId.Value,
                sourceAudit.RequestedByUserId.Value, cancellationToken);
            if (reconciled is null || reconciled.State != FindingResolutionReconciliationState.Completed)
            {
                Fail(orchestration, AutomaticRemediationState.Failed, "automatic-reconciliation-failed");
                return;
            }
            orchestration.State = AutomaticRemediationState.Completed;
        }
        catch (FindingResolutionException)
        {
            Fail(orchestration, AutomaticRemediationState.Failed, "automatic-reconciliation-failed");
        }
    }

    internal static bool MatchesPolicy(
        IReadOnlyList<FixPlanFindingSnapshot> findings,
        IReadOnlyList<FixPlanOperation> operations)
    {
        var byId = findings.ToDictionary(value => value.FindingId);
        return operations.All(operation => operation.SourceFindingIds.Count > 0
            && operation.SourceFindingIds.All(id => byId.TryGetValue(id, out var finding)
                && AutomaticRemediationPolicy.TryGetAutoApply(finding, out var contract)
                && string.Equals(contract.CapabilityId, operation.CapabilityId, StringComparison.Ordinal)
                && string.Equals(contract.CapabilityVersion, operation.CapabilityVersion, StringComparison.Ordinal)));
    }

    internal static IReadOnlySet<Guid> ContextuallyManualFindingIds(IReadOnlyList<FixPlanOperation> operations)
    {
        var result = new HashSet<Guid>();
        var abstractParagraphs = operations
            .Where(value => value.ValidationKey.StartsWith("abstract.", StringComparison.Ordinal))
            .Select(ParagraphTargetKey).ToHashSet(StringComparer.Ordinal);
        foreach (var operation in operations.Where(value => value.ValidationKey.StartsWith("body.", StringComparison.Ordinal)
            && abstractParagraphs.Contains(ParagraphTargetKey(value))))
            result.UnionWith(operation.SourceFindingIds);
        foreach (var group in operations.GroupBy(OperationTargetKey, StringComparer.Ordinal))
        {
            var values = group.ToArray();
            if (values.Select(value => value.CapabilityId).Distinct(StringComparer.Ordinal).Count() < 2
                || values.Select(OperationMeaning).Distinct(StringComparer.Ordinal).Count() != 1
                || !values.Any(value => value.ValidationKey.StartsWith("abstract.", StringComparison.Ordinal)))
                continue;
            foreach (var operation in values.Where(value => value.ValidationKey.StartsWith("body.", StringComparison.Ordinal)))
                result.UnionWith(operation.SourceFindingIds);
        }
        return result;
    }

    internal static Guid CanonicalGuid(Guid auditId, string policyVersion)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{auditId:D}\n{policyVersion}"));
        var value = bytes[..16];
        value[6] = (byte)((value[6] & 0x0f) | 0x50);
        value[8] = (byte)((value[8] & 0x3f) | 0x80);
        return new Guid(value);
    }

    private static void Fail(
        AutomaticRemediationOrchestration orchestration,
        AutomaticRemediationState state,
        string code)
    {
        orchestration.State = state;
        orchestration.SafeFailureCode = code;
    }

    private static string OperationTargetKey(FixPlanOperation value) => string.Join("/",
        value.Target.Scope, value.Target.BodyElementIndex, value.Target.SectionIndex,
        value.Target.ParagraphIndex, value.Target.RunIndex, value.PropertyIdentifier);

    private static string ParagraphTargetKey(FixPlanOperation value) => string.Join("/",
        value.Target.Scope.StartsWith("main-document", StringComparison.Ordinal) ? "main-document" : value.Target.Scope,
        value.Target.BodyElementIndex, value.Target.SectionIndex, value.Target.ParagraphIndex);

    private static string OperationMeaning(FixPlanOperation value) => string.Join("/",
        value.OperationKind, value.Expected.Type, value.Expected.Value, value.PreconditionCode);
}
