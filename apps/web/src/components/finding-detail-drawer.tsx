"use client";

import { useEffect, useRef, useState } from "react";
import { ApiRequestError, isApiRequestAborted } from "../lib/api";
import { getAuditFinding, getStructuralFindingExcerpt } from "../lib/audit-api";
import type { AuditFindingDetail, FindingDisposition, FindingResolutionState, FindingReviewState, FindingState, StructuralFindingExcerpt } from "../lib/audit-contract";
import type { CanonicalAuditIdentity } from "../lib/canonical-audit-identity";
import { assertCanonicalFindingDetail, assertCanonicalStructuralExcerpt, findingDetailRequestKey } from "../lib/finding-detail-model";
import { createLatestFindingRequestGuard } from "../lib/finding-list-model";
import { DocumentPageLocation } from "./document-page-location";
import { FindingLocation } from "./finding-location";
import { FindingReviewActions } from "./finding-review-actions";

type ExcerptView = { state: "Idle" | "Loading" | "Exact" | "Unavailable" | "Failed"; value?: StructuralFindingExcerpt };

export function FindingDetailDrawer({ identity, findingId, pageFindingIds, onSelect, onClose, onReviewChanged }: {
  identity: CanonicalAuditIdentity;
  findingId: string;
  pageFindingIds: string[];
  onSelect: (findingId: string) => void;
  onClose: () => void;
  onReviewChanged: () => Promise<void>;
}) {
  const [detail, setDetail] = useState<AuditFindingDetail>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [reload, setReload] = useState(0);
  const [excerpt, setExcerpt] = useState<ExcerptView>({ state: "Idle" });
  const drawer = useRef<HTMLDivElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const excerptRequest = useRef<AbortController | undefined>(undefined);
  const requests = useRef(createLatestFindingRequestGuard());

  useEffect(() => {
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
    closeButton.current?.focus();
    const handleKeyDown = (event: KeyboardEvent) => {
      if (drawer.current?.querySelector("dialog[open]")) return;
      if (event.key === "Escape") { event.preventDefault(); onClose(); return; }
      if (event.key !== "Tab" || !drawer.current) return;
      const focusable = Array.from(drawer.current.querySelectorAll<HTMLElement>("button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])"));
      if (!focusable.length) return;
      const first = focusable[0], last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    document.addEventListener("keydown", handleKeyDown);
    return () => { document.removeEventListener("keydown", handleKeyDown); previousFocus?.focus(); };
  }, [onClose]);

  useEffect(() => () => excerptRequest.current?.abort(), []);

  useEffect(() => {
    const controller = new AbortController();
    const token = requests.current.begin(findingDetailRequestKey(identity, findingId));
    excerptRequest.current?.abort();
    setDetail(undefined); setExcerpt({ state: "Idle" }); setLoading(true); setError("");
    getAuditFinding(identity.auditId, findingId, controller.signal)
      .then(value => assertCanonicalFindingDetail(identity, findingId, value))
      .then(value => { if (requests.current.isCurrent(token)) setDetail(value); })
      .catch(value => {
        if (requests.current.isCurrent(token) && !isApiRequestAborted(value)) setError(detailError(value));
      })
      .finally(() => { if (requests.current.isCurrent(token)) setLoading(false); });
    return () => { controller.abort(); requests.current.cancel(token); };
  }, [identity.auditId, identity.documentVersionId, findingId, reload]);

  const selectedIndex = pageFindingIds.indexOf(findingId);
  const previousId = selectedIndex > 0 ? pageFindingIds[selectedIndex - 1] : undefined;
  const nextId = selectedIndex >= 0 && selectedIndex < pageFindingIds.length - 1 ? pageFindingIds[selectedIndex + 1] : undefined;

  async function loadExcerpt() {
    if (!detail) return;
    const controller = new AbortController();
    excerptRequest.current?.abort(); excerptRequest.current = controller; setExcerpt({ state: "Loading" });
    try {
      const value = assertCanonicalStructuralExcerpt(identity, findingId,
        await getStructuralFindingExcerpt(identity.auditId, findingId, controller.signal));
      if (excerptRequest.current !== controller) return;
      setExcerpt(value.status === "Exact" ? { state: "Exact", value } : { state: "Unavailable", value });
    } catch (value) {
      if (excerptRequest.current === controller && !isApiRequestAborted(value)) setExcerpt({ state: "Failed" });
    } finally {
      if (excerptRequest.current === controller) excerptRequest.current = undefined;
    }
  }

  return <div className="finding-drawer-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <div ref={drawer} className="finding-drawer" role="dialog" aria-modal="true" aria-labelledby="finding-detail-title" aria-describedby="finding-detail-description">
      <header className="finding-drawer-header"><div><p className="eyebrow">Detail temuan</p><h2 id="finding-detail-title">{detail?.ruleCode ?? "Memuat temuan"}</h2><p id="finding-detail-description">Informasi authoritative dari snapshot audit dan versi dokumen terpilih.</p></div><button ref={closeButton} className="drawer-close" type="button" aria-label="Tutup detail temuan" onClick={onClose}>×</button></header>
      <div className="finding-drawer-navigation" aria-label="Pilih temuan pada halaman ini"><button className="text-button" type="button" disabled={!previousId} onClick={() => previousId && onSelect(previousId)}>← Temuan sebelumnya</button><button className="text-button" type="button" disabled={!nextId} onClick={() => nextId && onSelect(nextId)}>Temuan berikutnya →</button></div>
      {loading && <div className="drawer-state" role="status" aria-live="polite">Memuat detail temuan...</div>}
      {!loading && error && <div className="drawer-state error-box" role="alert"><h3>{error}</h3><p>Daftar temuan tetap dapat digunakan.</p><button className="button secondary" type="button" onClick={() => setReload(value => value + 1)}>Coba lagi</button></div>}
      {!loading && !error && !detail && <div className="drawer-state empty-state"><h3>Temuan tidak lagi tersedia</h3><p>Tutup detail lalu perbarui daftar temuan.</p></div>}
      {detail && <FindingDetailContent identity={identity} detail={detail} excerpt={excerpt} loadExcerpt={() => void loadExcerpt()} onReviewChanged={async () => { setReload(value => value + 1); await onReviewChanged(); }} />}
    </div>
  </div>;
}

function FindingDetailContent({ identity, detail, excerpt, loadExcerpt, onReviewChanged }: { identity: CanonicalAuditIdentity; detail: AuditFindingDetail; excerpt: ExcerptView; loadExcerpt: () => void; onReviewChanged: () => Promise<void> }) {
  return <div className="finding-drawer-content">
    <section className="drawer-rule" aria-labelledby="drawer-rule-title"><div><span className={`severity severity-${detail.severity.toLowerCase()}`}>{detail.severity}</span><span className="domain-label">{detail.domain}</span></div><h3 id="drawer-rule-title">{detail.presentation.propertyLabel}</h3><p>{detail.presentation.problem}</p><p className="muted">Elemen: {detail.element} · Kode aturan: <code>{detail.ruleCode}</code></p></section>
    <dl className="drawer-metadata"><div><dt>Status temuan</dt><dd>{findingStateLabel(detail.findingState)}</dd></div><div><dt>Disposisi</dt><dd>{dispositionLabel(detail.disposition)}</dd></div><div><dt>Mode perbaikan</dt><dd>{detail.fixMode}</dd></div><div><dt>Kemampuan otomatis</dt><dd>{detail.actionAvailability === "Automatic" ? "Tersedia menurut backend" : "Tidak tersedia"}</dd></div><div><dt>Status resolusi</dt><dd>{resolutionLabel(detail.resolutionState)}</dd></div><div><dt>Status review</dt><dd>{reviewLabel(detail.reviewState)}</dd></div><div><dt>Kunci validasi</dt><dd className="breakable">{detail.validationKey}</dd></div><div><dt>Keyakinan</dt><dd>{detail.confidence === null ? "Tidak tersedia" : detail.confidence}</dd></div></dl>
    <div className="drawer-comparison"><EvidenceValue title="Aktual (Actual)" label={detail.presentation.beforeLabel} value={detail.presentation.beforeValue} /><EvidenceValue title="Diharapkan (Expected)" label={detail.presentation.expectedLabel} value={detail.presentation.expectedValue} /></div>
    {detail.presentation.evidenceState !== "Complete" && <p className="muted">Bukti aman {detail.presentation.evidenceState === "Partial" ? "tersedia sebagian" : "tidak tersedia"}; nilai tidak diperkirakan atau dibuat.</p>}
    <section aria-labelledby="drawer-location-title"><h3 id="drawer-location-title">Lokasi dokumen</h3><div className="drawer-location"><FindingLocation value={detail.location} /><DocumentPageLocation versionId={detail.documentVersionId} value={detail.pageLocation} /></div><ExcerptPanel excerpt={excerpt} loadExcerpt={loadExcerpt} /></section>
    <section aria-labelledby="drawer-source-title"><h3 id="drawer-source-title">Referensi sumber</h3><p>{sourceLabel(detail.source)}</p></section>
    <FindingReviewActions key={detail.id} identity={identity} findingId={detail.id} onChanged={onReviewChanged} />
  </div>;
}

function EvidenceValue({ title, label, value }: { title: "Aktual (Actual)" | "Diharapkan (Expected)"; label: string; value: string | null }) {
  return <section aria-label={title}><h3>{title}</h3><small>{label}</small><p>{value ?? "Nilai aman tidak tersedia"}</p></section>;
}

function ExcerptPanel({ excerpt, loadExcerpt }: { excerpt: ExcerptView; loadExcerpt: () => void }) {
  if (excerpt.state === "Idle") return <button className="text-button excerpt-button" type="button" onClick={loadExcerpt}>Lihat bagian dokumen</button>;
  if (excerpt.state === "Loading") return <div className="context-state" role="status">Memuat bagian dokumen...</div>;
  if (excerpt.state === "Failed") return <div className="context-state" role="status">Bagian dokumen tidak dapat dimuat.</div>;
  if (excerpt.state !== "Exact" || !excerpt.value?.excerpt) return <div className="context-state" role="status">Cuplikan dokumen tidak tersedia.</div>;
  return <div className="structural-excerpt"><small>{excerpt.value.targetType === "Heading" ? "Teks pada dokumen" : excerpt.value.targetType === "Section" ? "Cuplikan bagian dokumen" : "Cuplikan paragraf"}</small><blockquote>{excerpt.value.excerpt}</blockquote></div>;
}

function sourceLabel(source: AuditFindingDetail["source"]): string {
  const parts = [source.sourceSection, source.pdfPage !== null ? `PDF halaman ${source.pdfPage}` : null, source.printedPage ? `halaman cetak ${source.printedPage}` : null].filter(Boolean);
  return parts.length ? parts.join(" · ") : "Referensi sumber tidak tersedia.";
}

function findingStateLabel(value: FindingState): string { return value === "Open" ? "Terbuka" : value === "Fixed" ? "Diperbaiki" : value === "Ignored" ? "Diabaikan" : "Perlu review manual"; }
function dispositionLabel(value: FindingDisposition): string { return value === "Resolved" ? "Selesai" : value === "Ignored" ? "Diabaikan" : "Perlu review"; }
function resolutionLabel(value: FindingResolutionState): string { return value === "Open" ? "Belum ditangani" : value === "Applied" ? "Perbaikan diterapkan" : value === "ReauditPending" ? "Menunggu audit ulang" : value === "VerifiedResolved" ? "Terverifikasi selesai" : "Masih terdeteksi"; }
function reviewLabel(value: FindingReviewState): string { return value === "NoReview" ? "Belum ada review" : value === "PendingReview" ? "Menunggu review" : value === "NeedsRevision" ? "Perlu revisi" : value === "ManualRemediationApproved" ? "Remediasi manual disetujui" : value === "ManualRemediationReported" ? "Remediasi manual dilaporkan" : value === "Rejected" ? "Ditolak" : value === "Ignored" ? "Diabaikan" : "Risiko diterima"; }

function detailError(value: unknown): string {
  if (value instanceof ApiRequestError && value.status === 404) return "Temuan tidak lagi tersedia";
  return value instanceof ApiRequestError ? value.message : "Detail temuan tidak dapat dimuat.";
}
