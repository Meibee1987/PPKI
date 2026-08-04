# PPKI IPB Smart Formatter — Supabase edition

PPKI IPB Smart Formatter memeriksa format DOCX secara asynchronous. Next.js
menyediakan UI dan Supabase Auth SSR; ASP.NET Core API dan worker menangani
ownership, private storage, serta audit DOCX.

## Local development quick start

Jalankan semua command dari root repository pada Windows PowerShell:

```powershell
npm ci
npm run dev:prerequisites
npm run dev:infra
```

Kemudian buka dua terminal:

```powershell
npm run dev:backend
```

```powershell
npm run dev:web
```

Web tersedia di `http://localhost:3000`; API liveness di
`http://localhost:5080/health/live`.

Contract local development hanya memakai Supabase CLI. Jangan menjalankan
`docker compose up` sebagai stack lokal kedua. Compose baseline tidak berisi
PostgreSQL/Auth/Storage Supabase. Container Compose
`ppki-smart-formatter-supabase-api-1`, bila masih ada, adalah aplikasi ASP.NET
service `api`, bukan Supabase API/Kong.

## Required tools

- Git
- Docker Desktop dengan daemon aktif
- Docker Compose v2 (untuk validasi baseline)
- Node.js 24
- .NET SDK 10

`npm run dev:prerequisites` memeriksa tool/major version dan tidak memasang atau
mengunduh apa pun.

## Development commands

| Tujuan | Command |
| --- | --- |
| Start/reconcile Supabase lokal | `npm run dev:infra` |
| API saja | `npm run dev:api` |
| Worker saja | `npm run dev:worker` |
| API + Worker | `npm run dev:backend` |
| Web | `npm run dev:web` |
| Status/preflight | `npm run dev:status` |
| Stop infrastruktur | `npm run dev:stop` |

`dev:backend` menjalankan API dan worker dengan environment yang sama. Ctrl+C
menghentikan keduanya; bila salah satu gagal, sibling process ikut dihentikan dan
exit command menjadi non-zero. Rule catalog ditemukan otomatis dari root pada
`rules/ppki-ipb-2019/rules.json`.

Supabase URL, key, dan database password lokal diambil programmatically dari
CLI lalu hanya diteruskan melalui environment child process. Nilainya tidak
dicetak dan tidak masuk command-line argument. Runner tidak membaca `.env`.
Override non-secret opsional dapat dibuat dengan:

```powershell
Copy-Item .env.local.example .env.local
```

Lihat [docs/SUPABASE_SETUP.md](docs/SUPABASE_SETUP.md) untuk setup pertama,
daftar variable, port, recovery stale container, serta diagnosis konflik port.

## Verify repository

```powershell
npm run test:dev-bootstrap
npm run check:secrets
npm run test:secret-hygiene
npm run check:migrations
npm run verify
```

`npm run verify` menjalankan restore/build/test .NET, install/test/typecheck/build
web, lalu validasi Compose dengan `.env.example`. Validasi Compose ini hanya
memeriksa bentuk baseline dan tidak men-start stack atau mengakses hosted
Supabase.

Untuk gate keamanan yang menjalankan API dan worker terhadap Supabase lokal yang
sudah aktif, gunakan `npm run test:security-integration-local`. Suite itu tidak
menjalankan reset. Detailnya ada di
[docs/SECURITY_INTEGRATION_TESTS.md](docs/SECURITY_INTEGRATION_TESTS.md).

## Health checks

- `GET /health/live` membuktikan process API hidup tanpa menghubungi dependency.
- `GET /health/ready` memeriksa database dan konfigurasi Storage server-side.
- `GET /health` adalah compatibility alias untuk liveness.

Response health tidak memuat exception, secret, isi dokumen, atau data user.
Worker tidak mempunyai endpoint HTTP.

## Safety

- Jangan commit `.env` atau `.env.local`.
- Jangan menaruh secret/service-role key pada variable `NEXT_PUBLIC_*`.
- Jangan memakai `supabase db reset` atau menghapus Docker volume untuk recovery
  biasa.
- `dev:infra` tidak menghentikan container project lain; konflik port dilaporkan
  dengan owner bila tersedia.
- Bucket private lokal (`documents-original`, `documents-versions`, dan
  `audit-reports`) dibuat oleh migration existing.

Starter rule aktif mencakup A4, Times New Roman 12, margin PPKI, spasi tunggal,
indentasi awal 1 cm, dan justified. Katalog 317 rule tetap berada di
`rules/ppki-ipb-2019/rules.json`.
