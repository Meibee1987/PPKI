# Sprint 10 — Version history, export DOCX, dan laporan audit JSON/PDF

**Sprint goal:** User dapat menelusuri versi dan mengunduh artefak hasil yang terverifikasi serta laporan kepatuhan.

## Exit criteria

- [ ] Version history menunjukkan parent, reason, audit, fix plan, checksum.
- [ ] Export policy memblokir blocking error sesuai snapshot.
- [ ] Final DOCX disalin/ditandai tanpa mutasi tersembunyi.
- [ ] Audit report JSON dan PDF tersimpan private.
- [ ] Unduhan menggunakan signed URL dan audit trail.

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

## S10-T01 — Schema Export dan report artifact

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Modelkan export job/artifact untuk DOCX, JSON, PDF dengan source version/audit/profile/hash/checksum.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S10-T01: Schema Export dan report artifact.

Tujuan task:
Modelkan export job/artifact untuk DOCX, JSON, PDF dengan source version/audit/profile/hash/checksum.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan ExportJob/ExportArtifact status, format, storage metadata, actor, timestamps.
B. Tambahkan constraint satu logical artifact per job/format dan retry fields.
C. Buat migration/EF mapping.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Artifact immutable.
- Failed job tidak dianggap downloadable.
- No public storage URL persisted.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npx supabase db lint

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Artifact immutable.
- [ ] Failed job tidak dianggap downloadable.
- [ ] No public storage URL persisted.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npx supabase db lint
```


---

## S10-T02 — Version history API dan UI

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** User melihat graph linear parent version, checksum, created by, source fix plan, dan audit terbaru.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `apps/web/src/app/documents/[id]/page.tsx`
- `apps/web/src/components`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S10-T02: Version history API dan UI.

Tujuan task:
User melihat graph linear parent version, checksum, created by, source fix plan, dan audit terbaru.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- apps/web/src/app/documents/[id]/page.tsx
- apps/web/src/components

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement version history response yang paginated/ordered.
B. Tampilkan original/fixed/manual upload labels dan current marker.
C. Tambahkan signed download action per version.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Ownership enforced.
- No storage key exposed unnecessarily.
- Current version bukan hasil asumsi client.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Ownership enforced.
- [ ] No storage key exposed unnecessarily.
- [ ] Current version bukan hasil asumsi client.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run build
```


---

## S10-T03 — Export policy service

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Tentukan apakah version dapat diekspor final berdasarkan audit terbaru, blocking findings, decisions, dan job state.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Application`
- `backend/src/Ppki.RuleEngine`
- `backend/src/Ppki.Domain/Entities.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S10-T03: Export policy service.

Tujuan task:
Tentukan apakah version dapat diekspor final berdasarkan audit terbaru, blocking findings, decisions, dan job state.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Application
- backend/src/Ppki.RuleEngine
- backend/src/Ppki.Domain/Entities.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement typed eligibility result/reasons.
B. Require completed audit untuk exact version/profile.
C. Tambahkan unit tests blocking error, warning, manual decision, stale audit.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Score tinggi tidak override blocking error.
- Policy snapshot dicatat pada export job.
- UI dapat menampilkan alasan blok.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Score tinggi tidak override blocking error.
- [ ] Policy snapshot dicatat pada export job.
- [ ] UI dapat menampilkan alasan blok.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S10-T04 — Final DOCX export worker

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Buat export DOCX dari exact DocumentVersion tanpa menulis ulang content jika tidak ada transform export.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Worker`
- `backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs`
- `backend/src/Ppki.Application`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S10-T04: Final DOCX export worker.

Tujuan task:
Buat export DOCX dari exact DocumentVersion tanpa menulis ulang content jika tidak ada transform export.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Worker
- backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs
- backend/src/Ppki.Application

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Queue/claim export job atomic.
B. Download source, verify checksum, copy ke immutable export key atau gunakan artifact reference sesuai desain.
C. Upload/finalize artifact dan audit trail.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Export byte sama dengan source version bila hanya packaging/copy.
- Retry idempotent.
- Temporary files cleaned.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Export byte sama dengan source version bila hanya packaging/copy.
- [ ] Retry idempotent.
- [ ] Temporary files cleaned.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S10-T05 — Audit report JSON canonical

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Generate machine-readable report yang cukup untuk reproduksi dan tidak bergantung pada rule terbaru.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Application`
- `backend/services/Ppki.Worker`
- `backend/src/Ppki.RuleEngine`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S10-T05: Audit report JSON canonical.

Tujuan task:
Generate machine-readable report yang cukup untuk reproduksi dan tidak bergantung pada rule terbaru.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Application
- backend/services/Ppki.Worker
- backend/src/Ppki.RuleEngine

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Definisikan schema version, document/version checksum, profile, rule-set hash, summary, findings snapshot, decisions, fixes.
B. Canonical serialize dan upload ke `audit-reports`.
C. Tambahkan JSON schema/golden test.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No secret/storage key/full document text.
- Report deterministic untuk snapshot yang sama selain generated timestamp yang dinormalisasi test.
- Schema version documented.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No secret/storage key/full document text.
- [ ] Report deterministic untuk snapshot yang sama selain generated timestamp yang dinormalisasi test.
- [ ] Schema version documented.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S10-T06 — Audit report PDF

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Generate laporan PDF ringkas dan dapat dibaca dari data report snapshot, bukan dari DOCX content.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Application`
- `backend/services/Ppki.Worker`
- `backend/Directory.Packages.props`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S10-T06: Audit report PDF.

Tujuan task:
Generate laporan PDF ringkas dan dapat dibaca dari data report snapshot, bukan dari DOCX content.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Application
- backend/services/Ppki.Worker
- backend/Directory.Packages.props
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat report renderer abstraction dan implement PDF deterministic dengan dependency minimal berlisensi jelas.
B. Render cover metadata, summary, domain breakdown, finding table, source, decisions, checksum.
C. Tambahkan smoke test PDF signature/page count/text metadata tanpa snapshot pixel rapuh.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- PDF tidak memuat isi lengkap skripsi.
- Dependency/license dicatat.
- Long finding list dipaginasi tanpa crash.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] PDF tidak memuat isi lengkap skripsi.
- [ ] Dependency/license dicatat.
- [ ] Long finding list dipaginasi tanpa crash.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S10-T07 — Export API, progress UI, dan signed downloads

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** User dapat request export, melihat progress/blocked reason, lalu mengunduh DOCX/JSON/PDF.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `apps/web/src/app/documents/[id]/page.tsx`
- `apps/web/src/components`
- `apps/web/src/lib/api.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S10-T07: Export API, progress UI, dan signed downloads.

Tujuan task:
User dapat request export, melihat progress/blocked reason, lalu mengunduh DOCX/JSON/PDF.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- apps/web/src/app/documents/[id]/page.tsx
- apps/web/src/components
- apps/web/src/lib/api.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement create/status/artifacts endpoints dengan idempotency.
B. Tambahkan UI export panel dan polling.
C. Gunakan signed URL pendek dan log request.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak dapat export version user lain.
- Blocked reason actionable.
- Completed artifacts mempunyai checksum/size.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak dapat export version user lain.
- [ ] Blocked reason actionable.
- [ ] Completed artifacts mempunyai checksum/size.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run build
```


---

## S10-T08 — Compatibility dan artifact integrity tests

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Validasi hasil DOCX/JSON/PDF dan download terhadap corruption/regression.

### File/konteks minimum yang harus dibaca

- `backend/tests`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S10-T08: Compatibility dan artifact integrity tests.

Tujuan task:
Validasi hasil DOCX/JSON/PDF dan download terhadap corruption/regression.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Open exported DOCX via Open XML and optional manual Word/LibreOffice checklist.
B. Validate JSON schema dan PDF smoke.
C. Download artifact dan compare stored checksum.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No corrupt DOCX.
- All artifact checksums match.
- Manual compatibility gap dicatat jujur.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No corrupt DOCX.
- [ ] All artifact checksums match.
- [ ] Manual compatibility gap dicatat jujur.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---
