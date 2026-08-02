import Link from "next/link";
import { createClient } from "../lib/supabase/server";
import { LogoutButton } from "../components/logout-button";
import { DocumentsClient } from "../components/documents-client";

export default async function HomePage() {
  const supabase = await createClient();
  const { data: { user } } = await supabase.auth.getUser();

  return (
    <main className="page-shell">
      <header className="topbar">
        <div><p className="eyebrow">PPKI IPB Smart Formatter</p><h1>Dokumen Saya</h1></div>
        <div className="actions"><span>{user?.email}</span><LogoutButton /></div>
      </header>
      <section className="panel intro">
        <div>
          <h2>Audit DOCX sebelum diperbaiki</h2>
          <p>Unggah skripsi, lihat temuan PPKI, lalu setujui perubahan pada tahap fix.</p>
        </div>
        <Link className="button" href="/documents/new">Unggah DOCX</Link>
      </section>
      <DocumentsClient />
    </main>
  );
}
