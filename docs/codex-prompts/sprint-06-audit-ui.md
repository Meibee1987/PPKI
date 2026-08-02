# Sprint 06 — Audit progress, summary, finding log, dan keputusan manual

**Sprint goal:** User melihat apa yang salah sebelum perubahan apa pun diterapkan, lengkap dengan sumber PPKI dan tindakan.

## Exit criteria

- [ ] Progress audit dapat dipantau dan diretry.
- [ ] Finding dapat difilter, dicari, dan dibuka detailnya.
- [ ] Actual/expected/location/source/fix mode terlihat jelas.
- [ ] Manual review/ignore membutuhkan alasan.
- [ ] UI accessible dan tidak bergantung warna saja.

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

## S6-T01 — Typed frontend API client dan error model

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Frontend menggunakan type/contract eksplisit untuk document, audit, finding, dan ProblemDetails.

### File/konteks minimum yang harus dibaca

- `apps/web/src/lib/api.ts`
- `backend/src/Ppki.Application/Contracts.cs`
- `backend/services/Ppki.Api/Program.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S6-T01: Typed frontend API client dan error model.

Tujuan task:
Frontend menggunakan type/contract eksplisit untuk document, audit, finding, dan ProblemDetails.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/lib/api.ts
- backend/src/Ppki.Application/Contracts.cs
- backend/services/Ppki.Api/Program.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan typed fetch wrapper dengan bearer session, cancellation, dan JSON/error parsing.
B. Definisikan DTO TypeScript selaras OpenAPI/manual contract.
C. Hilangkan `any` dan duplicated fetch logic pada page/component.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- 401 memicu auth flow yang tepat.
- Abort tidak ditampilkan sebagai error user.
- ProblemDetails message aman ditampilkan.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] 401 memicu auth flow yang tepat.
- [ ] Abort tidak ditampilkan sebagai error user.
- [ ] ProblemDetails message aman ditampilkan.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S6-T02 — Audit progress polling dan retry UX

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** User dapat melihat Queued/Processing/Completed/Failed tanpa refresh manual.

### File/konteks minimum yang harus dibaca

- `apps/web/src/app/documents/[id]/page.tsx`
- `apps/web/src/components`
- `apps/web/src/lib/api.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S6-T02: Audit progress polling dan retry UX.

Tujuan task:
User dapat melihat Queued/Processing/Completed/Failed tanpa refresh manual.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/app/documents/[id]/page.tsx
- apps/web/src/components
- apps/web/src/lib/api.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat polling hook dengan backoff, visibility pause, timeout, dan cleanup.
B. Tampilkan phase/progress timestamps serta failed error yang disanitasi.
C. Tambahkan retry action untuk failed audit.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak ada polling leak setelah navigasi.
- Completed berhenti polling.
- Rate request bounded.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak ada polling leak setelah navigasi.
- [ ] Completed berhenti polling.
- [ ] Rate request bounded.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S6-T03 — Audit summary dan readiness status

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Tampilkan score, error/warning/info, blocking error, applicable rules, profile version, dan rule-set hash ringkas.

### File/konteks minimum yang harus dibaca

- `apps/web/src/app/documents/[id]/page.tsx`
- `apps/web/src/components/status-badge.tsx`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S6-T03: Audit summary dan readiness status.

Tujuan task:
Tampilkan score, error/warning/info, blocking error, applicable rules, profile version, dan rule-set hash ringkas.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/app/documents/[id]/page.tsx
- apps/web/src/components/status-badge.tsx

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat summary cards dan status Needs Fix/Ready for Review.
B. Tambahkan tooltip penjelasan score dan hash.
C. Tangani no-audit dan failed state.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Status tidak hanya berdasarkan score.
- Hash tidak memenuhi layar; copy action aman.
- Semua label dapat dibaca screen reader.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Status tidak hanya berdasarkan score.
- [ ] Hash tidak memenuhi layar; copy action aman.
- [ ] Semua label dapat dibaca screen reader.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S6-T04 — Finding list dengan filter, search, dan pagination

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Finding log tetap usable pada ratusan temuan.

### File/konteks minimum yang harus dibaca

- `apps/web/src/components/finding-card.tsx`
- `apps/web/src/app/documents/[id]/page.tsx`
- `apps/web/src/lib/api.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S6-T04: Finding list dengan filter, search, dan pagination.

Tujuan task:
Finding log tetap usable pada ratusan temuan.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/components/finding-card.tsx
- apps/web/src/app/documents/[id]/page.tsx
- apps/web/src/lib/api.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement server-side pagination/filter query.
B. Tambahkan filter severity/domain/status/fix mode dan search code/title.
C. Persist filter di URL query.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Filter dapat dibagikan via URL.
- Empty state spesifik.
- Tidak memuat semua findings sekaligus.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Filter dapat dibagikan via URL.
- [ ] Empty state spesifik.
- [ ] Tidak memuat semua findings sekaligus.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S6-T05 — Finding detail drawer

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Detail finding menampilkan seluruh informasi sebelum user memutuskan tindakan.

### File/konteks minimum yang harus dibaca

- `apps/web/src/components/finding-card.tsx`
- `apps/web/src/components`
- `apps/web/src/app/documents/[id]/page.tsx`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S6-T05: Finding detail drawer.

Tujuan task:
Detail finding menampilkan seluruh informasi sebelum user memutuskan tindakan.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/components/finding-card.tsx
- apps/web/src/components
- apps/web/src/app/documents/[id]/page.tsx

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat drawer/modal accessible dengan rule code/title, actual, expected, location, severity, fix mode, confidence.
B. Render JSON actual/expected secara human-readable tanpa raw unsafe HTML.
C. Tampilkan source section/PDF/printed page dan action placeholder.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Focus trap dan escape bekerja.
- Long values/truncated locations ditangani.
- No `dangerouslySetInnerHTML` untuk data finding.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Focus trap dan escape bekerja.
- [ ] Long values/truncated locations ditangani.
- [ ] No `dangerouslySetInnerHTML` untuk data finding.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S6-T06 — Manual review dan ignore dengan alasan

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** User dapat menandai ManualReview atau Ignored hanya dengan alasan dan policy yang benar.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Domain/Entities.cs`
- `supabase/migrations`
- `apps/web/src/components`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S6-T06: Manual review dan ignore dengan alasan.

Tujuan task:
User dapat menandai ManualReview atau Ignored hanya dengan alasan dan policy yang benar.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Domain/Entities.cs
- supabase/migrations
- apps/web/src/components

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan finding decision entity/columns dengan actor/time/reason.
B. Implement API transition validation dan audit trail.
C. Tambahkan dialog UI reason serta optimistic state yang aman.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Blocking error tertentu tidak dapat di-ignore.
- Reason length divalidasi dan plain text.
- Keputusan dapat ditelusuri.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run typecheck

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Blocking error tertentu tidak dapat di-ignore.
- [ ] Reason length divalidasi dan plain text.
- [ ] Keputusan dapat ditelusuri.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run typecheck
```


---

## S6-T07 — Source reference navigation

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** User dapat memahami asal rule tanpa aplikasi mengklaim source yang tidak tersedia.

### File/konteks minimum yang harus dibaca

- `apps/web/src/components`
- `rules/ppki-ipb-2019/rules.json`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S6-T07: Source reference navigation.

Tujuan task:
User dapat memahami asal rule tanpa aplikasi mengklaim source yang tidak tersedia.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/components
- rules/ppki-ipb-2019/rules.json
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat component source reference dengan section, PDF page, printed page.
B. Bila PDF PPKI belum dibundel, tampilkan metadata saja dan jangan membuat link palsu.
C. Tambahkan glossary fix mode/severity.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Source null ditangani.
- Tidak menampilkan nomor halaman sebagai link bila asset tidak ada.
- Terminologi mengikuti katalog.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Source null ditangani.
- [ ] Tidak menampilkan nomor halaman sebagai link bila asset tidak ada.
- [ ] Terminologi mengikuti katalog.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S6-T08 — Accessibility, responsive, dan audit UI tests

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Audit UI memenuhi navigasi keyboard dasar dan mempunyai test komponen/E2E kritis.

### File/konteks minimum yang harus dibaca

- `apps/web/src`
- `apps/web/package.json`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S6-T08: Accessibility, responsive, dan audit UI tests.

Tujuan task:
Audit UI memenuhi navigasi keyboard dasar dan mempunyai test komponen/E2E kritis.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src
- apps/web/package.json

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan test stack frontend minimal yang sesuai Next.js project.
B. Test filter, detail drawer, progress complete/failed, manual decision.
C. Perbaiki responsive layout dan status yang tidak hanya warna.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No critical accessibility violation pada flow utama.
- Test tidak memerlukan production Supabase.
- Build tetap lolos.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build
- npm --prefix apps/web run test --if-present

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No critical accessibility violation pada flow utama.
- [ ] Test tidak memerlukan production Supabase.
- [ ] Build tetap lolos.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
  npm --prefix apps/web run test --if-present
```


---
