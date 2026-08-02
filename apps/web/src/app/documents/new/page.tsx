"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { FormEvent, useState } from "react";
import { apiFetch } from "../../../lib/api";

export default function NewDocumentPage() {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setBusy(true); setError("");
    const form = new FormData(event.currentTarget);
    try {
      const result = await apiFetch<{ id: string }>("/api/documents", { method: "POST", body: form });
      router.push(`/documents/${result.id}`);
    } catch (e) { setError(e instanceof Error ? e.message : "Upload gagal"); setBusy(false); }
  }

  return (
    <main className="page-shell narrow">
      <Link href="/">← Kembali</Link>
      <form className="panel form-stack" onSubmit={submit}>
        <p className="eyebrow">Dokumen baru</p><h1>Unggah DOCX</h1>
        <label>Judul dokumen<input name="title" required /></label>
        <label>Jenis tugas akhir<select name="documentTypeCode" defaultValue="SKRIPSI"><option value="SKRIPSI">Skripsi</option><option value="TESIS">Tesis</option><option value="DISERTASI">Disertasi</option><option value="LAPORAN_AKHIR">Laporan Akhir</option></select></label>
        <label>File DOCX<input name="file" type="file" accept=".docx,application/vnd.openxmlformats-officedocument.wordprocessingml.document" required /></label>
        {error && <p className="error-box">{error}</p>}
        <button className="button" disabled={busy}>{busy ? "Mengunggah..." : "Unggah sebagai Versi 1"}</button>
      </form>
    </main>
  );
}
