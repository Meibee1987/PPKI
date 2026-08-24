import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (relative: string) => readFileSync(new URL(relative, import.meta.url), "utf8");
const page = read("../app/documents/[id]/page.tsx");
const observer = read("./audit-progress.ts");
const client = read("./document-api.ts");
const transport = read("./api.ts");
const backend = read("../../../../backend/services/Ppki.Api/Program.cs");

test("document UI exposes waiting, running, completed, and failed states", () => {
  for (const copy of ["Menunggu giliran", "Audit sedang berjalan", "Audit selesai", "Audit gagal"]) assert.match(page, new RegExp(copy));
});

test("polling reads only the canonical typed audit summary endpoint", () => {
  assert.match(page, /getAuditStatus/); assert.match(client, /parseAuditSummary/);
  assert.match(client, /\/api\/audits\/\$\{encodeURIComponent\(auditId\)\}/);
});

test("completed transition reloads document data and keeps the existing result route", () => {
  assert.match(page, /onCompleted:[\s\S]*getDocument\(id, signal\)/);
  assert.match(page, /href=\{`\/audits\/\$\{encodeURIComponent\(audit\.id\)\}`\}/);
});

test("polling lifecycle is bounded, sequential, and abortable", () => {
  assert.match(observer, /maximumAuditPolls = 60/); assert.match(observer, /inFlight/);
  assert.match(observer, /scheduleNext\(\)/); assert.match(observer, /active\?\.abort\(\)/);
  assert.doesNotMatch(observer, /setInterval/);
});

test("backend has no audit retry endpoint and failed UX creates only a new audit job", () => {
  assert.doesNotMatch(backend, /audits\/\{[^}]+\}\/retry|retry-audit/i);
  assert.doesNotMatch(page, /retryAudit|\/retry/);
  assert.match(page, /Audit ini tidak dapat dilanjutkan/);
  assert.match(page, /startAudit\(currentVersion\.id/);
});

test("duplicate audit creation is synchronously guarded", () => {
  assert.match(page, /submissionInFlight\.current/); assert.match(page, /disabled=\{submitting \|\| observing\}/);
});

test("route document changes cannot start polling with stale document state", () => {
  assert.match(page, /doc\.id !== id/); assert.match(page, /setDoc\(undefined\); setAudit\(undefined\)/);
});

test("401 remains delegated to the S6-T01 login redirect flow", () => {
  assert.match(transport, /response\.status === 401[^\n]*dependencies\.onUnauthorized/);
  assert.match(transport, /window\.location\.assign\(`\/login\?next=/);
  assert.match(page, /value instanceof ApiRequestError && value\.status === 401/);
});

test("failed UI never renders backend failure details", () => {
  assert.doesNotMatch(page, /errorMessage|failureCode|exception|stack|storageUrl|signedUrl/);
  assert.match(page, /Detail internal tidak ditampilkan/);
});
