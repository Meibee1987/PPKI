"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { apiFetch } from "../../../lib/api";
import { selectLatestAudit, type DocumentDetail } from "../../../lib/document-contract";

type AuditRunStatus = { id: string; status: string };

export default function DocumentPage() {
  const id = String(useParams().id);
  const [doc, setDoc] = useState<DocumentDetail>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const load = useCallback(async () => {
    const detail = await apiFetch<DocumentDetail>(`/api/documents/${id}`);
    setDoc(detail);
  }, [id]);
  useEffect(() => { load().catch(e => setError(e.message)); }, [load]);

  async function runAudit() {
    if (!doc) return;
    setBusy(true); setError("");
    const version = doc.versions.find(v => v.versionNo === doc.currentVersionNo)!;
    const audit = await apiFetch<AuditRunStatus>(`/api/document-versions/${version.id}/audits`, { method: "POST" });
    for (let i = 0; i < 60; i++) {
      await new Promise(r => setTimeout(r, 2000));
      const status = await apiFetch<AuditRunStatus>(`/api/audits/${audit.id}`);
      if (status.status === "Completed" || status.status === "Failed") break;
    }
    await load(); setBusy(false);
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
