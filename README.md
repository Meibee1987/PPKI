# PPKI IPB Smart Formatter — Supabase edition

PPKI IPB Smart Formatter memeriksa format DOCX secara asynchronous. Next.js
menyediakan UI dan Supabase Auth SSR; ASP.NET Core API dan worker menangani
ownership, storage private, serta audit DOCX.

## Quick Start

Jalankan seluruh perintah dari root repository.

```powershell
Copy-Item .env.example .env
# Ganti seluruh placeholder di .env dengan konfigurasi Supabase Anda.
npm run dev:prerequisites
npm run verify
npm run dev:up
```

Setelah stack sehat, buka web di `http://localhost:3000` dan API health di
`http://localhost:8080/health`.

## Required Tools

- Git
- Docker Desktop (daemon aktif) dan Docker Compose v2
- Node.js 24 untuk menjalankan frontend atau command repository langsung
- .NET SDK 10 untuk menjalankan backend atau verifikasi langsung

`npm run dev:prerequisites` memeriksa tool tersebut, termasuk major version
Node.js dan .NET. Script ini tidak memasang atau mengunduh tool apa pun.

Jika SDK Node.js atau .NET tidak tersedia di host, tahap backend dan frontend
dapat diverifikasi dengan image SDK resmi di container. Mount hanya direktori
yang diperlukan sebagai read-only, lalu salin ke filesystem sementara container
sebelum menjalankan command. Contoh PowerShell berikut tidak memakai `.env`:

```powershell
docker run --rm -v "${PWD}/backend:/source:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c "cp -a /source /tmp/backend && cd /tmp/backend && dotnet restore PpkiSmartFormatter.slnx && dotnet build PpkiSmartFormatter.slnx --no-restore && dotnet test PpkiSmartFormatter.slnx --no-build"
docker run --rm -v "${PWD}/apps/web:/source:ro" node:24-bookworm sh -c "cp -a /source /tmp/web && cd /tmp/web && npm ci && npm run test:config && npm run typecheck && NEXT_PUBLIC_API_BASE_URL=http://localhost:8080 NEXT_PUBLIC_SUPABASE_URL=https://verification.supabase.co NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=sb_publishable_verification npm run build"
```

Docker Compose validation tetap memerlukan Docker Compose di host. Image
container di atas tidak mengubah source checkout atau memasang tool di host.

## Configure .env

`.env.example` adalah template, bukan konfigurasi siap pakai. Salin menjadi
`.env`, lalu ganti semua placeholder sebelum memulai aplikasi. Jangan commit,
membaca ke log, atau membagikan `.env`; ia berisi connection string dan secret
Supabase. `.env` tetap dipakai Docker Compose secara lokal dan tidak dibaca
oleh script verifikasi.

Untuk membuat schema Supabase, gunakan `npm ci` lalu command `npm run
supabase:login`, `npm run supabase:link -- --project-ref YOUR_PROJECT_REF`, dan
`npm run supabase:push`. Lihat [docs/SUPABASE_SETUP.md](docs/SUPABASE_SETUP.md)
untuk urutan lengkapnya.

## Verify Repository

Command verifikasi canonical adalah:

```powershell
npm run verify
```

Ia berhenti pada kegagalan pertama dan mengembalikan exit code non-zero. Delapan
tahap dijalankan berurutan: restore, build, dan test .NET; `npm ci`, test
konfigurasi, typecheck, dan build web; lalu `docker compose --env-file
.env.example config --quiet`. Build web memakai nilai publik non-secret khusus
verifikasi sehingga tidak membutuhkan `.env`; nilai tersebut tidak dicetak.

Validasi Compose hanya memeriksa bentuk konfigurasi dengan template contoh. Ini
tidak sama dengan startup aplikasi dan tidak memverifikasi kredensial Supabase
atau koneksi hosted.

## Start Development Stack

```powershell
npm run dev:up
```

Command ini menjalankan `docker compose up --build` dan menggunakan `.env`
lokal yang sudah dikonfigurasi. Untuk membangun ulang dan mengganti container,
gunakan `npm run dev:rebuild`.

## View Logs

```powershell
npm run dev:status
npm run dev:logs
npm run dev:logs:api
npm run dev:logs:worker
npm run dev:logs:web
```

Gunakan `Ctrl+C` untuk berhenti mengikuti log; container tetap berjalan.

## Stop Stack

```powershell
npm run dev:down
```

Command ini menjalankan `docker compose down` tanpa menghapus volume.

## Destructive Reset Warning

Jangan gunakan reset volume untuk masalah biasa. Jika Anda benar-benar ingin
menghapus volume Docker stack dan memahami bahwa data development akan hilang,
jalankan sendiri `docker compose down -v` setelah meninjau targetnya. Command
tersebut sengaja tidak mempunyai npm script dan tidak pernah dijalankan oleh
verifikasi atau command default.

## Common Windows Issues

- **Port sudah dipakai:** hentikan proses/container pemakai port 3000 atau 8080,
  atau ubah `WEB_PORT` / `API_PORT` di `.env`, lalu jalankan `npm run dev:up`.
- **Docker daemon tidak aktif:** buka Docker Desktop dan tunggu status engine
  berjalan, lalu ulangi `npm run dev:prerequisites`.
- **`npm` gagal dengan `EPERM`:** tutup proses Node/Next yang masih memakai
  `node_modules`, jalankan PowerShell biasa dari folder repository, lalu ulangi
  `npm --prefix apps/web ci`. Jangan menghapus `.env` dan jangan menjalankan
  `npm audit fix` untuk masalah ini.
- **PowerShell memblokir `npm.ps1`:** gunakan `npm.cmd` untuk command yang
  sama, misalnya `npm.cmd run verify`, atau jalankan melalui Command Prompt.
- **API atau worker menolak konfigurasi Supabase:** periksa nama setting dan
  placeholder di `.env` tanpa membagikan nilainya. API/worker berhenti lebih
  awal untuk URL, key, atau connection string Supabase yang invalid; gunakan
  dashboard Supabase untuk mengambil nilai yang benar.

## Active PPKI rules

Starter saat ini mencakup A4, Times New Roman 12, margin PPKI, spasi tunggal,
indentasi awal 1 cm, dan justified. Katalog 317 rule tetap di
`rules/ppki-ipb-2019/rules.json`.

## Security notes

- `NEXT_PUBLIC_*` hanya boleh memuat Project URL dan publishable key.
- Secret key dan connection string hanya dipakai API/worker.
- Bucket Supabase Storage bersifat private; akses file melewati API/worker.
