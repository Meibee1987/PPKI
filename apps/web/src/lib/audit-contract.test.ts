import assert from "node:assert/strict";
import test from "node:test";
import { auditFindingDetailPath, auditFindingsPath, auditSummaryPath, findingsQuery, normalizeFindingFilters, parseAuditFindingDetail, parseAuditFindingPage, parseAuditSummary } from "./audit-contract.ts";

const auditId = "11111111-1111-4111-8111-111111111111";
const findingId = "22222222-2222-4222-8222-222222222222";
const versionId = "33333333-3333-4333-8333-333333333333";
const profileId = "44444444-4444-4444-8444-444444444444";

const summary = {
  id: auditId, status: "Completed", documentVersionId: versionId, profileVersionId: profileId,
  documentKindSnapshot: "Skripsi", resolvedRuleSetHash: "abcdef", applicableRuleCount: 10, totalRules: 10,
  persistedFindingCount: 1, findingCount: 1, errorCount: 1, warningCount: 0, infoCount: 0,
  severity: { error: 1, warning: 0, info: 0 }, domains: [{ domain: "Layout", findingCount: 1 }],
  fixModes: { auto: 0, confirm: 0, manual: 1, report: 0 }, scoreState: "NotConfigured", score: null,
  scorePolicyVersion: null, scoreBreakdown: null, scoreDiagnosticCode: "scoring-policy-not-configured",
  startedAt: "2026-08-04T10:00:00Z", completedAt: "2026-08-04T10:01:00Z", failureCode: null, errorMessage: null,
  automaticRemediation: null,
  documentRender: { state: "Completed", pageCount: 7, rendererVersion: "8.34.0+libreoffice-26.2.4.2", rendererContractVersion: "docx-pdf/1.0", fontProfileVersion: "ppki-liberation-noto/1.0", pageMapVersion: "page-map/1.0", safeFailureCode: null, previewAvailable: true },
};

const finding = {
  id: findingId, auditId, ruleOrdinal: 1, ruleCode: "PPKI-LAYOUT-001", domain: "Layout", validationKey: "page.size",
  element: "Page", severity: "Error", fixMode: "Manual", findingState: "Open", reasonCode: "page-size-invalid",
  message: "page-size-invalid", actual: { Property: "width", RawValue: "200" }, expected: { Property: "width", AcceptedValues: ["210"] },
  location: { CompactLocation: "document", SectionIndex: null, BodyElementIndex: null, ParagraphIndex: null, RunIndex: null },
  confidence: 1, source: { sourceSection: "Format", pdfPage: 12, printedPage: "9" }, actionAvailability: "None",
  pageLocation: { pageNumber: 2, confidence: "Exact", state: "Completed" },
};

test("parses a valid audit summary without calculating score", () => {
  const parsed = parseAuditSummary(summary);
  assert.equal(parsed.scoreState, "NotConfigured"); assert.equal(parsed.score, null); assert.equal(parsed.severity.error, 1);
});

test("parses canonical automatic remediation progress", () => {
  const parsed = parseAuditSummary({ ...summary, automaticRemediation: { state: "ReauditPending", policyVersion: "auto-format/1.0", eligibleFindingCount: 8, operationCount: 8, verifiedResolvedCount: 0, stillDetectedCount: 0, failureCode: null } });
  assert.equal(parsed.automaticRemediation?.state, "ReauditPending");
  assert.equal(parsed.automaticRemediation?.operationCount, 8);
});

test("parses the completed backend wire summary with deterministic profile UUID and null score", () => {
  const parsed = parseAuditSummary({
    ...summary,
    profileVersionId: "21000000-0000-0000-0000-000000000001",
    persistedFindingCount: 2228,
    findingCount: 2228,
    errorCount: 2228,
    severity: { error: 2228, warning: 0, info: 0 },
    domains: [{ domain: "LAY", findingCount: 2228 }],
  });
  assert.equal(parsed.status, "Completed");
  assert.equal(parsed.persistedFindingCount, 2228);
  assert.equal(parsed.scoreState, "NotConfigured");
  assert.equal(parsed.score, null);
});

test("rejects a stale numeric scoreState from the backend", () => {
  assert.throws(() => parseAuditSummary({ ...summary, scoreState: 1 }), /kontrak/);
});

test("parses a paginated findings response and preserves backend ordering", () => {
  const second = { ...finding, id: "55555555-5555-4555-8555-555555555555", ruleOrdinal: 2 };
  const parsed = parseAuditFindingPage({ page: 1, pageSize: 25, totalCount: 2, items: [finding, second] });
  assert.deepEqual(parsed.items.map(item => item.id), [finding.id, second.id]);
});

test("parses a bounded first page when findings exceed one page", () => {
  const items = Array.from({ length: 25 }, (_, index) => ({
    ...finding,
    id: `55555555-5555-4555-8555-${String(index + 1).padStart(12, "0")}`,
  }));
  const parsed = parseAuditFindingPage({ page: 1, pageSize: 25, totalCount: 2228, items });
  assert.equal(parsed.page, 1);
  assert.equal(parsed.pageSize, 25);
  assert.equal(parsed.totalCount, 2228);
  assert.equal(parsed.items.length, 25);
});

test("parses explicit null and empty safe finding variants", () => {
  const parsed = parseAuditFindingPage({
    page: 2,
    pageSize: 25,
    totalCount: 2228,
    items: [{
      ...finding,
      actual: null,
      expected: {},
      location: { CompactLocation: "", SectionIndex: null, ParagraphIndex: 0 },
      confidence: null,
      source: { sourceSection: null, pdfPage: null, printedPage: null },
    }],
  });
  assert.equal(parsed.items[0].actual, null);
  assert.deepEqual(parsed.items[0].expected, {});
  assert.equal(parsed.items[0].confidence, null);
  assert.equal(parsed.items[0].source.sourceSection, null);
});

test("does not parse a problem response as a success DTO", () => {
  assert.throws(() => parseAuditSummary({
    type: "about:blank",
    title: "Invalid request",
    status: 400,
    code: "finding-pagination-invalid",
  }), /kontrak/);
});

test("finding wire shape excludes raw transport and sensitive top-level fields", () => {
  const parsed = parseAuditFindingPage({ page: 1, pageSize: 25, totalCount: 1, items: [finding] });
  const keys = Object.keys(parsed.items[0]);
  for (const forbidden of ["actualJson", "expectedJson", "actualValueJson", "expectedValueJson", "path", "filename", "text", "xml", "url", "secret"])
    assert.equal(keys.includes(forbidden), false);
  assert.equal(typeof parsed.items[0].actual, "object");
  assert.equal(typeof parsed.items[0].expected, "object");
});

test("parses finding detail snapshot fields", () => {
  const parsed = parseAuditFindingDetail({ ...finding, documentVersionId: versionId });
  assert.equal(parsed.validationKey, "page.size"); assert.equal(parsed.actionAvailability, "None");
});

test("parses exact structural page location and canonical render state", () => {
  const parsed = parseAuditFindingPage({ page: 1, pageSize: 25, totalCount: 1, items: [finding] });
  assert.deepEqual(parsed.items[0].pageLocation, { pageNumber: 2, confidence: "Exact", state: "Completed" });
  assert.equal(parseAuditSummary(summary).documentRender.pageMapVersion, "page-map/1.0");
});

test("rejects fabricated zero page numbers", () => assert.throws(() => parseAuditFindingPage({ page: 1, pageSize: 25, totalCount: 1, items: [{ ...finding, pageLocation: { pageNumber: 0, confidence: "Exact", state: "Completed" } }] }), /kontrak/));

test("rejects invalid summary enum with a controlled error", () => assert.throws(() => parseAuditSummary({ ...summary, status: "Unknown" }), /kontrak/));
test("rejects invalid finding enum with a controlled error", () => assert.throws(() => parseAuditFindingPage({ page: 1, pageSize: 25, totalCount: 1, items: [{ ...finding, severity: "Critical" }] }), /kontrak/));
test("rejects malformed pagination shape", () => assert.throws(() => parseAuditFindingPage({ page: 0, pageSize: 101, totalCount: 0, items: [] }), /kontrak/));
test("rejects invalid UUIDs", () => assert.throws(() => parseAuditFindingDetail({ ...finding, documentVersionId: "bad" }), /kontrak/));

test("normalizes default page and page size", () => assert.deepEqual(normalizeFindingFilters(new URLSearchParams()), { page: 1, pageSize: 25 }));
test("normalizes enum filters case-insensitively", () => { const value = normalizeFindingFilters(new URLSearchParams("severity=warning&fixMode=manual")); assert.equal(value.severity, "Warning"); assert.equal(value.fixMode, "Manual"); });
test("preserves exact text filters", () => { const value = normalizeFindingFilters(new URLSearchParams("domain=Layout&ruleCode=PPKI-1&validationKey=page.size")); assert.equal(value.domain, "Layout"); assert.equal(value.ruleCode, "PPKI-1"); assert.equal(value.validationKey, "page.size"); });
test("ignores unknown enum and overlong text filters", () => { const value = normalizeFindingFilters(new URLSearchParams(`severity=Critical&domain=${"x".repeat(129)}`)); assert.equal(value.severity, undefined); assert.equal(value.domain, undefined); });
test("normalizes invalid page values safely", () => { assert.equal(normalizeFindingFilters(new URLSearchParams("page=-1&pageSize=101")).page, 1); assert.equal(normalizeFindingFilters(new URLSearchParams("page=-1&pageSize=101")).pageSize, 25); });
test("normalizes pagination whose offset exceeds the backend cap", () => assert.equal(normalizeFindingFilters(new URLSearchParams("page=401&pageSize=25")).page, 1));
test("bounds page size at 100", () => assert.equal(normalizeFindingFilters(new URLSearchParams("pageSize=100")).pageSize, 100));
test("serializes supported filters and pagination", () => assert.equal(findingsQuery({ severity: "Error", ruleCode: "PPKI-1", page: 2, pageSize: 25 }), "severity=Error&ruleCode=PPKI-1&page=2&pageSize=25"));
test("API paths contain audit and finding identifiers but never owner ID", () => { const filters = { page: 1, pageSize: 25 }; assert.equal(auditSummaryPath(auditId), `/api/audits/${auditId}`); assert.match(auditFindingsPath(auditId, filters), /findings\?page=1/); assert.match(auditFindingDetailPath(auditId, findingId), new RegExp(findingId)); assert.doesNotMatch(auditFindingsPath(auditId, filters), /owner/i); });
