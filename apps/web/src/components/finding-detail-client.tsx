"use client";

import Link from "next/link";
import { useParams, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { getAuditFinding } from "../lib/audit-api";
import type { AuditFindingDetail } from "../lib/audit-contract";
import { FindingGovernancePanel } from "./finding-governance-panel";
import { FindingLocation } from "./finding-location";
import { FindingPayload } from "./finding-payload";

export function FindingDetailClient() {
  const params = useParams();
  const auditId = String(params.auditId), findingId = String(params.findingId), query = useSearchParams().toString();
  const [finding, setFinding] = useState<AuditFindingDetail>();
  const [error, setError] = useState(""); const [loading, setLoading] = useState(true); const [reload, setReload] = useState(0);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError("");
    getAuditFinding(auditId, findingId, controller.signal).then(setFinding).catch(value => { if (value?.name !== "AbortError") setError(value instanceof Error ? value.message : "Detail tidak dapat dimuat."); }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [auditId, findingId, reload]);
  const backHref = `/audits/${encodeURIComponent(auditId)}${query ? `?${query}` : ""}`;
  if (loading) return <DetailState backHref={backHref} title="Memuat detail temuan" message="Snapshot temuan sedang dimuat." busy />;
  if (error) return <DetailState backHref={backHref} title={error.includes("tidak ditemukan") ? "Temuan tidak ditemukan" : "Detail tidak dapat dimuat"} message={error} retry={() => setReload(value => value + 1)} />;
  if (!finding) return <DetailState backHref={backHref} title="Temuan tidak tersedia" message="Respons detail tidak tersedia." />;
  return <main className="page-shell narrow detail-page">
    <Link className="back-link" href={backHref}>← Kembali ke daftar temuan</Link>
    <article className="panel detail-card">
      <header className="detail-header"><div><p className="eyebrow">Snapshot temuan historis</p><h1>{finding.ruleCode}</h1><p>{finding.element}</p></div><span className={`severity severity-${finding.severity.toLowerCase()}`}>{finding.severity}</span></header>
      <dl className="metadata-list"><div><dt>Domain</dt><dd>{finding.domain}</dd></div><div><dt>Mode perbaikan</dt><dd>{finding.fixMode}</dd></div><div><dt>Status temuan</dt><dd>{finding.findingState}</dd></div><div><dt>Capability</dt><dd>Diperiksa melalui pratinjau server</dd></div><div><dt>Alasan</dt><dd>{finding.reasonCode}</dd></div><div><dt>Lokasi</dt><dd><FindingLocation value={finding.location} /></dd></div><div><dt>Kunci validasi</dt><dd className="breakable">{finding.validationKey}</dd></div><div><dt>Keyakinan</dt><dd>{finding.confidence === null ? "Tidak tersedia" : finding.confidence}</dd></div></dl>
      <section aria-labelledby="source-title"><h2 id="source-title">Referensi sumber</h2><p>{sourceLabel(finding.source)}</p></section>
      <div className="comparison detail-comparison"><FindingPayload label="Aktual" value={finding.actual} /><FindingPayload label="Diharapkan" value={finding.expected} /></div>
      <section aria-labelledby="diagnostic-title"><h2 id="diagnostic-title">Informasi diagnostik</h2><p><code>{finding.reasonCode}</code></p><p className="muted">Informasi berasal dari snapshot audit; capability dan transition selalu ditentukan server.</p></section>
    </article>
    <FindingGovernancePanel auditId={auditId} findingId={findingId} />
  </main>;
}

function DetailState({ backHref, title, message, busy = false, retry }: { backHref: string; title: string; message: string; busy?: boolean; retry?: () => void }) { return <main className="page-shell narrow"><Link className="back-link" href={backHref}>← Kembali ke daftar temuan</Link><section className="panel page-state" aria-live="polite" aria-busy={busy}><h1>{title}</h1><p>{message}</p>{retry && <button className="button secondary" onClick={retry}>Coba lagi</button>}</section></main>; }
function sourceLabel(source: AuditFindingDetail["source"]): string { const parts = [source.sourceSection, source.pdfPage !== null ? `PDF halaman ${source.pdfPage}` : null, source.printedPage ? `halaman cetak ${source.printedPage}` : null].filter(Boolean); return parts.length ? parts.join(" · ") : "Referensi sumber tidak tersedia."; }
