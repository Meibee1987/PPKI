import assert from "node:assert/strict";
import test from "node:test";
import type { AuditFindingPage } from "./audit-contract.ts";
import { assertCanonicalFindingPage, createLatestFindingRequestGuard, findingRequestKey, hasFindingQuery } from "./finding-list-model.ts";

const auditId = "11111111-1111-4111-8111-111111111111";
const documentVersionId = "22222222-2222-4222-8222-222222222222";
const identity = { routeAuditId: auditId, auditId, documentVersionId };
const page = { auditId, documentVersionId, page: 1, pageSize: 25, totalCount: 0, items: [] } satisfies AuditFindingPage;

test("request identity includes canonical lineage and every server query value", () => {
  const key = findingRequestKey(identity, { severity: "Error", fixMode: "Manual", disposition: "RequiresReview", domain: "Layout", search: "margin", page: 2, pageSize: 25 });
  assert.match(key, new RegExp(`^${auditId}:${documentVersionId}\\?`));
  for (const value of ["severity=Error", "fixMode=Manual", "disposition=RequiresReview", "domain=Layout", "search=margin", "page=2", "pageSize=25"])
    assert.match(key, new RegExp(value));
});

test("a later page or search request invalidates every earlier response", () => {
  const guard = createLatestFindingRequestGuard();
  const firstPage = guard.begin(findingRequestKey(identity, { page: 1, pageSize: 25 }));
  const secondPage = guard.begin(findingRequestKey(identity, { page: 2, pageSize: 25 }));
  assert.equal(guard.isCurrent(firstPage), false);
  assert.equal(guard.isCurrent(secondPage), true);
  const searched = guard.begin(findingRequestKey(identity, { search: "heading", page: 1, pageSize: 25 }));
  assert.equal(guard.isCurrent(secondPage), false);
  assert.equal(guard.isCurrent(searched), true);
  guard.cancel(searched);
  assert.equal(guard.isCurrent(searched), false);
});

test("canonical page identity rejects old audits and document versions", () => {
  assert.equal(assertCanonicalFindingPage(identity, page), page);
  assert.throws(() => assertCanonicalFindingPage(identity, { ...page, auditId: "33333333-3333-4333-8333-333333333333" }), /lineage/);
  assert.throws(() => assertCanonicalFindingPage(identity, { ...page, documentVersionId: "33333333-3333-4333-8333-333333333333" }), /lineage/);
});

test("filtered-empty state recognizes only authoritative query fields", () => {
  assert.equal(hasFindingQuery({ page: 1, pageSize: 25 }), false);
  assert.equal(hasFindingQuery({ search: "rule", page: 1, pageSize: 25 }), true);
  assert.equal(hasFindingQuery({ severity: "Warning", page: 1, pageSize: 25 }), true);
});
