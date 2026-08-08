# Developer command inventory

## Finding review

Use `npm run test:finding-review` for the focused offline contract suite. With
local Supabase ready and the additive migration applied, run
`npm run test:finding-review-local` twice to verify the bounded runtime workflow.
The smoke proves the global database-role gate, token-claim spoof resistance,
operational self-review, admin-only RLS, role protection, immutable history, and
rerunnable bounded cardinality without resetting the database. It also runs the
shared Admin A/Admin B closure over documents, nested versions, audits,
findings, FixPlan, fix status, re-audit, comparison, resolution, and finding
review; a database-role downgrade is always restored in `finally`.

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
| Test audit comparison | `npm run test:audit-comparison` |
| Smoke audit comparison lokal | `npm run test:audit-comparison-local` |
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

`test:audit-comparison` memverifikasi read model deterministik secara offline.
`test:audit-comparison-local` membutuhkan stack Supabase CLI lokal dan migration
terkini; smoke menjalankan API loopback pada port bebas dengan fixture bounded,
tanpa membaca `.env`, mencetak credential, memakai hosted Supabase, atau reset
database. Detail kontraknya ada di
[AUDIT_COMPARISON.md](AUDIT_COMPARISON.md).

`npm run test:finding-resolution` menjalankan suite offline S4-T03.
`npm run test:finding-resolution-local` memakai stack Supabase CLI lokal dan
migration additive terkini tanpa hosted Supabase, reset database, atau delete
volume. Detail kontraknya ada di
[FINDING_RESOLUTION.md](FINDING_RESOLUTION.md).

`npm run test:remediation-hardening` menjalankan typed failure, fencing/retry,
fault-injection, publish, downstream, dan privacy contract secara offline.
`npm run test:remediation-hardening-local` memakai Supabase lokal untuk claim
takeover, stale-worker denial, retry bound, source superseded, Storage orphan,
compensating cleanup, canonical publish, safe DTO, shared-admin RLS, serta
checksum historis. Jalankan smoke lokal dua kali; command tidak melakukan reset
database atau menghapus volume.

`npm run test:remediation-ui` menjalankan focused typed-contract,
presentation, polling/idempotency, privacy, accessibility, dan architecture
suite S4-T06. `npm run test:remediation-ui-local` adalah agregat API-backed
lokal yang bounded dan rerunnable, bukan browser E2E. Checklist browser manual
ada di `docs/REMEDIATION_UI.md`.
