# Sprint 11 — Reviewer workflow, hardening, pilot, dan rilis MVP

**Sprint goal:** Menutup alur Ready for Review hingga Approved/Changes Requested dan memastikan MVP layak pilot terbatas.

## Exit criteria

- [ ] DocumentVersion dapat dibagikan kepada reviewer tertentu.
- [ ] Reviewer dapat approve/request changes pada exact version.
- [ ] Audit trail, rate limit, logging, cleanup, dan observability siap pilot.
- [ ] Pilot 10–20 DOCX terdokumentasi.
- [ ] Release checklist dan rollback plan tersedia; tidak ada P0/P1 terbuka.

## Kontrak kerja wajib untuk Codex

- Baca `AGENTS.md` terlebih dahulu dan patuhi seluruh guardrail di dalamnya.
- Kerjakan **hanya satu task ini**. Jangan lanjut ke task berikutnya meskipun masih ada waktu.
- Jangan melakukan refactor luas yang tidak diperlukan oleh acceptance criteria.
- Jangan pernah menulis atau mencetak isi `.env`, Supabase secret key, connection string, access token, atau isi lengkap karya ilmiah ke log/test snapshot.
- File DOCX original harus immutable. Setiap mutasi wajib menghasilkan `DocumentVersion` baru.
- Validator format harus deterministik; jangan menambahkan generative AI untuk rule mekanis.
- `rules/ppki-ipb-2019/rules.json` adalah source data. Implementasi validator tetap compiled code yang dipilih melalui `ValidationKey`.
- Perubahan schema harus additive melalui migration baru. Jangan mengubah migration yang sudah dianggap pernah diterapkan, kecuali task secara eksplisit menyatakan baseline belum pernah dipakai.
- Tambahkan atau perbarui test yang relevan. Untuk parser/fixer, gunakan golden DOCX fixture.
- Jika dependency baru diperlukan, pilih yang minimal, jelaskan lisensinya di ringkasan, dan jangan menambah dependency yang tidak dipakai.
- Bila akses Supabase hosted tidak tersedia di lingkungan agent, implementasikan bagian kode/test yang dapat dilakukan secara lokal dan tulis langkah verifikasi remote yang belum dijalankan.

## Format jawaban akhir Codex

1. Ringkasan hasil.
2. File yang diubah.
3. Migration/kontrak API yang berubah.
4. Perintah build/test yang dijalankan dan hasilnya.
5. Risiko atau langkah verifikasi manual yang tersisa.
6. Jangan mengklaim berhasil bila build/test tidak dijalankan atau gagal.

---

## S11-T01 — Schema document sharing dan review

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Modelkan invitation/share dan review decision terikat exact DocumentVersion.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T01: Schema document sharing dan review.

Tujuan task:
Modelkan invitation/share dan review decision terikat exact DocumentVersion.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan DocumentShare/ReviewRequest/DocumentReview dengan owner, reviewer, status, timestamps, comment.
B. Tambahkan unique/expiry/revoke constraints dan RLS default-deny.
C. Buat migration/EF mapping.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Reviewer tidak memperoleh akses sebelum share aktif.
- Approval tidak berpindah otomatis ke version baru.
- Revoked share tidak menghapus history.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npx supabase db lint

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Reviewer tidak memperoleh akses sebelum share aktif.
- [ ] Approval tidak berpindah otomatis ke version baru.
- [ ] Revoked share tidak menghapus history.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npx supabase db lint
```


---

## S11-T02 — Reviewer access authorization

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Reviewer hanya dapat membaca document/version/audit yang secara eksplisit dibagikan dan tidak dapat menjalankan fix sebagai owner.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Application`
- `backend/services/Ppki.Api/Program.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T02: Reviewer access authorization.

Tujuan task:
Reviewer hanya dapat membaca document/version/audit yang secara eksplisit dibagikan dan tidak dapat menjalankan fix sebagai owner.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Application
- backend/services/Ppki.Api/Program.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Extend authorization service dengan share scope/read-only.
B. Implement request/revoke/list reviewer endpoints.
C. Tambahkan cross-user and revoked/expired tests.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No global reviewer access.
- Reviewer cannot download original jika policy tidak mengizinkan; kebijakan eksplisit.
- Owner tetap dapat revoke.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No global reviewer access.
- [ ] Reviewer cannot download original jika policy tidak mengizinkan; kebijakan eksplisit.
- [ ] Owner tetap dapat revoke.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S11-T03 — Review decision API dan state machine

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Reviewer dapat Approved atau ChangesRequested pada version tertentu dengan comment dan audit trail.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Domain/Enums.cs`
- `backend/src/Ppki.Application`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T03: Review decision API dan state machine.

Tujuan task:
Reviewer dapat Approved atau ChangesRequested pada version tertentu dengan comment dan audit trail.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Domain/Enums.cs
- backend/src/Ppki.Application

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement state transitions Pending/InReview/Approved/ChangesRequested.
B. Validate reviewer identity/share/current exact version.
C. Persist immutable decision; perubahan baru menjadi review round baru.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No approval for failed/incomplete audit jika policy mensyaratkan ready.
- Double submit idempotent.
- Owner tidak dapat memalsukan reviewer decision.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No approval for failed/incomplete audit jika policy mensyaratkan ready.
- [ ] Double submit idempotent.
- [ ] Owner tidak dapat memalsukan reviewer decision.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S11-T04 — Reviewer dan owner UI

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Owner dapat memilih reviewer/request review; reviewer melihat inbox dan memberi keputusan.

### File/konteks minimum yang harus dibaca

- `apps/web/src/app`
- `apps/web/src/components`
- `apps/web/src/lib/api.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T04: Reviewer dan owner UI.

Tujuan task:
Owner dapat memilih reviewer/request review; reviewer melihat inbox dan memberi keputusan.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/app
- apps/web/src/components
- apps/web/src/lib/api.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat owner share/request dialog dan status panel.
B. Buat reviewer inbox/detail read-only audit/version.
C. Buat approve/request changes confirmation dengan comment.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Role-gated routes server-side.
- Approval menunjukkan exact version/checksum.
- No editing/fix controls untuk reviewer.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Role-gated routes server-side.
- [ ] Approval menunjukkan exact version/checksum.
- [ ] No editing/fix controls untuk reviewer.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S11-T05 — Security hardening dan abuse controls

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Terapkan kontrol minimum production untuk upload/API/signed URL dan secret handling.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `compose.yaml`
- `supabase/migrations`
- `docs/SUPABASE_SETUP.md`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T05: Security hardening dan abuse controls.

Tujuan task:
Terapkan kontrol minimum production untuk upload/API/signed URL dan secret handling.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- compose.yaml
- supabase/migrations
- docs/SUPABASE_SETUP.md

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan rate limiting per user/IP untuk auth-facing API, upload, audit, export.
B. Tambahkan security headers/CORS exact origin/request limits dan optional malware scanning integration point.
C. Review logs, RLS, service key use, dependency vulnerabilities, retention/cleanup policy.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No wildcard CORS production.
- Request body/upload limits enforced.
- Secret scan/build dependency audit documented.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run build
- docker compose config

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No wildcard CORS production.
- [ ] Request body/upload limits enforced.
- [ ] Secret scan/build dependency audit documented.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run build
  docker compose config
```


---

## S11-T06 — Observability, metrics, dan log redaction

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Operasional dapat melihat job latency/failure tanpa melihat isi skripsi.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api`
- `backend/services/Ppki.Worker`
- `backend/src`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T06: Observability, metrics, dan log redaction.

Tujuan task:
Operasional dapat melihat job latency/failure tanpa melihat isi skripsi.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api
- backend/services/Ppki.Worker
- backend/src
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Standardize correlation ID dan structured events.
B. Tambahkan metrics audit/fix/export queue time, duration, failure reason category, finding count.
C. Tambahkan log redaction test/checklist dan health dashboard guidance.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No paragraph/full filename/token/URL signed in logs.
- Metric cardinality bounded.
- Error correlation dapat ditelusuri.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No paragraph/full filename/token/URL signed in logs.
- [ ] Metric cardinality bounded.
- [ ] Error correlation dapat ditelusuri.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S11-T07 — Performance dan reliability acceptance

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Ukur target audit 100 halaman dan perilaku retry/cancellation pada beban pilot.

### File/konteks minimum yang harus dibaca

- `backend/tests`
- `apps/web`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T07: Performance dan reliability acceptance.

Tujuan task:
Ukur target audit 100 halaman dan perilaku retry/cancellation pada beban pilot.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests
- apps/web
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat load/performance harness dengan DOCX sintetis, bukan data user.
B. Uji concurrent upload/audit, worker lease recovery, storage/network transient failure.
C. Catat baseline dan bottleneck; optimasi hanya yang terukur.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak ada duplicate job/version.
- Target median audit 100 halaman <2 menit pada environment pilot atau gap dijelaskan.
- No unbounded memory growth jelas.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak ada duplicate job/version.
- [ ] Target median audit 100 halaman <2 menit pada environment pilot atau gap dijelaskan.
- [ ] No unbounded memory growth jelas.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S11-T08 — Pilot corpus 10–20 DOCX dan triage

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Jalankan pilot pada 10–20 DOCX yang memiliki izin dan telah disanitasi, lalu triage false positive/negative.

### File/konteks minimum yang harus dibaca

- `backend/tests/fixtures/docx`
- `docs/RULE_COVERAGE_MVP.md`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T08: Pilot corpus 10–20 DOCX dan triage.

Tujuan task:
Jalankan pilot pada 10–20 DOCX yang memiliki izin dan telah disanitasi, lalu triage false positive/negative.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests/fixtures/docx
- docs/RULE_COVERAGE_MVP.md
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat consent/sanitization checklist dan label expected findings.
B. Catat hasil per rule/domain, parser failure, fix success, compatibility.
C. Buat issue backlog P0/P1/P2 tanpa memperluas scope MVP diam-diam.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Dokumen user tidak masuk repository.
- Hasil agregat tidak mengandung content pribadi.
- P0/P1 diselesaikan atau release diblokir.

Command verifikasi minimum:
- git diff --check

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Dokumen user tidak masuk repository.
- [ ] Hasil agregat tidak mengandung content pribadi.
- [ ] P0/P1 diselesaikan atau release diblokir.

### Command verifikasi

```bash
  git diff --check
```


---

## S11-T09 — MVP release checklist, deployment, dan rollback

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Siapkan rilis pilot yang dapat direproduksi dan dipulihkan.

### File/konteks minimum yang harus dibaca

- `README.md`
- `docs`
- `compose.yaml`
- `.github/workflows/ci.yml`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S11-T09: MVP release checklist, deployment, dan rollback.

Tujuan task:
Siapkan rilis pilot yang dapat direproduksi dan dipulihkan.

Baca hanya konteks minimum berikut terlebih dahulu:
- README.md
- docs
- compose.yaml
- .github/workflows/ci.yml

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat `docs/MVP_RELEASE_CHECKLIST.md`, environment matrix, migration order, smoke test, backup/restore, rollback.
B. Tambahkan build artifacts/version metadata dan release tag workflow.
C. Jalankan full verification dan catat known limitations.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Semua DoD MVP dicentang dengan evidence.
- No production secret di repository/artifact.
- Rollback schema/app terdokumentasi realistis.

Command verifikasi minimum:
- npm run verify
- docker compose config
- git diff --check

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Semua DoD MVP dicentang dengan evidence.
- [ ] No production secret di repository/artifact.
- [ ] Rollback schema/app terdokumentasi realistis.

### Command verifikasi

```bash
  npm run verify
  docker compose config
  git diff --check
```


---
