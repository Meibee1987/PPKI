import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (relative: string) => readFileSync(new URL(relative, import.meta.url), "utf8");
const component = read("../components/audit-finding-list.tsx");
const routeClient = read("../components/streamlined-audit-client.tsx");
const contract = read("./audit-contract.ts");
const model = read("./finding-list-model.ts");
const api = read("./api.ts");

test("routed audit uses one bounded server-authoritative finding page", () => {
  assert.match(routeClient, /<AuditFindingList key=\{current\.identity\.auditId\} identity=\{current\.identity\} summary=\{summary\}/);
  assert.match(component, /listAuditFindings\(identity\.auditId, filters, controller\.signal\)/);
  assert.doesNotMatch(component, /pageSize:\s*(?:1000|summary\.findingCount)|Promise\.all/);
  assert.deepEqual([...component.matchAll(/<option value="(10|25|50|100)">/g)].map(value => Number(value[1])), [10, 25, 50, 100]);
});

test("previous and next navigation request exact pages and disable impossible actions", () => {
  assert.match(component, /navigate\(\{ \.\.\.filters, page: page\.page - 1 \}\)/);
  assert.match(component, /navigate\(\{ \.\.\.filters, page: page\.page \+ 1 \}\)/);
  assert.match(component, /disabled=\{page\.page <= 1\}/);
  assert.match(component, /disabled=\{page\.page >= range\.totalPages\}/);
  assert.match(component, /Menampilkan \{range\.start\}–\{range\.end\} dari \{page\.totalCount\}/);
});

test("backend order is rendered directly without client sorting or cross-page merging", () => {
  assert.match(component, /page\.items\.map/);
  assert.doesNotMatch(component, /\.sort\(|\.reverse\(|\.concat\(/);
});

test("typed severity, fix mode, disposition, and domain filters compose in one submitted query", () => {
  for (const source of ["severities", "fixModes", "findingDispositions", "summary.domains"])
    assert.match(component, new RegExp(source.replace(".", "\\.")));
  for (const key of ["severity", "fixMode", "disposition", "domain", "pageSize"])
    assert.match(component, new RegExp(`query\\.set\\("${key}"`));
  assert.match(component, /navigate\(\{ \.\.\.normalizeFindingFilters\(query\), page: 1 \}\)/);
});

test("search is explicit, bounded, server-backed, and empty search clears normally", () => {
  assert.match(component, /<form[^>]*role="search"[^>]*onSubmit=\{apply\}/);
  assert.match(component, /type="search" maxLength=\{128\}/);
  assert.match(component, /if \(draft\.search\.trim\(\)\) query\.set\("search"/);
  assert.match(contract, /query\.set\("search", filters\.search\)/);
  assert.doesNotMatch(component, /items\.filter\([^)]*(?:search|ruleCode)/i);
});

test("initial, refreshing, audit-empty, filtered-empty, out-of-range, and safe error states differ", () => {
  for (const copy of ["Memuat halaman pertama temuan", "Memperbarui daftar temuan", "Audit ini tidak memiliki temuan", "Tidak ada temuan yang cocok", "Halaman tidak tersedia", "Daftar temuan tidak dapat dimuat"])
    assert.match(component, new RegExp(copy));
  assert.match(component, /summary\.findingCount === 0/);
  assert.match(component, /page\.totalCount === 0/);
});

test("stale requests are guarded and in-flight work is aborted without surfacing AbortError", () => {
  assert.match(component, /requests\.current\.begin\(findingRequestKey\(identity, filters\)\)/);
  assert.match(component, /requests\.current\.isCurrent\(token\)/);
  assert.match(component, /controller\.abort\(\)/);
  assert.match(component, /requests\.current\.cancel\(token\)/);
  assert.match(component, /!isApiRequestAborted\(value\)/);
});

test("canonical audit and document-version identity are checked before a page is installed", () => {
  assert.match(component, /assertCanonicalFindingPage\(identity, value\)/);
  assert.match(model, /page\.auditId !== identity\.auditId \|\| page\.documentVersionId !== identity\.documentVersionId/);
  assert.match(contract, /items\.some\(item => item\.auditId !== auditId\)/);
});

test("filter results never mutate or recompute the S6-T03 summary/readiness", () => {
  assert.match(component, /ringkasan audit di atas tidak berubah/i);
  assert.doesNotMatch(component, /setSummary|blockingFindingCount\s*[=<>]|readinessState\s*[=<>]/);
});

test("S6-T01 transport remains the only auth and cancellation implementation", () => {
  assert.doesNotMatch(component, /fetch\(|Authorization|window\.location/);
  assert.match(api, /if \(response\.status === 401\) dependencies\.onUnauthorized\(\)/);
  assert.match(api, /export function isApiRequestAborted/);
});
