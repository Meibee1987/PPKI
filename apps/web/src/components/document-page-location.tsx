"use client";

import { useState } from "react";
import { apiFetchBlob } from "../lib/api";
import type { AuditFinding } from "../lib/audit-contract";

export function DocumentPageLocation({ versionId, value }: { versionId: string; value: AuditFinding["pageLocation"] }) {
  const [opening, setOpening] = useState(false);
  const label = value.pageNumber !== null && value.confidence === "Exact"
    ? `Halaman ${value.pageNumber}`
    : value.pageNumber !== null && value.confidence === "Estimated"
      ? `Perkiraan halaman ${value.pageNumber}`
      : value.state === "Pending" || value.state === "Processing"
        ? "Menentukan halaman..."
        : "Lokasi halaman belum tersedia";
  const available = value.pageNumber !== null && value.state === "Completed"
    && (value.confidence === "Exact" || value.confidence === "Estimated");

  async function openPreview() {
    const target = window.open("", "_blank");
    if (target) target.opener = null;
    setOpening(true);
    try {
      const blob = await apiFetchBlob(`/api/document-versions/${encodeURIComponent(versionId)}/preview`);
      const url = URL.createObjectURL(blob);
      if (target) target.location.href = `${url}#page=${value.pageNumber}`;
      else window.open(`${url}#page=${value.pageNumber}`, "_blank", "noopener,noreferrer");
      window.setTimeout(() => URL.revokeObjectURL(url), 300_000);
    } catch {
      target?.close();
    } finally { setOpening(false); }
  }

  return <span className="document-page-location"><span>{label}</span>{available && <button className="text-button" type="button" disabled={opening} onClick={openPreview}>{opening ? "Membuka..." : "Buka di dokumen"}</button>}</span>;
}
