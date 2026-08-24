"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";
import { isApiRequestAborted } from "../../../lib/api";
import { getAuditStatus, getDocument, startAudit } from "../../../lib/document-api";
import { selectLatestAudit, type DocumentDetail } from "../../../lib/document-contract";

export default function DocumentPage() {
  const id = String(useParams().id);
  const [doc, setDoc] = useState<DocumentDetail>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const auditRequest = useRef<AbortController | undefined>(undefined);
  const load = useCallback(async (signal?: AbortSignal) => {
    const detail = await getDocument(id, signal);
    setDoc(detail);
  }, [id]);
  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal).catch(value => {
      if (!isApiRequestAborted(value)) setError(value instanceof Error ? value.message : "Dokumen tidak dapat dimuat.");
    });
    return () => { controller.abort(); auditRequest.current?.abort(); };
  }, [load]);

  async function runAudit() {
    if (!doc) return;
    setBusy(true); setError("");
    const version = doc.versions.find(v => v.versionNo === doc.currentVersionNo)!;
    const controller = new AbortController(); auditRequest.current?.abort(); auditRequest.current = controller;
    try {
      const audit = await startAudit(version.id, controller.signal);
      for (let i = 0; i < 60; i++) {
        await wait(2000, controller.signal);
        const current = await getAuditStatus(audit.id, controller.signal);
        if (["Completed", "Failed", "Cancelled"].includes(current.status)) break;
      }
      await load(controller.signal);
    } catch (value) {
      if (!isApiRequestAborted(value)) setError(value instanceof Error ? value.message : "Audit tidak dapat dijalankan.");
    } finally {
      if (auditRequest.current === controller) { auditRequest.current = undefined; setBusy(false); }
    }
  }

  if (!doc) return <main className="page-shell"><p>{error || "Memuat..."}</p></main>;
  const latest = selectLatestAudit(doc.versions);
  return (
    <main className="page-shell">
      <Link href="/">← Dokumen saya</Link>
      <header className="topbar"><div><p className="eyebrow">{doc.documentType}</p><h1>{doc.title}</h1><p>Versi aktif: {doc.currentVersionNo}</p></div><button className="button" onClick={runAudit} disabled={busy}>{busy ? "Audit berjalan..." : "Jalankan Audit PPKI"}</button></header>
      {error && <p className="error-box">{error}</p>}
      <section className="metrics">
        <div className="metric"><span>Skor</span><strong>{latest?.score ?? "-"}</strong></div>
        <div className="metric"><span>Error</span><strong>{latest?.errorCount ?? 0}</strong></div>
        <div className="metric"><span>Warning</span><strong>{latest?.warningCount ?? 0}</strong></div>
        <div className="metric"><span>Status</span><strong>{latest?.status ?? "Belum audit"}</strong></div>
      </section>
      <section className="panel"><h2>Hasil audit</h2>{latest ? <><p>Lihat ringkasan, filter, dan temuan historis untuk audit terbaru.</p><Link className="button secondary" href={`/audits/${latest.id}`}>Buka hasil audit</Link></> : <p>Belum ada audit.</p>}</section>
    </main>
  );
}

function wait(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const finish = () => { signal.removeEventListener("abort", abort); resolve(); };
    const abort = () => { window.clearTimeout(timer); reject(new DOMException("Aborted", "AbortError")); };
    const timer = window.setTimeout(finish, milliseconds);
    signal.addEventListener("abort", abort, { once: true });
  });
}
