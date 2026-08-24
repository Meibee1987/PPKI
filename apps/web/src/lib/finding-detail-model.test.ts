import assert from "node:assert/strict";
import test from "node:test";
import type { AuditFindingDetail, StructuralFindingExcerpt } from "./audit-contract.ts";
import { assertCanonicalFindingDetail, assertCanonicalStructuralExcerpt, findingDetailRequestKey } from "./finding-detail-model.ts";
import { createLatestFindingRequestGuard } from "./finding-list-model.ts";

const auditId = "11111111-1111-4111-8111-111111111111";
const documentVersionId = "22222222-2222-4222-8222-222222222222";
const findingId = "33333333-3333-4333-8333-333333333333";
const identity = { routeAuditId: auditId, auditId, documentVersionId };
const detail = { id: findingId, auditId, documentVersionId } as AuditFindingDetail;
const excerpt = { findingId, documentVersionId } as StructuralFindingExcerpt;

test("detail request identity binds audit, document version, and selected finding", () => {
  assert.equal(findingDetailRequestKey(identity, findingId), `${auditId}:${documentVersionId}:${findingId}`);
});

test("detail accepts only the selected canonical finding", () => {
  assert.equal(assertCanonicalFindingDetail(identity, findingId, detail), detail);
  assert.throws(() => assertCanonicalFindingDetail(identity, findingId, { ...detail, id: auditId }), /lineage/);
  assert.throws(() => assertCanonicalFindingDetail(identity, findingId, { ...detail, auditId: findingId }), /lineage/);
  assert.throws(() => assertCanonicalFindingDetail(identity, findingId, { ...detail, documentVersionId: findingId }), /lineage/);
});

test("excerpt accepts only the active finding and audited document version", () => {
  assert.equal(assertCanonicalStructuralExcerpt(identity, findingId, excerpt), excerpt);
  assert.throws(() => assertCanonicalStructuralExcerpt(identity, findingId, { ...excerpt, findingId: auditId }), /lineage/);
  assert.throws(() => assertCanonicalStructuralExcerpt(identity, findingId, { ...excerpt, documentVersionId: auditId }), /lineage/);
});

test("a newer finding selection invalidates a late detail response", () => {
  const guard = createLatestFindingRequestGuard();
  const first = guard.begin(findingDetailRequestKey(identity, findingId));
  const secondFindingId = "44444444-4444-4444-8444-444444444444";
  const second = guard.begin(findingDetailRequestKey(identity, secondFindingId));
  assert.equal(guard.isCurrent(first), false);
  assert.equal(guard.isCurrent(second), true);
  guard.cancel(second);
  assert.equal(guard.isCurrent(second), false);
});
