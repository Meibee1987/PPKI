import Link from "next/link";

export default function SignupPage() {
  return (
    <main className="auth-shell">
      <section className="panel auth-card">
        <p className="eyebrow">PPKI IPB</p>
        <h1>Pendaftaran ditutup</h1>
        <p>Akun aplikasi dibuat secara manual oleh operator PPKI yang terpercaya.</p>
        <p>Sudah memiliki akun internal? <Link href="/login">Masuk</Link></p>
      </section>
    </main>
  );
}
