"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { apiFetch } from "../lib/api";

type DocumentRow = {
  id: string; title: string; documentType: string; currentVersionNo: number;
  updatedAt: string; latestAudit?: { id: string; status: string; score?: number; errorCount: number; warningCount: number };
};

export function DocumentsClient() {
  const [rows, setRows] = useState<DocumentRow[]>([]);
  const [error, setError] = useState("");
  useEffect(() => { apiFetch<DocumentRow[]>("/api/documents").then(setRows).catch(e => setError(e.message)); }, []);
  if (error) return <p className="error-box">{error}</p>;
  return (
    <section className="panel">
      <h2>Daftar dokumen</h2>
      {rows.length === 0 ? <p>Belum ada dokumen.</p> : (
        <div className="document-list">
          {rows.map(row => (
            <Link className="document-row" href={`/documents/${row.id}`} key={row.id}>
              <div><strong>{row.title}</strong><span>{row.documentType} · Versi {row.currentVersionNo}</span></div>
              <div className="right"><span>{row.latestAudit?.status ?? "Belum diaudit"}</span><strong>{row.latestAudit?.score ?? "-"}</strong></div>
            </Link>
          ))}
        </div>
      )}
    </section>
  );
}
