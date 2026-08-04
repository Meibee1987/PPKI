# Supabase lokal untuk development

Local development repository ini menggunakan **Supabase CLI** sebagai satu-satunya
infrastruktur. `compose.yaml` tidak menyediakan PostgreSQL, Auth, Storage, atau
Kong dan bukan jalur bootstrap lokal. Jangan menjalankan `docker compose up`
bersamaan dengan command di halaman ini.

## Setup pertama (Windows PowerShell)

Jalankan dari root repository:

```powershell
npm ci
npm run dev:prerequisites
npm run dev:infra
```

`dev:infra` menjalankan stack dari `supabase/config.toml`. Migration dan seed
diterapkan oleh Supabase CLI; migration yang ada juga membuat bucket private
`documents-original`, `documents-versions`, dan `audit-reports`. Command tidak
menjalankan `db reset`, tidak menghapus volume, dan tidak menyentuh container
project lain.

CLI menghasilkan URL/key/password lokal. Runner mengambil `supabase status
--output env` melalui pipe tertutup dan meneruskannya hanya melalui environment
child process. Nilainya tidak ditulis ke terminal, argument command, `.env`, atau
log buatan runner.

## Menjalankan aplikasi

Gunakan terminal terpisah dari root repository:

```powershell
npm run dev:backend
npm run dev:web
```

Command individual tersedia sebagai `npm run dev:api` dan `npm run dev:worker`.
API dan worker selalu menerima konfigurasi Supabase lokal yang sama. Rule catalog
di-resolve otomatis ke `rules/ppki-ipb-2019/rules.json`.

## Status dan stop

```powershell
npm run dev:status
npm run dev:stop
```

`dev:status` hanya menampilkan status aman, nama container, serta PID/nama owner
port bila tersedia. `dev:stop` hanya menargetkan project id
`ppki-smart-formatter` dari checkout ini dan tidak menjalankan reset database
atau penghapusan volume eksplisit.

## Konfigurasi lokal opsional

Default dapat dipakai tanpa file konfigurasi. Untuk mengubah opsi non-secret:

```powershell
Copy-Item .env.local.example .env.local
```

Nama yang didukung:

- `API_PORT`
- `WEB_PORT`
- `WORKER_POLL_SECONDS`
- `HEALTHCHECKS_TIMEOUT_SECONDS`

`.env.local` di-ignore Git. Runner menolak nama yang terlihat seperti key,
secret, password, token, JWT, atau connection string. Jangan menaruh kredensial
di file itu. `.env` tidak dibaca oleh runner lokal.

Variable yang diteruskan secara internal ke backend: `ASPNETCORE_URLS`,
`ConnectionStrings__Database`, `Supabase__Url`, `Supabase__PublishableKey`,
`Supabase__SecretKey`, `Supabase__Storage__OriginalBucket`,
`Supabase__Storage__VersionBucket`, `Supabase__Storage__ReportBucket`,
`RuleCatalog__Path`, `Cors__AllowedOrigins__0`, `Worker__PollSeconds`, dan
`HealthChecks__TimeoutSeconds`. Frontend menerima `NEXT_PUBLIC_API_BASE_URL`,
`NEXT_PUBLIC_SUPABASE_URL`, serta `NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY`.
Supabase hosted/deployed wajib memakai HTTPS. Supabase lokal memakai HTTP dengan
hostname loopback exact; URL canonical-nya `http://127.0.0.1:55321`. HTTP untuk
host non-loopback atau hostname yang hanya menyerupai loopback tidak diterima.

## Port

| Service | Port |
| --- | ---: |
| Web | 3000 |
| API aplikasi host-development | 5080 |
| Supabase shadow database | 54320 |
| Supabase API/Kong | 55321 |
| Supabase PostgreSQL | 54322 |
| Supabase Studio | 54323 |
| Supabase Inbucket UI | 54324 |
| Supabase Analytics | 54327 |

## Recovery aman

Jika startup sebelumnya terputus dan container project parsial/stale, jalankan:

```powershell
npm run dev:status
npm run dev:stop
npm run dev:infra
```

Ini merekonsiliasi container project sendiri tanpa `db reset` dan tanpa
`docker compose down -v`. Bila port dimiliki process atau container lain,
`dev:infra` berhenti sebelum startup dan menyebut owner yang harus ditinjau.
Hentikan owner tersebut secara manual hanya jika memang aman; bootstrap tidak
akan menghentikan project lain.
