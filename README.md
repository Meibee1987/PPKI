# PPKI IPB Smart Formatter — Supabase Starter

Starter ini adalah baseline baru dari nol untuk arsitektur:

- **Next.js 16 + TypeScript** — UI dan Supabase Auth SSR.
- **ASP.NET Core 10** — API, ownership, orchestration.
- **Supabase PostgreSQL** — metadata, rules, audit jobs, findings.
- **Supabase Storage** — DOCX original/versions dan laporan, semuanya private.
- **.NET Worker + Open XML SDK** — parse DOCX dan audit asynchronous.

## Vertical slice yang sudah disiapkan

```text
Sign up / login
→ upload DOCX ke private Supabase Storage
→ metadata Document + Version 1 masuk Postgres
→ queue AuditJob
→ worker download sementara
→ Open XML parser + 9 validator PPKI
→ findings tersimpan
→ audit log tampil di web
```

## 1. Buat project Supabase

Buat project hosted, lalu atur:

- Site URL: `http://localhost:3000`
- Redirect URL: `http://localhost:3000/auth/callback`

Salin Project URL, publishable key, secret key, serta **Session pooler** database connection.

## 2. Konfigurasi

Windows PowerShell:

```powershell
Copy-Item .env.example .env
```

macOS/Linux:

```bash
cp .env.example .env
```

Isi seluruh placeholder pada `.env`. Secret key dan connection string tidak boleh masuk Git atau frontend.

## 3. Terapkan schema Supabase

```powershell
npm install
npx supabase login
npx supabase link --project-ref YOUR_PROJECT_REF
npx supabase db push
```

Migration akan membuat tabel, RLS, trigger profil user, baseline profile PPKI, dan tiga private Storage buckets.

## 4. Jalankan aplikasi

```powershell
docker compose up --build
```

Buka:

- Web: `http://localhost:3000`
- API health: `http://localhost:8080/health`
- OpenAPI: `http://localhost:8080/openapi/v1.json`

## 5. Uji

1. Daftar akun dan konfirmasi email bila fitur tersebut aktif.
2. Login.
3. Unggah DOCX yang sengaja memakai Letter, Calibri 11, margin 2,54 cm, spasi 1,15, left alignment.
4. Jalankan audit.
5. Pastikan file muncul di bucket `documents-original`, metadata ada di `document_versions`, job selesai, dan findings tampil.

## Rule aktif pada starter

- PPKI-LAY-003 A4
- PPKI-LAY-005 Times New Roman 12
- PPKI-LAY-008 margin kiri 4 cm
- PPKI-LAY-009 margin kanan 3 cm
- PPKI-LAY-010 margin atas 3 cm
- PPKI-LAY-011 margin bawah 3 cm
- PPKI-LAY-017 spasi tunggal
- PPKI-LAY-018 indentasi awal 1 cm
- PPKI-LAY-019 justified

Katalog 317 rule tetap berada di `rules/ppki-ipb-2019/rules.json`; API mengimpornya pada start pertama.

## Dokumen penting

- `docs/SUPABASE_SETUP.md`
- `docs/architecture.md`
- `docs/SPRINTS_SUPABASE_MVP.md`
- `supabase/migrations/202608010001_initial_schema.sql`

## Catatan keamanan

- `NEXT_PUBLIC_*` hanya memuat Project URL dan publishable key.
- `SUPABASE_SECRET_KEY` hanya ada pada API/worker.
- Storage bucket tidak mempunyai policy browser; semua akses file melalui API/worker.
- API memverifikasi token ke Supabase Auth dan memfilter seluruh data berdasarkan user `sub`.
