"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";
import { StatusBadge } from "../../../components/status-badge";
import { ApiRequestError, isApiRequestAborted } from "../../../lib/api";
import {
  auditProgressFromAccepted,
  auditProgressFromDocument,
  auditProgressFromSummary,
  isAuditPollingStatus,
  observeAuditProgress,
  type AuditProgressSnapshot,
} from "../../../lib/audit-progress";
import { getAuditStatus, getDocument, startAudit } from "../../../lib/document-api";
import { selectLatestAudit, type DocumentDetail } from "../../../lib/document-contract";

export default function DocumentPage() {
  const id = String(useParams().id);
  const [doc, setDoc] = useState<DocumentDetail>();
  const [audit, setAudit] = useState<AuditProgressSnapshot>();
  const [submitting, setSubmitting] = useState(false);
  const [requestError, setRequestError] = useState("");
  const [pollingError, setPollingError] = useState("");
  const [pollRevision, setPollRevision] = useState(0);
  const submissionRequest = useRef<AbortController | undefined>(undefined);
  const submissionInFlight = useRef(false);

  const applyDocument = useCallback((detail: DocumentDetail) => {
    setDoc(detail);
    const latest = selectLatestAudit(detail.versions);
    setAudit(latest ? auditProgressFromDocument(latest) : undefined);
  }, []);

  const load = useCallback(async (signal?: AbortSignal) => {
    applyDocument(await getDocument(id, signal));
  }, [applyDocument, id]);

  useEffect(() => {
    const controller = new AbortController();
    setDoc(undefined); setAudit(undefined); setRequestError(""); setPollingError("");
    load(controller.signal).catch(value => {
      if (!isApiRequestAborted(value)) setRequestError(value instanceof Error ? value.message : "Dokumen tidak dapat dimuat.");
    });
    return () => { controller.abort(); submissionRequest.current?.abort(); };
  }, [load]);

  const currentVersion = doc?.versions.find(value => value.versionNo === doc.currentVersionNo);
  useEffect(() => {
    if (!doc || doc.id !== id || !audit || !currentVersion || !isAuditPollingStatus(audit.status)) return;
    const observedAuditId = audit.id;
    const observedVersionId = currentVersion.id;
    setPollingError("");
    return observeAuditProgress({
      auditId: observedAuditId,
      initialStatus: audit.status,
      getStatus: async (auditId, signal) => auditProgressFromSummary(await getAuditStatus(auditId, signal)),
      onStatus: value => setAudit(previous => previous?.id === observedAuditId ? value : previous),
      onCompleted: async (_value, signal) => {
        const detail = await getDocument(id, signal);
        if (!detail.versions.some(value => value.id === observedVersionId)) return;
        applyDocument(detail);
      },
      onUnavailable: () => setPollingError("Status audit belum dapat diperbarui. Status terakhir tetap ditampilkan."),
      shouldStopAfterError: value => value instanceof ApiRequestError && value.status === 401,
    });
  }, [applyDocument, audit?.id, currentVersion?.id, doc?.id, id, pollRevision]);

  async function runAudit() {
    if (!currentVersion || submissionInFlight.current || audit && isAuditPollingStatus(audit.status)) return;
    submissionInFlight.current = true; setSubmitting(true); setRequestError(""); setPollingError("");
    const controller = new AbortController(); submissionRequest.current?.abort(); submissionRequest.current = controller;
    try {
      const accepted = await startAudit(currentVersion.id, controller.signal);
      setAudit(auditProgressFromAccepted(accepted));
    } catch (value) {
      if (!isApiRequestAborted(value)) setRequestError(value instanceof Error ? value.message : "Audit tidak dapat dijalankan.");
    } finally {
      if (submissionRequest.current === controller) {
        submissionRequest.current = undefined; submissionInFlight.current = false; setSubmitting(false);
      }
    }
  }

  if (!doc) return <main className="page-shell"><p>{requestError || "Memuat..."}</p></main>;
  const observing = Boolean(audit && isAuditPollingStatus(audit.status));
  const startLabel = submitting ? "Mengirim permintaan audit..."
    : audit?.status === "Failed" || audit?.status === "Cancelled" ? "Jalankan Audit Baru"
      : observing ? "Audit sedang dipantau..." : "Jalankan Audit PPKI";

  return (
    <main className="page-shell">
      <Link href="/">← Dokumen saya</Link>
      <header className="topbar"><div><p className="eyebrow">{doc.documentType}</p><h1>{doc.title}</h1><p>Versi aktif: {doc.currentVersionNo}</p></div><button className="button" onClick={runAudit} disabled={submitting || observing}>{startLabel}</button></header>
      {requestError && <p className="error-box">{requestError}</p>}
      <section className="metrics">
        <div className="metric"><span>Skor</span><strong>{audit?.score ?? "-"}</strong></div>
        <div className="metric"><span>Error</span><strong>{audit?.errorCount ?? 0}</strong></div>
        <div className="metric"><span>Warning</span><strong>{audit?.warningCount ?? 0}</strong></div>
        <div className="metric"><span>Status</span><strong>{audit?.status ?? "Belum audit"}</strong></div>
      </section>
      <AuditProgressPanel audit={audit} pollingError={pollingError}
        refreshStatus={() => setPollRevision(value => value + 1)} runNewAudit={runAudit} submitting={submitting} />
    </main>
  );
}

function AuditProgressPanel({ audit, pollingError, refreshStatus, runNewAudit, submitting }: {
  audit?: AuditProgressSnapshot; pollingError: string; refreshStatus: () => void;
  runNewAudit: () => void; submitting: boolean;
}) {
  if (!audit) return <section className="panel"><h2>Hasil audit</h2><p>Belum ada audit.</p></section>;
  const copy = audit.status === "Queued" ? ["Menunggu giliran", "Permintaan audit sudah diterima dan menunggu worker."]
    : audit.status === "Processing" ? ["Audit sedang berjalan", "Dokumen sedang diperiksa. Status diperbarui otomatis."]
      : audit.status === "Completed" ? ["Audit selesai", "Hasil canonical sudah tersedia tanpa memuat ulang browser."]
        : audit.status === "Failed" ? ["Audit gagal", "Audit tidak dapat diselesaikan. Detail internal tidak ditampilkan."]
          : ["Audit dibatalkan", "Audit berhenti tanpa hasil lengkap."];
  return <section className={`panel progress-panel${audit.status === "Failed" || audit.status === "Cancelled" ? " progress-failed" : ""}`} aria-live="polite" aria-busy={isAuditPollingStatus(audit.status)}>
    <header className="section-heading"><div><h2>{copy[0]}</h2><p>{copy[1]}</p></div><StatusBadge status={audit.status} /></header>
    {pollingError && <div className="notice"><p>{pollingError}</p><button className="button secondary" type="button" onClick={refreshStatus}>Coba perbarui status</button></div>}
    {audit.status === "Completed" && <Link className="button secondary" href={`/audits/${encodeURIComponent(audit.id)}`}>Buka hasil audit</Link>}
    {(audit.status === "Failed" || audit.status === "Cancelled") && <div className="error-box" role="alert"><p>Audit ini tidak dapat dilanjutkan. Anda dapat membuat audit baru untuk versi dokumen yang sama.</p><button className="button secondary" type="button" onClick={runNewAudit} disabled={submitting}>{submitting ? "Mengirim..." : "Jalankan audit baru"}</button></div>}
  </section>;
}
