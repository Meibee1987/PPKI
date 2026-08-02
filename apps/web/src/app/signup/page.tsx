"use client";

import Link from "next/link";
import { FormEvent, useState } from "react";
import { createClient } from "../../lib/supabase/client";

export default function SignupPage() {
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true); setError(""); setMessage("");
    const form = new FormData(event.currentTarget);
    const email = String(form.get("email"));
    const fullName = String(form.get("fullName"));
    const { error } = await createClient().auth.signUp({
      email,
      password: String(form.get("password")),
      options: {
        data: { full_name: fullName },
        emailRedirectTo: `${window.location.origin}/auth/callback`,
      },
    });
    setBusy(false);
    if (error) return setError(error.message);
    setMessage("Pendaftaran berhasil. Periksa email konfirmasi bila konfirmasi email diaktifkan.");
  }

  return (
    <main className="auth-shell">
      <form className="panel auth-card" onSubmit={submit}>
        <p className="eyebrow">PPKI IPB</p>
        <h1>Buat akun</h1>
        <label>Nama lengkap<input name="fullName" required /></label>
        <label>Email<input name="email" type="email" required /></label>
        <label>Kata sandi<input name="password" type="password" minLength={8} required /></label>
        {error && <p className="error-box">{error}</p>}
        {message && <p className="success-box">{message}</p>}
        <button className="button" disabled={busy}>{busy ? "Memproses..." : "Daftar"}</button>
        <p>Sudah punya akun? <Link href="/login">Masuk</Link></p>
      </form>
    </main>
  );
}
