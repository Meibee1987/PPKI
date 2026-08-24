import type { AuditFindingDetail, StructuralFindingExcerpt } from "./audit-contract.ts";
import type { CanonicalAuditIdentity } from "./canonical-audit-identity.ts";

export function findingDetailRequestKey(identity: CanonicalAuditIdentity, findingId: string): string {
  return `${identity.auditId}:${identity.documentVersionId}:${findingId}`;
}

export function assertCanonicalFindingDetail(
  identity: Pick<CanonicalAuditIdentity, "auditId" | "documentVersionId">,
  findingId: string,
  detail: AuditFindingDetail,
): AuditFindingDetail {
  if (detail.id !== findingId || detail.auditId !== identity.auditId
      || detail.documentVersionId !== identity.documentVersionId)
    throw new Error("canonical-finding-detail-lineage-invalid");
  return detail;
}

export function assertCanonicalStructuralExcerpt(
  identity: Pick<CanonicalAuditIdentity, "documentVersionId">,
  findingId: string,
  excerpt: StructuralFindingExcerpt,
): StructuralFindingExcerpt {
  if (excerpt.findingId !== findingId || excerpt.documentVersionId !== identity.documentVersionId)
    throw new Error("canonical-finding-excerpt-lineage-invalid");
  return excerpt;
}
