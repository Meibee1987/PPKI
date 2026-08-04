import assert from "node:assert/strict";
import test from "node:test";
import type { AuditFinding } from "./audit-contract.ts";
import { findingGuidance, formatTimestamp, pageRange, presentLocation, presentPayload, scorePresentation } from "./findings-presentation.ts";

const semanticFinding: Pick<AuditFinding, "ruleCode" | "validationKey" | "element" | "reasonCode" | "actual" | "expected" | "actionAvailability"> = {
  ruleCode: "PPKI-ABS-013",
  validationKey: "summary.thesis-dissertation-language-pair",
  element: "Bahasa",
  reasonCode: "semantic-section-required",
  actual: { Property: "sectionPresence.SummaryEnglish", NormalizedValue: "absent" },
  expected: { Property: "sectionPresence.SummaryEnglish", AcceptedValues: ["present"] },
  actionAvailability: "None",
};

test("renders document-level location as Dokumen", () => assert.equal(presentLocation({ SectionIndex: null, BodyElementIndex: null, ParagraphIndex: null, RunIndex: null }).primary, "Dokumen"));
test("renders section index as a one-based human label", () => assert.deepEqual(presentLocation({ SectionIndex: 0 }).details, ["Bagian 1"]));
test("renders paragraph and run indexes consistently", () => { const location = presentLocation({ SectionIndex: 1, ParagraphIndex: 9, RunIndex: 0 }); assert.deepEqual(location.details, ["Bagian 2", "Paragraf 10", "Segmen format 1"]); assert.match(location.accessibleLabel, /Paragraf 10/); });
test("keeps bounded compact location as a secondary accessible reference", () => { const location = presentLocation({ CompactLocation: "body/paragraph:2", ParagraphIndex: 2 }); assert.equal(location.compact, "body/paragraph:2"); assert.match(location.accessibleLabel, /Paragraf 3.*body\/paragraph:2/); });
test("renders partial and invalid locations safely", () => { assert.equal(presentLocation({ RunIndex: 2 }).primary, "Segmen format 3"); assert.equal(presentLocation("unexpected").primary, "Lokasi tidak tersedia"); });

test("keeps null, zero, and false visually distinct", () => { const rows = presentPayload({ missing: null, count: 0, inherited: false }); assert.deepEqual(rows.map(row => row.value), ["Tidak tersedia", "0", "Tidak"]); });
test("renders allowlisted structured fields without a raw JSON dump", () => { const rows = presentPayload({ Property: "font-size", NormalizedValue: "12", Unit: "pt" }); assert.deepEqual(rows.map(row => row.label), ["Properti", "Nilai ternormalisasi", "Satuan"]); assert.doesNotMatch(rows.map(row => row.value).join(" "), /\{|\}/); });
test("renders unknown safe fields through a bounded fallback", () => { const value = Object.fromEntries(Array.from({ length: 30 }, (_, index) => [`field${index}`, index])); assert.equal(presentPayload(value).length, 12); });
test("limits arrays and recursive depth", () => { const rows = presentPayload({ acceptedValues: ["a", "b", "c", "d", "e", "f", "g"], nested: { deeper: { value: "hidden-by-depth" } } }); assert.equal(rows[0].value, "a, b, c, d, e, f"); assert.equal(rows[1].value, "Data terstruktur"); });
test("truncates long strings", () => assert.ok(presentPayload({ value: "a".repeat(300) })[0].value.length <= 120));
test("never exposes sensitive keys or a synthetic document marker", () => { const marker = "PRIVATE-THESIS-MARKER"; const rows = presentPayload({ property: "safe", text: marker, nested: { rawXml: marker, filename: marker, stack: marker, path: marker, url: marker, exception: marker } }); assert.doesNotMatch(JSON.stringify(rows), new RegExp(marker)); });

test("explains a missing English abstract in user language", () => {
  const guidance = findingGuidance(semanticFinding);
  assert.equal(guidance.title, "Abstrak bahasa Inggris belum terdeteksi");
  assert.match(guidance.issue, /tidak menemukan bagian abstrak berbahasa Inggris/);
  assert.equal(guidance.repairStatus, "Perbaikan manual diperlukan");
});
test("never presents a target as an actual completed repair", () => {
  const guidance = findingGuidance(semanticFinding);
  assert.match(guidance.afterTitle, /Belum/);
  assert.match(guidance.afterDetail, /versi dokumen baru dan audit ulang/);
  assert.doesNotMatch(`${guidance.afterTitle} ${guidance.afterDetail}`, /sudah diperbaiki/i);
});
test("explains deterministic justified alignment without inventing document text", () => {
  const guidance = findingGuidance({ ...semanticFinding, ruleCode: "PPKI-LAY-019", validationKey: "body.justified", element: "Perataan paragraf", reasonCode: "paragraph-alignment-invalid", actual: { Property: "alignment", NormalizedValue: "left" }, expected: { Property: "alignment", AcceptedValues: ["both"] } });
  assert.match(guidance.title, /rata kiri-kanan/);
  assert.match(guidance.expected, /rata kiri-kanan/);
});
test("generic guidance remains bounded and excludes payload content", () => {
  const marker = "PRIVATE-THESIS-MARKER";
  const guidance = findingGuidance({ ...semanticFinding, validationKey: "unknown.key", reasonCode: "unknown", actual: { text: marker }, expected: {} });
  assert.doesNotMatch(JSON.stringify(guidance), new RegExp(marker));
  assert.equal(guidance.steps.length, 3);
});

test("NotConfigured shows no invented numeric score", () => { const value = scorePresentation("NotConfigured", null, null); assert.equal(value.title, "Skor belum dikonfigurasi"); assert.doesNotMatch(`${value.title} ${value.detail}`, /(^|\D)(0|100)(\D|$)/); });
test("Calculated shows backend score and policy version", () => assert.deepEqual(scorePresentation("Calculated", 87.5, "policy-v1"), { title: "87.5", detail: "Kebijakan policy-v1" }));
test("Calculated with missing value is controlled", () => assert.equal(scorePresentation("Calculated", null, "policy-v1").title, "Skor tidak tersedia"));
test("AuditIncomplete has an explicit state", () => assert.equal(scorePresentation("AuditIncomplete", null, null).detail, "Audit belum selesai"));
test("InvalidConfiguration and NotApplicable are distinct", () => assert.notEqual(scorePresentation("InvalidConfiguration", null, null).title, scorePresentation("NotApplicable", null, null).title));

test("computes stable pagination ranges and boundaries", () => assert.deepEqual(pageRange(2, 25, 60), { start: 26, end: 50, totalPages: 3 }));
test("empty page range is zero rather than a fake item", () => assert.deepEqual(pageRange(1, 25, 0), { start: 0, end: 0, totalPages: 1 }));
test("formats valid timestamps and rejects invalid values safely", () => { assert.notEqual(formatTimestamp("2026-08-04T10:00:00Z"), "Tidak valid"); assert.equal(formatTimestamp("not-a-date"), "Tidak valid"); assert.equal(formatTimestamp(null), "Belum tersedia"); });
