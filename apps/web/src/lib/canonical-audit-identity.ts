import type { AuditSummary } from "./audit-contract";
import type { CorrectionBatchStatus } from "./text-correction-contract";

export type CanonicalAuditIdentity = {
  routeAuditId: string;
  auditId: string;
  documentVersionId: string;
};

export function canonicalIdentityFromRouteSummary(
  routeAuditId: string,
  summary: AuditSummary,
): CanonicalAuditIdentity {
  const automatic = summary.automaticRemediation;
  if (automatic?.state === "Completed"
      && automatic.reauditJobId
      && automatic.resultDocumentVersionId) {
    return {
      routeAuditId,
      auditId: automatic.reauditJobId,
      documentVersionId: automatic.resultDocumentVersionId,
    };
  }
  return { routeAuditId, auditId: summary.id, documentVersionId: summary.documentVersionId };
}

export function canonicalIdentityFromCompletedBatch(
  current: CanonicalAuditIdentity,
  batch: CorrectionBatchStatus,
): CanonicalAuditIdentity {
  if (batch.state !== "Completed" || !batch.reauditId || !batch.resultDocumentVersionId) return current;
  return { ...current, auditId: batch.reauditId, documentVersionId: batch.resultDocumentVersionId };
}

export function assertCanonicalSummary(
  identity: CanonicalAuditIdentity,
  summary: AuditSummary,
): AuditSummary {
  if (summary.id !== identity.auditId || summary.documentVersionId !== identity.documentVersionId)
    throw new Error("canonical-audit-lineage-invalid");
  return summary;
}
