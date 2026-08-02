# Sprint 03 — Upload DOCX aman dan document versioning immutable

**Sprint goal:** Menghasilkan vertical slice upload yang aman, transactional, dan dapat diunduh kembali tanpa perubahan byte.

## Exit criteria

- [ ] Hanya DOCX valid yang diterima.
- [ ] Original disimpan pada private bucket dengan path immutable.
- [ ] Metadata dan object storage tetap konsisten saat gagal sebagian.
- [ ] Version history dan signed download tunduk pada ownership.
- [ ] Checksum file upload/download sama.

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

## S3-T01 — Stabilkan kontrak Document dan DocumentVersion API

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Pisahkan DTO request/response dari EF entity dan tentukan kontrak error yang konsisten.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Application/Contracts.cs`
- `backend/services/Ppki.Api/Program.cs`
- `apps/web/src/lib/api.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S3-T01: Stabilkan kontrak Document dan DocumentVersion API.

Tujuan task:
Pisahkan DTO request/response dari EF entity dan tentukan kontrak error yang konsisten.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Application/Contracts.cs
- backend/services/Ppki.Api/Program.cs
- apps/web/src/lib/api.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat DTO typed untuk create document, upload version, list, detail, dan version summary.
B. Tambahkan validation error format ProblemDetails.
C. Update TypeScript types/client agar tidak memakai `any`.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Entity EF tidak dikembalikan langsung.
- Tanggal/enum mempunyai format stabil.
- OpenAPI menggambarkan request/response utama.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run typecheck

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Entity EF tidak dikembalikan langsung.
- [ ] Tanggal/enum mempunyai format stabil.
- [ ] OpenAPI menggambarkan request/response utama.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run typecheck
```


---

## S3-T02 — Validasi DOCX berlapis

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Tolak file spoofed, corrupt, terlalu besar, atau bukan DOCX sebelum disimpan permanen.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Application`
- `backend/tests`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S3-T02: Validasi DOCX berlapis.

Tujuan task:
Tolak file spoofed, corrupt, terlalu besar, atau bukan DOCX sebelum disimpan permanen.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Application
- backend/tests

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Validasi extension, declared MIME, ukuran, ZIP signature, `[Content_Types].xml`, dan main document part.
B. Sanitize filename dan batasi jumlah entry/uncompressed size untuk mengurangi ZIP bomb risk.
C. Tambahkan test valid, wrong MIME, renamed ZIP, corrupt ZIP, oversized, dan macro-enabled file.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Pesan user aman dan spesifik.
- Tidak ada full file content di log.
- Validation dapat dijalankan streaming/temporary file dengan cleanup.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Pesan user aman dan spesifik.
- [ ] Tidak ada full file content di log.
- [ ] Validation dapat dijalankan streaming/temporary file dengan cleanup.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S3-T03 — Immutable Supabase Storage key strategy

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Setiap version menggunakan key unik yang tidak pernah di-upsert atau ditimpa.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs`
- `backend/src/Ppki.Infrastructure/SupabaseOptions.cs`
- `docs/SUPABASE_SETUP.md`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S3-T03: Immutable Supabase Storage key strategy.

Tujuan task:
Setiap version menggunakan key unik yang tidak pernah di-upsert atau ditimpa.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs
- backend/src/Ppki.Infrastructure/SupabaseOptions.cs
- docs/SUPABASE_SETUP.md

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Definisikan key builder berbasis owner/document/version/random immutable identifier.
B. Nonaktifkan overwrite/upsert dan verifikasi bucket allowlist.
C. Tambahkan unit test path traversal, duplicate key, dan bucket mismatch.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Key tidak memakai raw filename sebagai path authority.
- Original dan fixed version berada di bucket yang benar.
- Delete/overwrite tidak tersedia pada public application service.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Key tidak memakai raw filename sebagai path authority.
- [ ] Original dan fixed version berada di bucket yang benar.
- [ ] Delete/overwrite tidak tersedia pada public application service.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S3-T04 — Transactional upload orchestration dan compensation

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Upload storage dan metadata database tidak boleh meninggalkan orphan tanpa jejak ketika salah satu langkah gagal.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Application`
- `backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S3-T04: Transactional upload orchestration dan compensation.

Tujuan task:
Upload storage dan metadata database tidak boleh meninggalkan orphan tanpa jejak ketika salah satu langkah gagal.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Application
- backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat application service upload dengan tahapan validate, reserve metadata, upload, finalize.
B. Implementasikan compensation aman atau status pending/failed untuk kegagalan parsial.
C. Tulis audit trail dan correlation ID.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Retry tidak membuat document/version duplikat.
- Orphan object dapat diidentifikasi dan dibersihkan.
- Original checksum dihitung streaming.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Retry tidak membuat document/version duplikat.
- [ ] Orphan object dapat diidentifikasi dan dibersihkan.
- [ ] Original checksum dihitung streaming.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S3-T05 — Signed download dan checksum verification

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Owner dapat meminta URL unduhan singkat; user lain tidak dapat memperoleh URL.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S3-T05: Signed download dan checksum verification.

Tujuan task:
Owner dapat meminta URL unduhan singkat; user lain tidak dapat memperoleh URL.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat service signed URL dengan TTL terkonfigurasi dan allowlist bucket.
B. Tambahkan endpoint metadata download yang mengembalikan filename/checksum/expiry.
C. Tambahkan test ownership, expired/invalid ID, dan TTL bounds.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- URL tidak disimpan permanen di database.
- TTL default maksimal 5 menit untuk MVP.
- Checksum metadata cocok dengan file yang diunduh pada integration test.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] URL tidak disimpan permanen di database.
- [ ] TTL default maksimal 5 menit untuk MVP.
- [ ] Checksum metadata cocok dengan file yang diunduh pada integration test.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S3-T06 — Document list, detail, dan version history API

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Expose query yang dipaginasi untuk dashboard dan riwayat versi tanpa N+1.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Application`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S3-T06: Document list, detail, dan version history API.

Tujuan task:
Expose query yang dipaginasi untuk dashboard dan riwayat versi tanpa N+1.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Application
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement list pagination/sort/filter status sederhana.
B. Implement detail dengan current version dan audit terbaru.
C. Implement versions endpoint ordered descending.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Semua query ownership-scoped.
- Tidak mengembalikan storage secret/key bila tidak diperlukan.
- Pagination mempunyai max page size.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Semua query ownership-scoped.
- [ ] Tidak mengembalikan storage secret/key bila tidak diperlukan.
- [ ] Pagination mempunyai max page size.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S3-T07 — Frontend dashboard dan upload UX

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** User dapat melihat dokumen, mengunggah DOCX, dan memahami status upload tanpa kehilangan session.

### File/konteks minimum yang harus dibaca

- `apps/web/src/app/page.tsx`
- `apps/web/src/app/documents/new/page.tsx`
- `apps/web/src/components/documents-client.tsx`
- `apps/web/src/lib/api.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S3-T07: Frontend dashboard dan upload UX.

Tujuan task:
User dapat melihat dokumen, mengunggah DOCX, dan memahami status upload tanpa kehilangan session.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/app/page.tsx
- apps/web/src/app/documents/new/page.tsx
- apps/web/src/components/documents-client.tsx
- apps/web/src/lib/api.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat form typed dengan client-side extension/size hint, tetapi backend tetap authority.
B. Tambahkan upload progress/pending/error/success dan redirect ke detail.
C. Tambahkan dashboard pagination/empty state.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Double submit dicegah.
- Error ProblemDetails ditampilkan ringkas.
- Accessibility label dan keyboard flow tersedia.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Double submit dicegah.
- [ ] Error ProblemDetails ditampilkan ringkas.
- [ ] Accessibility label dan keyboard flow tersedia.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S3-T08 — End-to-end test upload/version/download

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Buktikan file original tidak berubah dari upload sampai download.

### File/konteks minimum yang harus dibaca

- `backend/tests`
- `apps/web`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S3-T08: End-to-end test upload/version/download.

Tujuan task:
Buktikan file original tidak berubah dari upload sampai download.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests
- apps/web
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat integration test user A upload DOCX sintetis dan verify DB/storage.
B. Download via signed URL dan bandingkan SHA-256 byte-for-byte.
C. Buktikan user B tidak dapat list/detail/download.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Test memakai test project/prefix saja.
- Cleanup aman.
- Hasil dan prasyarat didokumentasikan.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Test memakai test project/prefix saja.
- [ ] Cleanup aman.
- [ ] Hasil dan prasyarat didokumentasikan.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---
