import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (relative: string) => readFileSync(new URL(relative, import.meta.url), "utf8");
const drawer = read("../components/finding-detail-drawer.tsx");
const list = read("../components/audit-finding-list.tsx");
const contract = read("./audit-contract.ts");
const api = read("./api.ts");

test("selecting one list finding opens exactly one drawer without route navigation", () => {
  assert.match(list, /setSelectedFindingId/);
  assert.match(list, /onClick=\{\(\) => selectFinding\(item\.id\)\}/);
  assert.match(list, /selectedFindingId && <FindingDetailDrawer/);
  assert.doesNotMatch(drawer, /useRouter|router\.push|window\.location/);
});

test("detail request and rendered identity use the selected finding ID", () => {
  assert.match(drawer, /getAuditFinding\(identity\.auditId, findingId, controller\.signal\)/);
  assert.match(drawer, /assertCanonicalFindingDetail\(identity, findingId, value\)/);
  assert.match(drawer, /findingDetailRequestKey\(identity, findingId\)/);
});

test("rule code, safe rule title, and description come from authoritative presentation", () => {
  assert.match(drawer, /detail\.ruleCode/);
  assert.match(drawer, /detail\.presentation\.propertyLabel/);
  assert.match(drawer, /detail\.presentation\.problem/);
  assert.doesNotMatch(drawer, /dangerouslySetInnerHTML/);
});

test("typed severity, fix mode, finding status, disposition, resolution, and review are displayed", () => {
  for (const field of ["severity", "fixMode", "findingState", "disposition", "resolutionState", "reviewState"])
    assert.match(drawer, new RegExp(`detail\\.${field}`));
  for (const values of ["findingStates", "findingDispositions", "findingResolutionStates", "findingReviewStates"])
    assert.match(contract, new RegExp(values));
});

test("Actual and Expected use the bounded backend presentation rather than raw JSON", () => {
  assert.match(drawer, /Aktual \(Actual\)/);
  assert.match(drawer, /Diharapkan \(Expected\)/);
  assert.match(drawer, /presentation\.beforeValue/);
  assert.match(drawer, /presentation\.expectedValue/);
  assert.doesNotMatch(drawer, /JSON\.stringify|detail\.actual|detail\.expected/);
});

test("location and page presentation use authoritative components without fabricating pages", () => {
  assert.match(drawer, /<FindingLocation value=\{detail\.location\}/);
  assert.match(drawer, /<DocumentPageLocation versionId=\{detail\.documentVersionId\} value=\{detail\.pageLocation\}/);
  assert.doesNotMatch(drawer, /pageNumber\s*\+|Math\..*page/);
});

test("missing evidence, confidence, source, and excerpt render safe unavailable states", () => {
  for (const copy of ["Nilai aman tidak tersedia", "Tidak tersedia", "Referensi sumber tidak tersedia", "Cuplikan dokumen tidak tersedia"])
    assert.match(drawer, new RegExp(copy));
});

test("closing detail preserves filter, search, page, and list results", () => {
  assert.match(list, /closeDetail = useCallback\(\(\) => setSelectedFindingId\(undefined\)/);
  assert.doesNotMatch(drawer, /findingsQuery|normalizeFindingFilters|setLoaded/);
  assert.match(list, /filters=\{visible\.filters\} page=\{visible\.page\}/);
});

test("previous or next selection replaces the active detail deterministically", () => {
  assert.match(drawer, /pageFindingIds\.indexOf\(findingId\)/);
  assert.match(drawer, /onSelect\(previousId\)/);
  assert.match(drawer, /onSelect\(nextId\)/);
  assert.match(drawer, /setDetail\(undefined\)/);
});

test("stale selection cannot install detail and active requests abort on replacement or unmount", () => {
  assert.match(drawer, /requests\.current\.begin/);
  assert.match(drawer, /requests\.current\.isCurrent\(token\)/);
  assert.match(drawer, /controller\.abort\(\)/);
  assert.match(drawer, /requests\.current\.cancel\(token\)/);
});

test("a new page or filter closes detail when the selected finding is absent", () => {
  assert.match(list, /value\.items\.some\(item => item\.id === selected\) \? selected : undefined/);
});

test("AbortError is silent and 401 remains delegated to S6-T01 transport", () => {
  assert.match(drawer, /!isApiRequestAborted\(value\)/);
  assert.match(api, /if \(response\.status === 401\) dependencies\.onUnauthorized\(\)/);
  assert.doesNotMatch(drawer, /Authorization|fetch\(/);
});

test("safe detail errors never render exception details or raw response bodies", () => {
  assert.match(drawer, /value instanceof ApiRequestError \? value\.message/);
  assert.doesNotMatch(drawer, /value\.stack|value\.problem\.detail|JSON\.stringify\(value\)/);
});

test("structural excerpt is requested only by explicit user action", () => {
  assert.match(drawer, /async function loadExcerpt\(\)/);
  assert.match(drawer, /onClick=\{loadExcerpt\}>Lihat bagian dokumen/);
  assert.match(drawer, /getStructuralFindingExcerpt\(identity\.auditId, findingId, controller\.signal\)/);
  assert.doesNotMatch(drawer, /useEffect\([\s\S]{0,400}getStructuralFindingExcerpt/);
});

test("excerpt is bound to selected finding and document version and kept only in component state", () => {
  assert.match(drawer, /assertCanonicalStructuralExcerpt\(identity, findingId/);
  assert.match(drawer, /setExcerpt\(\{ state: "Idle" \}\)/);
  assert.doesNotMatch(drawer, /localStorage|sessionStorage|indexedDB/);
});

test("source section, PDF page, and printed page are shown only when present", () => {
  assert.match(drawer, /source\.sourceSection/);
  assert.match(drawer, /source\.pdfPage !== null/);
  assert.match(drawer, /source\.printedPage/);
});

test("automatic capability is backend-owned and never derived from severity", () => {
  assert.match(drawer, /detail\.actionAvailability === "Automatic"/);
  assert.doesNotMatch(drawer, /severity[^\n]*(?:Automatic|actionAvailability)/);
});

test("opening detail does not alter bounded pagination or S6-T03 summary readiness", () => {
  assert.match(list, /page\.items\.map/);
  assert.doesNotMatch(drawer, /setSummary|blockingFindingCount|readinessState|pageSize:\s*(?:1000|summary)/);
});

test("drawer introduces no S6-T06 mutation, review, ignore, or approval actions", () => {
  assert.doesNotMatch(drawer, /submitTextCorrectionDecision|requestFindingReview|method:\s*["'](?:POST|PUT|PATCH|DELETE)|onClick=.*(?:Ignore|Approve|Reject)/i);
  assert.match(drawer, /Detail ini hanya menampilkan status authoritative/);
});

test("dialog has accessible heading, named close, Escape handling, and focus trap", () => {
  assert.match(drawer, /role="dialog" aria-modal="true" aria-labelledby="finding-detail-title"/);
  assert.match(drawer, /aria-label="Tutup detail temuan"/);
  assert.match(drawer, /event\.key === "Escape"/);
  assert.match(drawer, /event\.key !== "Tab"/);
  assert.match(drawer, /previousFocus\?\.focus\(\)/);
});

test("long metadata and locations are handled by bounded presenters and wrapping styles", () => {
  const styles = read("../app/globals.css");
  assert.match(styles, /finding-drawer[^\n]*overflow-y:auto/);
  assert.match(styles, /drawer-metadata dd[^\n]*overflow-wrap:anywhere/);
  assert.match(styles, /drawer-comparison p[^\n]*overflow-wrap:anywhere/);
});
