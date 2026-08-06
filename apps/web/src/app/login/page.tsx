"use client";

import { useRouter } from "next/navigation";
import { FormEvent, useState } from "react";
import { createClient } from "../../lib/supabase/client";

export default function LoginPage() {
  const router = useRouter();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const form = new FormData(event.currentTarget);
    const { error } = await createClient().auth.signInWithPassword({
      email: String(form.get("email")),
      password: String(form.get("password")),
    });
    setBusy(false);
    if (error) return setError(error.message);
    const nextPath = new URLSearchParams(window.location.search).get("next") || "/";
    router.replace(nextPath);
    router.refresh();
  }

  return (
    <main className="auth-shell">
      <form className="panel auth-card" onSubmit={submit}>
        <p className="eyebrow">PPKI IPB</p>
        <h1>Masuk</h1>
        <label>Email<input name="email" type="email" required /></label>
        <label>Kata sandi<input name="password" type="password" minLength={8} required /></label>
        {error && <p className="error-box">{error}</p>}
        <button className="button" disabled={busy}>{busy ? "Memproses..." : "Masuk"}</button>
        <p>Akun internal dibuat oleh operator PPKI yang terpercaya.</p>
      </form>
    </main>
  );
}
