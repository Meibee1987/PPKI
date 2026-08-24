import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (relative: string) => readFileSync(new URL(relative, import.meta.url), "utf8");
const page = read("../components/streamlined-audit-client.tsx");
const list = read("../components/audit-finding-list.tsx");
const drawer = read("../components/finding-detail-drawer.tsx");
const review = read("../components/finding-review-actions.tsx");
const dialog = read("../components/confirmation-dialog.tsx");
const source = read("../components/source-reference.tsx");
const sourceModel = read("./source-reference-model.ts");
const styles = read("../app/globals.css");
const packageJson = read("../../package.json");
const canonical = read("./canonical-audit-identity.ts");
const polling = read("./audit-progress.ts");

test("01 major audit sections have semantic headings and labelled regions", () => {
  for (const text of ["Hasil Audit", "Kesiapan review", "Ringkasan audit", "Daftar temuan audit"])
    assert.match(page + list, new RegExp(text));
  assert.match(list, /aria-labelledby="finding-log-title"/);
});

test("02 progress and readiness communicate explicit text without color", () => {
  assert.match(page, /Audit sedang diproses/);
  assert.match(page, /readinessPresentation\(summary\)/);
  assert.match(page, /item\.status/);
});

test("03 finding search has a programmatic group and field label", () => {
  assert.match(list, /role="search" aria-label="Cari dan filter temuan audit"/);
  assert.match(list, /<label className="finding-search">Cari kode atau elemen aturan<input type="search"/);
});

test("04 every finding filter uses a native labelled select", () => {
  for (const label of ["Keparahan", "Mode perbaikan", "Status temuan", "Domain", "Per halaman"])
    assert.match(list, new RegExp(`<label>${label}<select`));
});

test("05 pagination is a named navigation region with named controls", () => {
  assert.match(list, /<nav className="pagination" aria-label="Navigasi halaman daftar temuan"/);
  assert.match(list, /aria-label="Halaman temuan sebelumnya"/);
  assert.match(list, /aria-label="Halaman temuan berikutnya"/);
});

test("06 impossible pagination actions are natively disabled", () => {
  assert.match(list, /disabled=\{page\.page <= 1\}/);
  assert.match(list, /disabled=\{page\.page >= range\.totalPages\}/);
});

test("07 finding detail opens from a named native button", () => {
  assert.match(list, /<button className="button secondary" type="button" aria-label=\{`Lihat detail \$\{item\.ruleCode\}`\}/);
});

test("08 drawer exposes modal dialog name and description", () => {
  assert.match(drawer, /role="dialog" aria-modal="true" aria-labelledby="finding-detail-title" aria-describedby="finding-detail-description"/);
});

test("09 drawer entry focus moves to the named close control", () => {
  assert.match(drawer, /closeButton\.current\?\.focus\(\)/);
  assert.match(drawer, /aria-label="Tutup detail temuan"/);
});

test("10 drawer closes with Escape only when no nested dialog is open", () => {
  assert.match(drawer, /querySelector\("dialog\[open\]"\)\) return/);
  assert.match(drawer, /event\.key === "Escape"[\s\S]*onClose\(\)/);
});

test("11 drawer returns focus to its opener after unmount", () => {
  assert.match(drawer, /const previousFocus = document\.activeElement/);
  assert.match(drawer, /previousFocus\?\.focus\(\)/);
});

test("12 drawer contains forward and reverse Tab traversal", () => {
  assert.match(drawer, /event\.key !== "Tab"/);
  assert.match(drawer, /event\.shiftKey && document\.activeElement === first/);
  assert.match(drawer, /document\.activeElement === last/);
});

test("13 confirmation dialogs use unique linked ARIA IDs", () => {
  assert.match(dialog, /const titleId = useId\(\)/);
  assert.match(dialog, /const descriptionId = useId\(\)/);
  assert.match(dialog, /aria-labelledby=\{titleId\} aria-describedby=\{descriptionId\}/);
  assert.doesNotMatch(dialog, /id="confirm-(?:title|description)"/);
});

test("14 confirmation entry and return focus are deterministic", () => {
  assert.match(dialog, /element\.showModal\(\); cancelButton\.current\?\.focus\(\)/);
  assert.match(dialog, /ref=\{cancelButton\}/);
  assert.match(dialog, /trigger\.current\?\.focus\(\)/);
});

test("15 nested Escape closes only the native top-layer dialog", () => {
  assert.match(dialog, /onCancel=\{event => \{ event\.preventDefault\(\); if \(!busy\) dialog\.current\?\.close\(\)/);
  assert.match(drawer, /dialog\[open\]/);
});

test("16 review reason has stable labels, help, count, and conditional error linkage", () => {
  assert.match(review, /htmlFor=\{reasonId\}/);
  assert.match(review, /aria-describedby=\{`\$\{reasonHelpId\} \$\{reasonCountId\}\$\{reasonError \?/);
  assert.match(review, /aria-errormessage=\{reasonError \? reasonErrorId : undefined\}/);
  assert.match(review, /role="alert">\{reasonError\}/);
});

test("17 review actions and confirmation controls remain native keyboard controls", () => {
  for (const label of ["Tandai untuk review manual", "Ajukan Ignore", "Konfirmasi Ignore", "Simpan dengan alasan"])
    assert.match(review, new RegExp(label));
  assert.doesNotMatch(review + dialog, /role="button"|tabIndex=\{0\}.*onClick/);
});

test("18 source glossary remains a native keyboard disclosure", () => {
  assert.match(source, /<details className="source-glossary">/);
  assert.match(source, /<summary>Arti severity dan mode perbaikan<\/summary>/);
});

test("19 structural excerpt loading requires an explicit native button", () => {
  assert.match(drawer, /<button className="text-button excerpt-button" type="button" onClick=\{loadExcerpt\}>Lihat bagian dokumen<\/button>/);
});

test("20 important loading, error, and completion states have restrained semantics", () => {
  assert.match(list, /role="status">Memuat halaman pertama temuan/);
  assert.match(list, /role="alert"/);
  assert.match(drawer, /role="status" aria-live="polite">Memuat detail temuan/);
  assert.match(review, /className="success-box" role="status"/);
  assert.doesNotMatch(review, /<output[^>]*aria-live/);
});

test("21 focus indicators cover links, controls, textareas, and disclosures", () => {
  for (const selector of ["a:focus-visible", "button:focus-visible", "input:focus-visible", "select:focus-visible", "textarea:focus-visible", "summary:focus-visible"])
    assert.match(styles, new RegExp(selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
});

test("22 practical touch targets are retained for primary and text controls", () => {
  assert.match(styles, /\.button \{[^\n]*min-height:44px/);
  assert.match(styles, /\.text-button \{[^\n]*min-height:40px/);
  assert.match(styles, /\.drawer-close \{[^\n]*width:44px; height:44px/);
});

test("23 drawer and confirmation dialog remain bounded by the dynamic viewport", () => {
  assert.match(styles, /finding-drawer[^\n]*height:100dvh[^\n]*overflow-x:hidden[^\n]*overflow-y:auto/);
  assert.match(styles, /confirm-dialog[^\n]*max-height:calc\(100dvh - 32px\)[^\n]*overflow-y:auto/);
});

test("24 narrow layouts reflow metrics, filters, metadata, and actions", () => {
  assert.match(styles, /@media\(max-width:620px\)[^\n]*finding-filter-grid[^\n]*source-glossary dl[^\n]*grid-template-columns:1fr/);
  assert.match(styles, /@media\(max-width:430px\)[^\n]*summary-strip[^\n]*grid-template-columns:1fr/);
  assert.match(styles, /dialog-actions \.button\{width:100%\}/);
});

test("25 long rule, source, dialog, and review text wraps safely", () => {
  assert.match(styles, /finding-log-list h3[^\n]*overflow-wrap:anywhere/);
  assert.match(styles, /source-reference>p[^\n]*overflow-wrap:anywhere/);
  assert.match(styles, /confirm-dialog[^\n]*overflow-wrap:anywhere/);
  assert.match(styles, /drawer-review-actions>p[^\n]*overflow-wrap:anywhere/);
});

test("26 canonical audit and finding lineage checks remain enforced", () => {
  assert.match(canonical, /canonical-audit-lineage-invalid/);
  assert.match(drawer, /assertCanonicalFindingDetail\(identity, findingId, value\)/);
});

test("27 bounded sequential polling and silent cancellation remain intact", () => {
  assert.match(polling, /maximumAuditPolls = 60/);
  assert.match(polling, /inFlight/);
  assert.doesNotMatch(polling, /setInterval/);
  assert.match(drawer, /!isApiRequestAborted\(value\)/);
});

test("28 readiness, list, and review semantics are not recalculated by accessibility code", () => {
  assert.doesNotMatch(drawer + dialog + styles, /ReadyForExport|blockingFindingCount\s*=|requestFindingReview|findingsQuery/);
  assert.match(list, /ringkasan audit di atas tidak berubah/i);
  assert.match(review, /Ignore tidak berarti VerifiedResolved/);
});

test("29 source security and excerpt privacy remain unchanged", () => {
  assert.match(sourceModel, /navigationTarget: null/);
  assert.match(sourceModel, /unsafeReference/);
  assert.doesNotMatch(source, /href=|signedUrl|storage\/v1|dangerouslySetInnerHTML/);
  assert.doesNotMatch(drawer, /localStorage|sessionStorage|console\./);
});

test("30 hardening uses the existing lightweight test stack without browser dependencies", () => {
  assert.match(packageJson, /"test:audit-ui-accessibility": "node --test --experimental-strip-types/);
  assert.doesNotMatch(packageJson, /@playwright|cypress|jest-axe|axe-core/);
});
