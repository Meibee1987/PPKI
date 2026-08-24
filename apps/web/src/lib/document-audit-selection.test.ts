import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { selectLatestAudit, type DocumentAudit, type DocumentVersion } from "./document-contract.ts";

function audit(id: string, createdAt: string): DocumentAudit {
  return { id, createdAt, status: "Completed", score: null, errorCount: 0, warningCount: 0, infoCount: 0 };
}

function version(versionNo: number, audits: DocumentAudit[]): DocumentVersion {
  return { id: `version-${versionNo}`, versionNo, parentVersionId: null, originalFilename: "fixture.docx", sizeBytes: 1, sha256: "fixture", createdAt: "2026-08-01T00:00:00Z", audits };
}

test("documents detail API formally projects versions and nested audits newest-first", () => {
  const api = readFileSync(new URL("../../../../backend/services/Ppki.Api/Program.cs", import.meta.url), "utf8");
  assert.match(api, /Versions=doc\.Versions\.OrderByDescending\(v=>v\.VersionNo\)/);
  assert.match(api, /Audits=v\.Audits\.OrderByDescending\(a=>a\.CreatedAt\)/);
});

test("flatMap at zero agrees with explicit selection when the API ordering contract holds", () => {
  const newest = audit("audit-newest", "2026-08-04T10:00:00Z");
  const ordered = [
    version(2, [newest, audit("audit-middle", "2026-08-04T09:00:00Z")]),
    version(1, [audit("audit-old", "2026-08-03T09:00:00Z")]),
  ];
  assert.equal(ordered.flatMap(value => value.audits).at(0), newest);
  assert.equal(selectLatestAudit(ordered), newest);
});

test("latest audit remains correct when version and audit arrays arrive unordered", () => {
  const newest = audit("audit-newest", "2026-08-04T12:00:00Z");
  const unordered = [
    version(3, [audit("audit-old", "2026-08-01T12:00:00Z")]),
    version(1, [newest, audit("audit-middle", "2026-08-03T12:00:00Z")]),
    version(2, [audit("audit-new", "2026-08-04T11:00:00Z")]),
  ];
  assert.equal(selectLatestAudit(unordered), newest);
});

test("latest audit selection is deterministic for equal timestamps", () => {
  const timestamp = "2026-08-04T12:00:00Z";
  assert.equal(selectLatestAudit([version(1, [audit("audit-a", timestamp), audit("audit-b", timestamp)])])?.id, "audit-b");
});

test("latest audit selection handles empty and invalid timestamp inputs safely", () => {
  assert.equal(selectLatestAudit([]), undefined);
  assert.equal(selectLatestAudit([version(1, [audit("invalid", "not-a-date")])]), undefined);
});
