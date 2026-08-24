import assert from "node:assert/strict";
import test from "node:test";
import { assertCanonicalFindingReview, findingReviewIdentityKey, maximumFindingReviewReasonLength, validateFindingReviewReason } from "./finding-review-model.ts";
import type { FindingReview } from "./remediation-contract.ts";

const identity = { routeAuditId: "10000000-0000-0000-0000-000000000001", auditId: "10000000-0000-0000-0000-000000000001", documentVersionId: "20000000-0000-0000-0000-000000000002" };
const findingId = "30000000-0000-0000-0000-000000000003";
const review = (): FindingReview => ({ reviewCaseId: null, findingId, auditId: identity.auditId, sourceDocumentVersionId: identity.documentVersionId, resolutionState: "Open", reviewState: "NoReview", requestedDisposition: null, permissions: { canRequestReview: true, canReportManualRemediation: false, canDecide: false }, allowedDecisions: [], events: [] });

test("review reason is mandatory after trimming", () => {
  assert.equal(validateFindingReviewReason(""), "Alasan wajib diisi.");
  assert.equal(validateFindingReviewReason("   "), "Alasan wajib diisi.");
  assert.equal(validateFindingReviewReason(" alasan jelas "), null);
});

test("review reason uses the backend 1000 character bound", () => {
  assert.equal(maximumFindingReviewReasonLength, 1000);
  assert.equal(validateFindingReviewReason("a".repeat(1000)), null);
  assert.equal(validateFindingReviewReason("a".repeat(1001)), "Alasan maksimum 1.000 karakter.");
});

test("control characters are rejected before submission", () => {
  assert.match(validateFindingReviewReason("baris\nbaru") ?? "", /satu baris/);
  assert.match(validateFindingReviewReason("tab\tvalue") ?? "", /satu baris/);
});

test("review identity includes audit version and finding", () => {
  assert.equal(findingReviewIdentityKey(identity, findingId), `${identity.auditId}:${identity.documentVersionId}:${findingId}`);
});

test("canonical review accepts only the active lineage", () => {
  assert.equal(assertCanonicalFindingReview(identity, findingId, review()).findingId, findingId);
  assert.throws(() => assertCanonicalFindingReview(identity, "40000000-0000-0000-0000-000000000004", review()));
  assert.throws(() => assertCanonicalFindingReview({ ...identity, documentVersionId: "50000000-0000-0000-0000-000000000005" }, findingId, review()));
});
