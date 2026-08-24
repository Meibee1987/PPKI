"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { isApiRequestAborted } from "../lib/api";
import { listDocuments } from "../lib/document-api";
import type { DocumentListItem } from "../lib/document-contract";

export function DocumentsClient() {
  const [rows, setRows] = useState<DocumentListItem[]>([]);
  const [error, setError] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    listDocuments(controller.signal).then(setRows).catch(value => {
      if (!isApiRequestAborted(value)) setError(value instanceof Error ? value.message : "Dokumen tidak dapat dimuat.");
    });
    return () => controller.abort();
  }, []);
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
