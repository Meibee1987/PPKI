"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { apiFetch } from "../../../lib/api";
import { FindingCard } from "../../../components/finding-card";

type Audit = { id: string; status: string; score?: number; errorCount: number; warningCount: number; infoCount: number };
type Version = { id: string; versionNo: number; originalFilename: string; sizeBytes: number; sha256: string; audits: Audit[] };
type DocumentDetail = { id: string; title: string; documentType: string; currentVersionNo: number; versions: Version[] };
type Finding = { id: string; ruleCode: string; element: string; domain: string; severity: string; fixMode: string; message: string; actual: unknown; expected: unknown; location: unknown; source: { sourceSection?: string; pdfPage?: number } };

export default function DocumentPage() {
  const id = String(useParams().id);
  const [doc, setDoc] = useState<DocumentDetail>();
  const [findings, setFindings] = useState<Finding[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const load = useCallback(async () => {
    const detail = await apiFetch<DocumentDetail>(`/api/documents/${id}`);
    setDoc(detail);
    const latest = detail.versions.flatMap(v => v.audits).at(0);
    if (latest?.status === "Completed") setFindings(await apiFetch<Finding[]>(`/api/audits/${latest.id}/findings`));
  }, [id]);
  useEffect(() => { load().catch(e => setError(e.message)); }, [load]);

  async function runAudit() {
    if (!doc) return;
    setBusy(true); setError("");
    const version = doc.versions.find(v => v.versionNo === doc.currentVersionNo)!;
    const audit = await apiFetch<Audit>(`/api/document-versions/${version.id}/audits`, { method: "POST" });
    for (let i = 0; i < 60; i++) {
      await new Promise(r => setTimeout(r, 2000));
      const status = await apiFetch<Audit>(`/api/audits/${audit.id}`);
      if (status.status === "Completed" || status.status === "Failed") break;
    }
    await load(); setBusy(false);
  }

  if (!doc) return <main className="page-shell"><p>{error || "Memuat..."}</p></main>;
  const latest = doc.versions.flatMap(v => v.audits).at(0);
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
      <section className="panel"><h2>Audit log sebelum perbaikan</h2>{findings.length === 0 ? <p>Belum ada finding.</p> : findings.map(f => <FindingCard key={f.id} finding={f} />)}</section>
    </main>
  );
}
