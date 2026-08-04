# Developer command inventory

Semua command canonical dijalankan dari root repository.

| Kebutuhan | Command canonical |
| --- | --- |
| Prasyarat tool | `npm run dev:prerequisites` |
| Start Supabase lokal | `npm run dev:infra` |
| API saja | `npm run dev:api` |
| Worker saja | `npm run dev:worker` |
| API + Worker | `npm run dev:backend` |
| Frontend | `npm run dev:web` |
| Preflight/status aman | `npm run dev:status` |
| Stop Supabase lokal | `npm run dev:stop` |
| Test bootstrap | `npm run test:dev-bootstrap` |
| Test fix-plan preview | `npm run test:fix-plan-preview` |
| Test re-audit orchestration | `npm run test:reaudit` |
| Smoke re-audit lokal | `npm run test:reaudit-local` |
| Verifikasi repository | `npm run verify` |

Supabase CLI adalah infrastruktur development canonical. `compose.yaml` berisi
container aplikasi dari baseline lama; service `api` menghasilkan nama Compose
`ppki-smart-formatter-supabase-api-1` dan melayani ASP.NET pada host port 8080.
Container tersebut bukan Kong atau API Supabase dan Compose tidak menyediakan
PostgreSQL/Auth/Storage. Karena itu tidak ada command local-development yang
menjalankan Compose; `docker compose --env-file .env.example config --quiet`
tetap dipertahankan sebagai validasi deklaratif baseline.

Runner `scripts/dev-bootstrap.mjs` menemukan root tanpa bergantung current drive,
memvalidasi rule catalog, Docker, status stack, konfigurasi, dan port, kemudian
memuat kredensial lokal dari output Supabase CLI yang ditangkap. Tidak ada nilai
secret yang dicetak atau dimasukkan ke command line. Detail setup/recovery ada
di [SUPABASE_SETUP.md](SUPABASE_SETUP.md).

Command verifikasi/domain lain tetap tersedia di `package.json`, termasuk
fixture, parser, validator, scoring, findings UI, migration hygiene, secret
hygiene, serta security integration.

`test:reaudit` adalah suite fokus offline. `test:reaudit-local` membutuhkan
Supabase CLI lokal yang sudah aktif dan migration additive sudah diterapkan;
smoke tersebut menjalankan API lokal pada port bebas, tidak membaca `.env`,
tidak mencetak credential, tidak melakukan database reset, dan tidak menyentuh
hosted Supabase. Detail kontraknya ada di
[REAUDIT_ORCHESTRATION.md](REAUDIT_ORCHESTRATION.md).
