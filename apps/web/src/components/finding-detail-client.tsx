"use client";

import Link from "next/link";
import { useParams, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { getAuditFinding, getAuditSummary } from "../lib/audit-api";
import type { AuditFindingDetail, AuditSummary } from "../lib/audit-contract";
import { findingGuidance, formatTimestamp } from "../lib/findings-presentation";
import { FindingLocation } from "./finding-location";
import { FindingPayload } from "./finding-payload";

export function FindingDetailClient() {
  const params = useParams();
  const auditId = String(params.auditId);
  const findingId = String(params.findingId);
  const query = useSearchParams().toString();
  const [finding, setFinding] = useState<AuditFindingDetail>();
  const [audit, setAudit] = useState<AuditSummary>();
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError("");
    Promise.all([
      getAuditFinding(auditId, findingId, controller.signal),
      getAuditSummary(auditId, controller.signal),
    ]).then(([findingValue, auditValue]) => {
      setFinding(findingValue);
      setAudit(auditValue);
    }).catch(value => {
      if (value?.name !== "AbortError") setError(value instanceof Error ? value.message : "Detail tidak dapat dimuat.");
    }).finally(() => {
      if (!controller.signal.aborted) setLoading(false);
    });
    return () => controller.abort();
  }, [auditId, findingId, reload]);

  const backHref = `/audits/${encodeURIComponent(auditId)}${query ? `?${query}` : ""}`;
  if (loading) return <DetailState backHref={backHref} title="Memuat detail temuan" message="Bukti temuan sedang dimuat." busy />;
  if (error) return <DetailState backHref={backHref} title={error.includes("tidak ditemukan") ? "Temuan tidak ditemukan" : "Detail tidak dapat dimuat"} message={error} retry={() => setReload(value => value + 1)} />;
  if (!finding || !audit) return <DetailState backHref={backHref} title="Temuan tidak tersedia" message="Respons detail tidak tersedia." />;

  const guidance = findingGuidance(finding);
  return (
    <main className="page-shell finding-detail-page">
      <Link className="back-link" href={backHref}>← Kembali ke daftar temuan</Link>
      <article className="panel detail-card">
        <header className="detail-header">
          <div>
            <p className="eyebrow">Bukti temuan audit</p>
            <h1>{guidance.title}</h1>
            <p className="muted">Aturan <strong>{finding.ruleCode}</strong> · {finding.element}</p>
          </div>
          <span className={`severity severity-${finding.severity.toLowerCase()}`}>{finding.severity}</span>
        </header>

        <section className="finding-answer" aria-labelledby="problem-title">
          <p className="answer-label">Apa yang salah?</p>
          <h2 id="problem-title">{guidance.issue}</h2>
          <p className="repair-status">{guidance.repairStatus}</p>
        </section>

        <section aria-labelledby="evidence-title">
          <div className="section-heading">
            <div><h2 id="evidence-title">Sebelum dan sesudah</h2><p>Sesudah aktual hanya berasal dari versi baru dan audit ulang, bukan simulasi.</p></div>
          </div>
          <div className="evidence-grid">
            <EvidenceCard state="before" eyebrow="Sebelum" title="Kondisi yang ditemukan" detail={guidance.issue}>
              <FindingLocation value={finding.location} />
            </EvidenceCard>
            <EvidenceCard state="target" eyebrow="Target perbaikan" title="Kondisi yang diwajibkan" detail={guidance.expected} />
            <EvidenceCard state="pending" eyebrow="Sesudah aktual" title={guidance.afterTitle} detail={guidance.afterDetail} />
          </div>
        </section>

        <section aria-labelledby="location-title">
          <div className="section-heading"><div><h2 id="location-title">Bagian yang perlu diperiksa</h2><p>Gunakan lokasi dan referensi ini saat membuka dokumen.</p></div></div>
          <dl className="human-metadata">
            <div><dt>Lokasi dokumen</dt><dd><FindingLocation value={finding.location} /></dd></div>
            <div><dt>Referensi pedoman</dt><dd>{sourceLabel(finding.source)}</dd></div>
          </dl>
        </section>

        <section aria-labelledby="repair-title">
          <div className="section-heading"><div><h2 id="repair-title">Cara memperbaiki</h2><p>Ikuti langkah berikut, lalu buktikan hasilnya melalui audit ulang.</p></div></div>
          <ol className="repair-steps">{guidance.steps.map(step => <li key={step}>{step}</li>)}</ol>
        </section>

        <section aria-labelledby="trail-title">
          <div className="section-heading"><div><h2 id="trail-title">Jejak pemeriksaan</h2><p>Log ini diturunkan dari snapshot audit dan tidak mengubah dokumen.</p></div></div>
          <ol className="evidence-trail">
            <TrailItem title="Audit selesai" detail={`${formatTimestamp(audit.completedAt)} · versi ${compactId(audit.documentVersionId)}`} state="done" />
            <TrailItem title="Masalah ditemukan" detail={`${finding.ruleCode} tercatat dengan status ${finding.findingState}.`} state="problem" />
            <TrailItem title="Hasil perbaikan belum tersedia" detail="Belum ada versi hasil dan audit ulang yang membuktikan perubahan untuk temuan ini." state="pending" />
          </ol>
        </section>

        <details className="technical-details">
          <summary>Lihat detail teknis</summary>
          <div className="technical-content">
            <dl className="metadata-list">
              <div><dt>Domain</dt><dd>{finding.domain}</dd></div>
              <div><dt>Mode aturan</dt><dd>{finding.fixMode}</dd></div>
              <div><dt>Status temuan</dt><dd>{finding.findingState}</dd></div>
              <div><dt>Aksi sistem</dt><dd>{finding.actionAvailability === "None" ? "Tidak tersedia" : finding.actionAvailability}</dd></div>
              <div><dt>Kode alasan</dt><dd className="breakable"><code>{finding.reasonCode}</code></dd></div>
              <div><dt>Kunci validasi</dt><dd className="breakable"><code>{finding.validationKey}</code></dd></div>
              <div><dt>Keyakinan</dt><dd>{finding.confidence === null ? "Tidak tersedia" : finding.confidence}</dd></div>
            </dl>
            <div className="comparison detail-comparison">
              <FindingPayload label="Data sebelum" value={finding.actual} />
              <FindingPayload label="Target teknis" value={finding.expected} />
            </div>
          </div>
        </details>
      </article>
    </main>
  );
}

function EvidenceCard({ state, eyebrow, title, detail, children }: { state: "before" | "target" | "pending"; eyebrow: string; title: string; detail: string; children?: ReactNode }) {
  return <article className={`evidence-card evidence-${state}`}><span>{eyebrow}</span><h3>{title}</h3><p>{detail}</p>{children}</article>;
}

function TrailItem({ title, detail, state }: { title: string; detail: string; state: "done" | "problem" | "pending" }) {
  return <li className={`trail-${state}`}><span aria-hidden="true" /><div><strong>{title}</strong><p>{detail}</p></div></li>;
}

function DetailState({ backHref, title, message, busy = false, retry }: { backHref: string; title: string; message: string; busy?: boolean; retry?: () => void }) {
  return <main className="page-shell narrow"><Link className="back-link" href={backHref}>← Kembali ke daftar temuan</Link><section className="panel page-state" aria-live="polite" aria-busy={busy}><h1>{title}</h1><p>{message}</p>{retry && <button className="button secondary" onClick={retry}>Coba lagi</button>}</section></main>;
}

function sourceLabel(source: AuditFindingDetail["source"]): string {
  const parts = [source.sourceSection, source.pdfPage !== null ? `PDF halaman ${source.pdfPage}` : null, source.printedPage ? `halaman cetak ${source.printedPage}` : null].filter(Boolean);
  return parts.length ? parts.join(" · ") : "Referensi sumber tidak tersedia.";
}

function compactId(value: string): string { return `${value.slice(0, 8)}…${value.slice(-4)}`; }
