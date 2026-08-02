# Sprint 01 — Schema Supabase, RLS, integritas data, dan audit trail

**Sprint goal:** Menjadikan Supabase sebagai data plane yang aman, terversi, dan konsisten dengan model domain/EF Core.

## Exit criteria

- [ ] Migration additive membuat constraint/index/policy yang diperlukan.
- [ ] RLS mencegah akses lintas user melalui Data API.
- [ ] API/worker service access tidak membuka secret ke browser.
- [ ] Schema SQL dan EF mapping konsisten.
- [ ] Audit trail dasar tersedia untuk aksi sensitif.

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

## S1-T01 — Audit schema awal dan buat migration koreksi additive

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Temukan drift antara schema Supabase dan EF model lalu perbaiki melalui migration baru.

### File/konteks minimum yang harus dibaca

- `supabase/migrations/202608010001_initial_schema.sql`
- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S1-T01: Audit schema awal dan buat migration koreksi additive.

Tujuan task:
Temukan drift antara schema Supabase dan EF model lalu perbaiki melalui migration baru.

Baca hanya konteks minimum berikut terlebih dahulu:
- supabase/migrations/202608010001_initial_schema.sql
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Bandingkan nama kolom, FK, enum string, nullability, default, unique constraint, dan cascade behavior.
B. Buat migration SQL baru bertimestamp setelah migration awal; jangan rewrite file awal.
C. Tambahkan dokumen `docs/SCHEMA_MAPPING.md` yang memetakan entity ke tabel.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Migration idempotent sesuai pola Supabase.
- Tidak ada destructive drop tanpa strategi migrasi.
- EF query utama tidak bergantung pada relationship yang tidak dipetakan.

Command verifikasi minimum:
- dotnet build backend/PpkiSmartFormatter.slnx
- dotnet test backend/PpkiSmartFormatter.slnx
- npx supabase db lint

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Migration idempotent sesuai pola Supabase.
- [ ] Tidak ada destructive drop tanpa strategi migrasi.
- [ ] EF query utama tidak bergantung pada relationship yang tidak dipetakan.

### Command verifikasi

```bash
  dotnet build backend/PpkiSmartFormatter.slnx
  dotnet test backend/PpkiSmartFormatter.slnx
  npx supabase db lint
```


---

## S1-T02 — Perketat constraint dan index untuk lifecycle dokumen/audit

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Database harus menolak state yang mustahil dan mendukung query worker/API dengan index tepat.

### File/konteks minimum yang harus dibaca

- `supabase/migrations`
- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S1-T02: Perketat constraint dan index untuk lifecycle dokumen/audit.

Tujuan task:
Database harus menolak state yang mustahil dan mendukung query worker/API dengan index tepat.

Baca hanya konteks minimum berikut terlebih dahulu:
- supabase/migrations
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan check constraint untuk status, version_no positif, size non-negatif, SHA-256 valid, dan timestamp lifecycle.
B. Tambahkan index ownership, version lookup, audit queue, finding filter, dan profile status.
C. Tambahkan test integrasi atau SQL assertions untuk constraint kritis.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Duplicate version dan storage path ditolak.
- Audit status/timestamp tidak dapat masuk state invalid yang jelas.
- Index tidak duplikatif.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npx supabase db lint

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Duplicate version dan storage path ditolak.
- [ ] Audit status/timestamp tidak dapat masuk state invalid yang jelas.
- [ ] Index tidak duplikatif.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npx supabase db lint
```


---

## S1-T03 — Lengkapi RLS policy defense-in-depth

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Authenticated user hanya boleh membaca data miliknya; role reviewer/admin belum boleh mendapat akses implisit.

### File/konteks minimum yang harus dibaca

- `supabase/migrations/202608010001_initial_schema.sql`
- `supabase/migrations`
- `docs/SUPABASE_SETUP.md`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S1-T03: Lengkapi RLS policy defense-in-depth.

Tujuan task:
Authenticated user hanya boleh membaca data miliknya; role reviewer/admin belum boleh mendapat akses implisit.

Baca hanya konteks minimum berikut terlebih dahulu:
- supabase/migrations/202608010001_initial_schema.sql
- supabase/migrations
- docs/SUPABASE_SETUP.md

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Inventarisasi tabel public dan storage object yang memerlukan RLS.
B. Buat migration policy read/write minimal untuk user profile dan data milik user; default-deny untuk yang lain.
C. Tambahkan SQL test plan untuk dua user berbeda dan service role.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- User A tidak dapat membaca document/version/audit/finding User B via Data API.
- Browser tidak mempunyai policy langsung untuk private Storage object.
- Service role hanya dipakai server-side.

Command verifikasi minimum:
- npx supabase db lint
- git diff --check

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] User A tidak dapat membaca document/version/audit/finding User B via Data API.
- [ ] Browser tidak mempunyai policy langsung untuk private Storage object.
- [ ] Service role hanya dipakai server-side.

### Command verifikasi

```bash
  npx supabase db lint
  git diff --check
```


---

## S1-T04 — Tambahkan audit trail append-only

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Simpan jejak aktor, aksi, objek, waktu, correlation ID, dan metadata aman untuk perubahan penting.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S1-T04: Tambahkan audit trail append-only.

Tujuan task:
Simpan jejak aktor, aksi, objek, waktu, correlation ID, dan metadata aman untuk perubahan penting.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan entity/table `audit_trail_entries` dengan payload JSON terbatas.
B. Buat service aplikasi untuk menulis event tanpa isi paragraf/dokumen.
C. Integrasikan minimal pada upload, create audit, dan signed-download request.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Entry tidak dapat di-update/delete oleh user biasa.
- Payload tidak berisi token, secret, atau full document text.
- Correlation ID dapat ditelusuri dari API log ke entry.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npx supabase db lint

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Entry tidak dapat di-update/delete oleh user biasa.
- [ ] Payload tidak berisi token, secret, atau full document text.
- [ ] Correlation ID dapat ditelusuri dari API log ke entry.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npx supabase db lint
```


---

## S1-T05 — Selaraskan JSONB mapping actual/expected/location

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Hindari penyimpanan JSON sebagai text ketika schema menggunakan jsonb dan pastikan kontrak tetap stabil.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`
- `supabase/migrations/202608010001_initial_schema.sql`
- `backend/src/Ppki.RuleEngine/AuditRunner.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S1-T05: Selaraskan JSONB mapping actual/expected/location.

Tujuan task:
Hindari penyimpanan JSON sebagai text ketika schema menggunakan jsonb dan pastikan kontrak tetap stabil.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs
- supabase/migrations/202608010001_initial_schema.sql
- backend/src/Ppki.RuleEngine/AuditRunner.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Pilih mapping EF JSON yang konsisten untuk `actual_value`, `expected_value`, dan `location`.
B. Buat migration conversion aman bila diperlukan dan update serialization boundary.
C. Tambahkan round-trip test untuk payload location dan numeric values.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak terjadi double-encoded JSON.
- API mengembalikan object JSON, bukan string JSON.
- Existing row dapat dimigrasikan atau strategi reset dev didokumentasi.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- dotnet build backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak terjadi double-encoded JSON.
- [ ] API mengembalikan object JSON, bukan string JSON.
- [ ] Existing row dapat dimigrasikan atau strategi reset dev didokumentasi.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  dotnet build backend/PpkiSmartFormatter.slnx
```


---

## S1-T06 — Buat test harness Supabase integration yang terisolasi

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Sediakan pola integration test yang dapat berjalan terhadap Supabase local atau project test khusus tanpa menyentuh production.

### File/konteks minimum yang harus dibaca

- `backend/tests`
- `supabase/config.toml`
- `supabase/seed.sql`
- `.github/workflows/ci.yml`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S1-T06: Buat test harness Supabase integration yang terisolasi.

Tujuan task:
Sediakan pola integration test yang dapat berjalan terhadap Supabase local atau project test khusus tanpa menyentuh production.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests
- supabase/config.toml
- supabase/seed.sql
- .github/workflows/ci.yml

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan project test integration dan fixture lifecycle database/storage.
B. Gunakan environment khusus test dan random prefix untuk object storage.
C. Tambahkan cleanup yang aman dan dokumentasi menjalankan test.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Test menolak production project ref secara eksplisit.
- Cleanup hanya menghapus data dengan test prefix.
- CI dapat skip dengan alasan jelas bila service lokal tidak tersedia.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- git diff --check

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Test menolak production project ref secara eksplisit.
- [ ] Cleanup hanya menghapus data dengan test prefix.
- [ ] CI dapat skip dengan alasan jelas bila service lokal tidak tersedia.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  git diff --check
```


---
