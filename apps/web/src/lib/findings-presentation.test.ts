import assert from "node:assert/strict";
import test from "node:test";
import { formatTimestamp, pageRange, presentLocation, presentPayload, scorePresentation } from "./findings-presentation.ts";

test("renders document-level location as Dokumen", () => assert.equal(presentLocation({ SectionIndex: null, BodyElementIndex: null, ParagraphIndex: null, RunIndex: null }).primary, "Dokumen"));
test("renders section index as a one-based human label", () => assert.deepEqual(presentLocation({ SectionIndex: 0 }).details, ["Bagian 1"]));
test("renders paragraph and run indexes consistently", () => { const location = presentLocation({ SectionIndex: 1, ParagraphIndex: 9, RunIndex: 0 }); assert.deepEqual(location.details, ["Bagian 2", "Paragraf 10", "Segmen format 1"]); assert.match(location.accessibleLabel, /Paragraf 10/); });
test("keeps bounded compact location as a secondary accessible reference", () => { const location = presentLocation({ CompactLocation: "body/paragraph:2", ParagraphIndex: 2 }); assert.equal(location.compact, "body/paragraph:2"); assert.match(location.accessibleLabel, /Paragraf 3.*body\/paragraph:2/); });
test("renders partial and invalid locations safely", () => { assert.equal(presentLocation({ RunIndex: 2 }).primary, "Segmen format 3"); assert.equal(presentLocation("unexpected").primary, "Lokasi rinci tidak tersedia"); });

test("keeps null, zero, and false visually distinct", () => { const rows = presentPayload({ missing: null, count: 0, inherited: false }); assert.deepEqual(rows.map(row => row.value), ["Tidak tersedia", "0", "Tidak"]); });
test("renders allowlisted structured fields without a raw JSON dump", () => { const rows = presentPayload({ Property: "font-size", NormalizedValue: "12", Unit: "pt" }); assert.deepEqual(rows.map(row => row.label), ["Properti", "Nilai ternormalisasi", "Satuan"]); assert.doesNotMatch(rows.map(row => row.value).join(" "), /\{|\}/); });
test("renders unknown safe fields through a bounded fallback", () => { const value = Object.fromEntries(Array.from({ length: 30 }, (_, index) => [`field${index}`, index])); assert.equal(presentPayload(value).length, 12); });
test("limits arrays and recursive depth", () => { const rows = presentPayload({ acceptedValues: ["a", "b", "c", "d", "e", "f", "g"], nested: { deeper: { value: "hidden-by-depth" } } }); assert.equal(rows[0].value, "a, b, c, d, e, f"); assert.equal(rows[1].value, "Data terstruktur"); });
test("truncates long strings", () => assert.ok(presentPayload({ value: "a".repeat(300) })[0].value.length <= 120));
test("never exposes sensitive keys or a synthetic document marker", () => { const marker = "PRIVATE-THESIS-MARKER"; const rows = presentPayload({ property: "safe", text: marker, nested: { rawXml: marker, filename: marker, stack: marker, path: marker, url: marker, exception: marker } }); assert.doesNotMatch(JSON.stringify(rows), new RegExp(marker)); });

test("NotConfigured shows no invented numeric score", () => { const value = scorePresentation("NotConfigured", null, null); assert.equal(value.title, "Skor belum dikonfigurasi"); assert.doesNotMatch(`${value.title} ${value.detail}`, /(^|\D)(0|100)(\D|$)/); });
test("Calculated shows backend score and policy version", () => assert.deepEqual(scorePresentation("Calculated", 87.5, "policy-v1"), { title: "87.5", detail: "Kebijakan policy-v1" }));
test("Calculated with missing value is controlled", () => assert.equal(scorePresentation("Calculated", null, "policy-v1").title, "Skor tidak tersedia"));
test("AuditIncomplete has an explicit state", () => assert.equal(scorePresentation("AuditIncomplete", null, null).detail, "Audit belum selesai"));
test("InvalidConfiguration and NotApplicable are distinct", () => assert.notEqual(scorePresentation("InvalidConfiguration", null, null).title, scorePresentation("NotApplicable", null, null).title));

test("computes stable pagination ranges and boundaries", () => assert.deepEqual(pageRange(2, 25, 60), { start: 26, end: 50, totalPages: 3 }));
test("empty page range is zero rather than a fake item", () => assert.deepEqual(pageRange(1, 25, 0), { start: 0, end: 0, totalPages: 1 }));
test("formats valid timestamps and rejects invalid values safely", () => { assert.notEqual(formatTimestamp("2026-08-04T10:00:00Z"), "Tidak valid"); assert.equal(formatTimestamp("not-a-date"), "Tidak valid"); assert.equal(formatTimestamp(null), "Belum tersedia"); });
