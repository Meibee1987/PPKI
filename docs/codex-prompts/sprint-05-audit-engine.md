# Sprint 05 — Rule resolution, audit worker, scoring, dan findings persistence

**Sprint goal:** Audit asynchronous harus reproducible, idempotent, concurrency-safe, dan menghasilkan finding lengkap.

## Exit criteria

- [ ] Audit terikat pada DocumentVersion dan ProfileVersion.
- [ ] Resolved rule set hash canonical dan reproducible.
- [ ] Worker tidak mengklaim job yang sama dua kali.
- [ ] Finding menyimpan rule/source/actual/expected/location/severity/fix mode snapshot yang cukup.
- [ ] Retry/failure tidak menggandakan findings.

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

## S5-T01 — Rule applicability dan profile resolver

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Audit hanya menjalankan rule yang applicable untuk jenis dokumen dan profile version aktif.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Infrastructure/RuleCatalogImporter.cs`
- `backend/src/Ppki.RuleEngine/AuditRunner.cs`
- `rules/ppki-ipb-2019/rules.json`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S5-T01: Rule applicability dan profile resolver.

Tujuan task:
Audit hanya menjalankan rule yang applicable untuk jenis dokumen dan profile version aktif.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Infrastructure/RuleCatalogImporter.cs
- backend/src/Ppki.RuleEngine/AuditRunner.cs
- rules/ppki-ipb-2019/rules.json

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat `ResolvedRuleSet` service dengan document kind, profile version, status/effective date.
B. Normalisasi `applies_to` katalog menjadi condition teruji tanpa hardcode tersebar.
C. Tambahkan test Skripsi vs Tesis/Laporan Akhir untuk beberapa rule.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Rule non-applicable tidak masuk denominator.
- Profile version inactive tidak dapat dipakai audit baru.
- Resolver deterministic.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Rule non-applicable tidak masuk denominator.
- [ ] Profile version inactive tidak dapat dipakai audit baru.
- [ ] Resolver deterministic.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S5-T02 — Canonical resolved rule-set hash dan snapshot

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Hash harus berubah bila requirement/validator/severity/fix mode/source yang efektif berubah, bukan hanya code/key.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine/AuditRunner.cs`
- `backend/src/Ppki.Domain/Entities.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S5-T02: Canonical resolved rule-set hash dan snapshot.

Tujuan task:
Hash harus berubah bila requirement/validator/severity/fix mode/source yang efektif berubah, bukan hanya code/key.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine/AuditRunner.cs
- backend/src/Ppki.Domain/Entities.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Definisikan canonical JSON ordering untuk resolved rules.
B. Simpan hash dan snapshot metadata/version pada AuditJob atau table snapshot.
C. Tambahkan golden hash tests dan backward compatibility note.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Urutan query tidak memengaruhi hash.
- Semua field load-bearing masuk canonical form.
- Audit lama tetap dapat dijelaskan saat katalog berubah.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Urutan query tidak memengaruhi hash.
- [ ] Semua field load-bearing masuk canonical form.
- [ ] Audit lama tetap dapat dijelaskan saat katalog berubah.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S5-T03 — Validator registry dan startup validation

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Mapping rule-to-validator tidak boleh diam-diam hilang atau duplikat.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine`
- `backend/services/Ppki.Worker/Program.cs`
- `backend/src/Ppki.Infrastructure/RuleCatalogImporter.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S5-T03: Validator registry dan startup validation.

Tujuan task:
Mapping rule-to-validator tidak boleh diam-diam hilang atau duplikat.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine
- backend/services/Ppki.Worker/Program.cs
- backend/src/Ppki.Infrastructure/RuleCatalogImporter.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat registry typed dan validasi duplicate ValidationKey.
B. Pada startup, laporkan rule implemented tanpa validator dan validator tak terpakai sebagai error/warning yang tepat.
C. Pindahkan mapping implemented rule ke satu tempat yang mudah diuji.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Implemented rule tanpa validator menggagalkan readiness worker.
- Manual/not implemented rule tidak dianggap error.
- Tidak ada reflection bebas dari database.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Implemented rule tanpa validator menggagalkan readiness worker.
- [ ] Manual/not implemented rule tidak dianggap error.
- [ ] Tidak ada reflection bebas dari database.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S5-T04 — Atomic audit job claim dan idempotency

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Beberapa worker tidak boleh memproses AuditJob yang sama.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Worker/QueuedAuditWorker.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S5-T04: Atomic audit job claim dan idempotency.

Tujuan task:
Beberapa worker tidak boleh memproses AuditJob yang sama.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Worker/QueuedAuditWorker.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement atomic claim menggunakan PostgreSQL locking/UPDATE RETURNING yang sesuai.
B. Tambahkan attempt count, lock owner/lease atau processing token, dan idempotency key untuk create audit.
C. Tambahkan concurrency integration test dua worker claimant.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tepat satu claimant menang.
- Stale processing job dapat direcover sesuai policy.
- Create audit retry tidak menghasilkan job duplikat aktif.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tepat satu claimant menang.
- [ ] Stale processing job dapat direcover sesuai policy.
- [ ] Create audit retry tidak menghasilkan job duplikat aktif.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S5-T05 — Idempotent findings persistence dan retry

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Rerun attempt pada job yang sama tidak menggandakan findings atau summary.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine/AuditRunner.cs`
- `backend/src/Ppki.Domain/Entities.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S5-T05: Idempotent findings persistence dan retry.

Tujuan task:
Rerun attempt pada job yang sama tidak menggandakan findings atau summary.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine/AuditRunner.cs
- backend/src/Ppki.Domain/Entities.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan attempt transaction boundary dan clear/replace strategy aman.
B. Gunakan finding fingerprint atau unique key per attempt/rule/location bila perlu.
C. Tambahkan test fail setelah parse, fail saat save, lalu retry.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Summary sesuai findings final.
- Completed job tidak diproses ulang tanpa explicit rerun.
- Error message disanitasi.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Summary sesuai findings final.
- [ ] Completed job tidak diproses ulang tanpa explicit rerun.
- [ ] Error message disanitasi.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S5-T06 — Scoring dan export-blocking policy

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Skor informatif tetapi tidak menyembunyikan blocking error.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine/AuditRunner.cs`
- `backend/src/Ppki.Domain/Entities.cs`
- `rules/ppki-ipb-2019/rules.json`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S5-T06: Scoring dan export-blocking policy.

Tujuan task:
Skor informatif tetapi tidak menyembunyikan blocking error.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine/AuditRunner.cs
- backend/src/Ppki.Domain/Entities.cs
- rules/ppki-ipb-2019/rules.json

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement denominator applicable rules dan weight terkonfigurasi.
B. Tambahkan `is_export_blocking`/policy snapshot yang tidak otomatis berasal dari severity saja.
C. Tambahkan test no findings, mixed severity, multiple findings same rule, non-applicable.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Info tidak mengurangi skor.
- Skor bounded 0..100.
- Blocking count tersedia terpisah.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Info tidak mengurangi skor.
- [ ] Skor bounded 0..100.
- [ ] Blocking count tersedia terpisah.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S5-T07 — Finding snapshot yang explainable

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Setiap finding tetap dapat dijelaskan meskipun RuleDefinition berubah kemudian.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.RuleEngine/RuleContracts.cs`
- `backend/src/Ppki.RuleEngine/AuditRunner.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S5-T07: Finding snapshot yang explainable.

Tujuan task:
Setiap finding tetap dapat dijelaskan meskipun RuleDefinition berubah kemudian.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.RuleEngine/RuleContracts.cs
- backend/src/Ppki.RuleEngine/AuditRunner.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan snapshot rule_code, title/element, official requirement, source, severity, fix mode, expected ke finding atau audit snapshot.
B. Simpan message template/validator version bila relevan.
C. Update API response dan migration.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Finding lama tidak membutuhkan join ke rule terbaru untuk penjelasan utama.
- Payload actual/expected/location typed dan bounded.
- Tidak menyimpan full paragraph content.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Finding lama tidak membutuhkan join ke rule terbaru untuk penjelasan utama.
- [ ] Payload actual/expected/location typed dan bounded.
- [ ] Tidak menyimpan full paragraph content.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S5-T08 — Audit API: create, status, filters, pagination, retry

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Expose API async yang stabil untuk frontend audit log.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Application/Contracts.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S5-T08: Audit API: create, status, filters, pagination, retry.

Tujuan task:
Expose API async yang stabil untuk frontend audit log.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Application/Contracts.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambah idempotency header pada create audit dan endpoint retry untuk failed job.
B. Tambah findings filter severity/domain/status/fix mode dan pagination.
C. Tambah response progress phase dan retry-after hint.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Ownership selalu diterapkan.
- Page size dibatasi.
- Retry hanya valid untuk state yang diizinkan.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Ownership selalu diterapkan.
- [ ] Page size dibatasi.
- [ ] Retry hanya valid untuk state yang diizinkan.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---
