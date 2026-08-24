import assert from "node:assert/strict";
import test from "node:test";
import { parseAuditAccepted, parseDocumentCreated, parseDocumentDetail, parseDocumentList } from "./document-contract.ts";

const documentId = "11111111-1111-4111-8111-111111111111";
const versionId = "22222222-2222-4222-8222-222222222222";
const auditId = "33333333-3333-4333-8333-333333333333";
const timestamp = "2026-08-24T10:00:00Z";
const audit = { id: auditId, status: "Completed", score: null, errorCount: 2, warningCount: 1, infoCount: 0 };

test("parses representative document list and nullable latest audit", () => {
  const parsed = parseDocumentList([
    { id: documentId, title: "Skripsi", documentType: "Skripsi", currentVersionNo: 1, updatedAt: timestamp, latestAudit: audit },
    { id: "44444444-4444-4444-8444-444444444444", title: "Tesis", documentType: "Tesis", currentVersionNo: 1, updatedAt: timestamp, latestAudit: null },
  ]);
  assert.equal(parsed[0].latestAudit?.status, "Completed"); assert.equal(parsed[1].latestAudit, null);
});

test("parses document versions and nested audit results without renaming wire fields", () => {
  const parsed = parseDocumentDetail({ id: documentId, title: "Skripsi", documentType: "Skripsi", currentVersionNo: 1,
    createdAt: timestamp, updatedAt: timestamp, versions: [{ id: versionId, versionNo: 1, parentVersionId: null,
      originalFilename: "source.docx", sizeBytes: 123, sha256: "abc", createdAt: timestamp,
      audits: [{ ...audit, createdAt: timestamp }] }] });
  assert.equal(parsed.versions[0].parentVersionId, null); assert.equal(parsed.versions[0].audits[0].errorCount, 2);
});

test("parses document creation and audit acceptance contracts", () => {
  assert.equal(parseDocumentCreated({ id: documentId, versionId, title: "Skripsi", currentVersionNo: 1, sha256: "abc" }).versionId, versionId);
  assert.deepEqual(parseAuditAccepted({ id: auditId, status: "Queued" }), { id: auditId, status: "Queued" });
});

test("rejects malformed representative document contracts", () => {
  assert.throws(() => parseDocumentList([{ id: "bad" }]), /kontrak dokumen/);
  assert.throws(() => parseAuditAccepted({ id: auditId, status: "Unknown" }), /kontrak dokumen/);
});
