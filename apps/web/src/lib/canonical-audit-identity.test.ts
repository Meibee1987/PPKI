import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { parseAuditSummary } from "./audit-contract.ts";
import { assertCanonicalSummary, canonicalIdentityFromCompletedBatch, canonicalIdentityFromRouteSummary } from "./canonical-audit-identity.ts";
import { textCorrectionBatchPath, textCorrectionsPath } from "./text-correction-paths.ts";
import { parseCorrectionBatchStatus } from "./text-correction-contract.ts";

const a1 = "11111111-1111-4111-8111-111111111111";
const a2 = "22222222-2222-4222-8222-222222222222";
const a3 = "33333333-3333-4333-8333-333333333333";
const v1 = "44444444-4444-4444-8444-444444444444";
const v2 = "55555555-5555-4555-8555-555555555555";
const v3 = "66666666-6666-4666-8666-666666666666";
const profile = "77777777-7777-4777-8777-777777777777";
const component = await readFile(new URL("../components/streamlined-audit-client.tsx", import.meta.url), "utf8");

function summary(id: string, version: string, automaticRemediation: unknown = null) {
  return parseAuditSummary({
    id, status: "Completed", documentVersionId: version, profileVersionId: profile,
    documentKindSnapshot: "Skripsi", resolvedRuleSetHash: "a".repeat(64), applicableRuleCount: 1,
    totalRules: 1, persistedFindingCount: 197, findingCount: 197, errorCount: 197, warningCount: 0,
    infoCount: 0, severity: { error: 197, warning: 0, info: 0 }, domains: [],
    fixModes: { auto: 0, confirm: 0, manual: 197, report: 0 }, scoreState: "NotConfigured", score: null,
    scorePolicyVersion: null, scoreBreakdown: null, scoreDiagnosticCode: null, startedAt: null,
    completedAt: null, failureCode: null, errorMessage: null,
    correctionAnalysis: { state: "Completed" }, automaticRemediation,
    documentRender: { state: "Completed", pageCount: 1, rendererVersion: "r", rendererContractVersion: "c", fontProfileVersion: "f", pageMapVersion: "p", safeFailureCode: null, previewAvailable: true },
  });
}

const automatic = (state: string) => ({ state, policyVersion: "auto-format/1.0", eligibleFindingCount: 2031,
  operationCount: 2031, verifiedResolvedCount: state === "Completed" ? 2031 : 0, stillDetectedCount: 197,
  failureCode: null, resultDocumentVersionId: state === "Completed" ? v2 : null, reauditJobId: state === "Completed" ? a2 : null });

test("A1 remains canonical before automatic completion", () => {
  assert.equal(canonicalIdentityFromRouteSummary(a1, summary(a1, v1, automatic("Processing"))).auditId, a1);
});

test("backend-owned completed lineage resolves route A1 to canonical A2/v2", () => {
  assert.deepEqual(canonicalIdentityFromRouteSummary(a1, summary(a1, v1, automatic("Completed"))),
    { routeAuditId: a1, auditId: a2, documentVersionId: v2 });
});

test("summary and correction endpoints share canonical A2", () => {
  const identity = canonicalIdentityFromRouteSummary(a1, summary(a1, v1, automatic("Completed")));
  assert.equal(assertCanonicalSummary(identity, summary(a2, v2)).id, a2);
  assert.equal(textCorrectionsPath(identity.auditId, 1, 25), `/api/audits/${a2}/text-corrections?page=1&pageSize=25`);
  assert.equal(textCorrectionBatchPath(identity.auditId), `/api/audits/${a2}/text-correction-batches`);
  assert.doesNotMatch(textCorrectionsPath(identity.auditId, 1, 25), new RegExp(a1));
});

test("completed correction batch advances A2/v2 to A3/v3", () => {
  const batch = parseCorrectionBatchStatus({ id: a3, sourceAuditId: a2, sourceDocumentVersionId: v2,
    fixExecutionId: a3, resultDocumentVersionId: v3, reauditId: a3, state: "Completed", decisionCount: 1,
    safeFailureCode: null, verificationCounts: { VerifiedResolved: 1 } });
  const identity = canonicalIdentityFromCompletedBatch({ routeAuditId: a1, auditId: a2, documentVersionId: v2 }, batch);
  assert.deepEqual(identity, { routeAuditId: a1, auditId: a3, documentVersionId: v3 });
  assert.equal(assertCanonicalSummary(identity, summary(a3, v3)).documentVersionId, v3);
});

test("component never polls stale route corrections and aborts identity changes", () => {
  assert.match(component, /listTextCorrections\(requestedAuditId/);
  assert.doesNotMatch(component, /listTextCorrections\(routeAuditId/);
  assert.match(component, /return \(\) => controller\.abort\(\)/);
  assert.doesNotMatch(component, /status === 404\)[^\n]*setTimeout/);
  assert.match(component, /batch\?\.state !== "Completed" \|\| !versionId/);
  assert.doesNotMatch(component, /resultDocumentVersionId\s*\?\?\s*summary\.documentVersionId/);
});
