import type { CanonicalAuditIdentity } from "./canonical-audit-identity.ts";
import type { FindingReview } from "./remediation-contract.ts";

export const maximumFindingReviewReasonLength = 1000;

export function validateFindingReviewReason(value: string): string | null {
  if (!value.trim()) return "Alasan wajib diisi.";
  if (value.length > maximumFindingReviewReasonLength) return "Alasan maksimum 1.000 karakter.";
  if (/[\u0000-\u001f\u007f-\u009f]/u.test(value)) return "Alasan harus berupa satu baris teks biasa.";
  return null;
}

export function findingReviewIdentityKey(identity: CanonicalAuditIdentity, findingId: string): string {
  return `${identity.auditId}:${identity.documentVersionId}:${findingId}`;
}

export function assertCanonicalFindingReview(
  identity: Pick<CanonicalAuditIdentity, "auditId" | "documentVersionId">,
  findingId: string,
  review: FindingReview,
): FindingReview {
  if (review.auditId !== identity.auditId || review.findingId !== findingId
      || review.sourceDocumentVersionId !== identity.documentVersionId)
    throw new Error("canonical-finding-review-lineage-invalid");
  return review;
}
