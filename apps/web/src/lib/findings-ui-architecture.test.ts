import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (relative: string) => readFileSync(new URL(relative, import.meta.url), "utf8");
const list = read("../components/audit-findings-client.tsx");
const detail = read("../components/finding-detail-client.tsx");
const client = read("./audit-api.ts");
const styles = read("../app/globals.css");
const pageLocation = read("../components/document-page-location.tsx");

test("typed API client separates summary, list, and lazy detail requests", () => {
  assert.match(client, /getAuditSummary/); assert.match(client, /listAuditFindings/); assert.match(client, /getAuditFinding/);
  assert.match(detail, /getAuditFinding\(auditId, findingId/);
});

test("filters have visible labels and write URL state", () => {
  for (const label of ["Keparahan", "Mode perbaikan", "Domain", "Kode aturan", "Kunci validasi", "Item per halaman"]) assert.match(list, new RegExp(label));
  assert.match(list, /router\.push/); assert.match(list, /page: 1/);
});

test("list preserves backend order without client sorting or merging", () => {
  assert.doesNotMatch(list, /\.sort\(|\.reverse\(|setPage\([^)]*\.concat/);
  assert.match(list, /page\.items\.map/);
});

test("read-only UI exposes no fix, mutation, export, or retry-audit request", () => {
  const combined = `${list}\n${detail}\n${client}`;
  assert.doesNotMatch(combined, /method:\s*["'](?:POST|PUT|PATCH|DELETE)/i);
  assert.doesNotMatch(combined, /FixPlan|auto-fix|re-audit/i);
});

test("Action None is presented as no action and never as a fix control", () => {
  assert.match(list, /actionAvailability === "None" \? "Tidak ada"/);
  assert.doesNotMatch(list, />\s*(?:Apply|Auto|Confirm|Perbaiki)\s*</i);
});

test("production components do not log, inject HTML, or store finding payloads", () => {
  const combined = `${list}\n${detail}`;
  assert.doesNotMatch(combined, /console\.|dangerouslySetInnerHTML|localStorage|sessionStorage/);
});

test("loading, empty, filtered-empty, failed, processing, and safe retry states are explicit", () => {
  for (const state of ["Memuat hasil audit", "Tidak ada temuan yang cocok", "Audit gagal", "Audit sedang diproses", "Coba lagi"]) assert.match(list, new RegExp(state));
});

test("pagination is labelled, bounded, and keyboard-native", () => {
  assert.match(list, /aria-label="Navigasi halaman temuan"/); assert.match(list, /disabled=\{page\.page <= 1\}/); assert.match(list, /disabled=\{page\.page >= range\.totalPages\}/);
});

test("responsive rules cover tablet, mobile, and narrow mobile layouts", () => {
  assert.match(styles, /max-width:900px/); assert.match(styles, /max-width:700px/); assert.match(styles, /max-width:430px/);
});

test("canonical page location labels never fabricate a page and preview remains GET-only", () => {
  for (const label of ["Halaman ${value.pageNumber}", "Perkiraan halaman ${value.pageNumber}", "Menentukan halaman...", "Lokasi halaman belum tersedia", "Buka di dokumen"])
    assert.match(pageLocation, new RegExp(label.replace(/[.$\{\}]/g, "\\$&")));
  assert.match(pageLocation, /value\.state === "Completed"/);
  assert.doesNotMatch(pageLocation, /method:\s*["'](?:POST|PUT|PATCH|DELETE)/i);
  assert.match(pageLocation, /#page=\$\{value\.pageNumber\}/);
});
