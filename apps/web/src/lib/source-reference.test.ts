import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import type { AuditSource } from "./audit-contract.ts";
import { fixModeGlossary, severityGlossary, sourceReferencePresentation } from "./source-reference-model.ts";

const read = (relative: string) => readFileSync(new URL(relative, import.meta.url), "utf8");
const component = read("../components/source-reference.tsx");
const drawer = read("../components/finding-detail-drawer.tsx");
const list = read("../components/audit-finding-list.tsx");
const snapshot = read("../../../../backend/src/Ppki.RuleEngine/ResolvedRuleSetSnapshots.cs");
const readService = read("../../../../backend/src/Ppki.Infrastructure/AuditReadService.cs");
const empty: AuditSource = { sourceSection: null, pdfPage: null, printedPage: null };

test("authoritative source section is preserved", () => {
  assert.equal(sourceReferencePresentation({ ...empty, sourceSection: "Bab 3.2" }).sourceSection, "Bab 3.2");
});

test("PDF page is preserved only when supplied", () => {
  const value = sourceReferencePresentation({ ...empty, pdfPage: 42 });
  assert.equal(value.pdfPage, 42);
  assert.equal(value.printedPage, null);
});

test("printed page is preserved only when supplied", () => {
  const value = sourceReferencePresentation({ ...empty, printedPage: "xvii" });
  assert.equal(value.printedPage, "xvii");
  assert.equal(value.pdfPage, null);
});

test("PDF and printed pages remain distinct", () => {
  const value = sourceReferencePresentation({ ...empty, pdfPage: 23, printedPage: "9" });
  assert.deepEqual([value.pdfPage, value.printedPage], [23, "9"]);
  assert.match(component, /Halaman PDF/);
  assert.match(component, /Halaman cetak/);
});

test("missing pages are never inferred from each other or the section", () => {
  const value = sourceReferencePresentation({ ...empty, sourceSection: "Lampiran 16", printedPage: "138" });
  assert.equal(value.pdfPage, null);
  assert.doesNotMatch(component, /parseInt|Number\(|pdfPage\s*[+\-]|printedPage\s*[+\-]/);
});

test("missing source metadata has a safe unavailable state", () => {
  assert.equal(sourceReferencePresentation(empty).availability, "Unavailable");
  assert.match(component, /Referensi sumber tidak tersedia/);
});

test("partial source metadata is explicit", () => {
  assert.equal(sourceReferencePresentation({ ...empty, sourceSection: "Bab 2" }).availability, "Partial");
  assert.match(component, /Metadata referensi tersedia sebagian/);
});

test("complete current metadata remains metadata-only", () => {
  assert.equal(sourceReferencePresentation({ sourceSection: "Bab 2", pdfPage: 10, printedPage: "7" }).availability, "MetadataOnly");
});

test("navigation target is absent without an authoritative asset contract", () => {
  assert.equal(sourceReferencePresentation({ ...empty, pdfPage: 10 }).navigationTarget, null);
  assert.doesNotMatch(component, /<a\b|<Link\b|href=|router\.push|window\.open/);
});

test("no fake PDF fragment or guessed source URL is fabricated", () => {
  assert.doesNotMatch(component, /#page=|\.pdf(?:["'?#])|sourceUrl|signedUrl/);
});

test("filesystem, storage, and token-like metadata are suppressed", () => {
  for (const sourceSection of ["C:\\private\\ppki.pdf", "/tmp/ppki.pdf", "https://example.test/source", "/storage/v1/object/a?token=secret"])
    assert.equal(sourceReferencePresentation({ ...empty, sourceSection }).sourceSection, null);
});

test("source presentation is bound to the selected detail object", () => {
  assert.match(drawer, /<SourceReference source=\{detail\.source\} severity=\{detail\.severity\} fixMode=\{detail\.fixMode\}/);
});

test("historical source comes from finding snapshots, never the live catalog", () => {
  assert.match(snapshot, /SourceReferenceJson/);
  assert.match(readService, /finding\.SourceSectionSnapshot/);
  assert.match(readService, /finding\.PdfPageSnapshot/);
  assert.match(readService, /finding\.PrintedPageSnapshot/);
  assert.doesNotMatch(component + drawer, /rules\.json|RuleDefinition|source_type/);
});

test("selection replacement retains canonical and stale-response safeguards", () => {
  assert.match(drawer, /assertCanonicalFindingDetail\(identity, findingId, value\)/);
  assert.match(drawer, /requests\.current\.isCurrent\(token\)/);
  assert.match(drawer, /controller\.abort\(\)/);
  assert.match(drawer, /setDetail\(undefined\)/);
});

test("source UI does not mutate list, readiness, remediation, or review state", () => {
  assert.doesNotMatch(component, /onClick|fetch\(|setSummary|readiness|requestFindingReview|method=|method:/i);
  assert.match(list, /filters=\{visible\.filters\} page=\{visible\.page\}/);
  assert.match(drawer, /<FindingReviewActions/);
});

test("transient structural excerpt privacy behavior is unchanged", () => {
  assert.match(drawer, /getStructuralFindingExcerpt\(identity\.auditId, findingId, controller\.signal\)/);
  assert.match(drawer, /setExcerpt\(\{ state: "Idle" \}\)/);
  assert.doesNotMatch(component, /excerpt|targetText|document content/i);
});

test("source labels and glossary control are accessible text", () => {
  assert.match(component, /aria-labelledby="drawer-source-title"/);
  assert.match(component, /<summary>Arti severity dan mode perbaikan<\/summary>/);
  assert.match(component, /Bagian sumber/);
});

test("glossary covers only the typed catalog terminology", () => {
  assert.deepEqual(Object.keys(severityGlossary), ["Error", "Warning", "Info"]);
  assert.deepEqual(Object.keys(fixModeGlossary), ["Auto", "Confirm", "Manual", "Report"]);
});

test("S6-T07 introduces no S6-T08 test framework or broad accessibility tooling", () => {
  assert.doesNotMatch(component + drawer, /axe-core|playwright|cypress|jest-axe/);
});
