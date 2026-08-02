# Sprint 07 — Fix plan, preview, konflik, dan explicit approval

**Sprint goal:** Tidak ada perubahan dokumen sebelum user memilih finding dan menyetujui fix plan yang dapat dijelaskan.

## Exit criteria

- [ ] FixPlan/FixPlanItem tersimpan dan terikat ke source DocumentVersion/AuditJob.
- [ ] Hanya finding eligible yang dapat dimasukkan.
- [ ] Preview before/after tersedia tanpa memodifikasi file.
- [ ] Conflict/dependency dideteksi.
- [ ] Approval immutable dan tercatat dalam audit trail.

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

## S7-T01 — Schema dan domain FixPlan/FixPlanItem

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Modelkan draft/approved/applying/completed/failed fix plan dan item per finding.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Domain/Enums.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S7-T01: Schema dan domain FixPlan/FixPlanItem.

Tujuan task:
Modelkan draft/approved/applying/completed/failed fix plan dan item per finding.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Domain/Enums.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan entity, enum state, FK source audit/version, owner/approver, timestamps.
B. Tambahkan unique constraint satu finding per plan dan immutable approved fields.
C. Buat migration dan EF mapping.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Plan tidak dapat mencampur findings dari audit/version berbeda.
- Approved plan tidak dapat diedit.
- Cascade behavior tidak menghapus audit historis.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npx supabase db lint

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Plan tidak dapat mencampur findings dari audit/version berbeda.
- [ ] Approved plan tidak dapat diedit.
- [ ] Cascade behavior tidak menghapus audit historis.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npx supabase db lint
```


---

## S7-T02 — Fix eligibility service

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Tentukan apakah finding Auto/Confirm/Manual/Report dapat dipilih berdasarkan fix mode, status, validator/fixer availability, dan confidence.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine/FixContracts.cs`
- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Application`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S7-T02: Fix eligibility service.

Tujuan task:
Tentukan apakah finding Auto/Confirm/Manual/Report dapat dipilih berdasarkan fix mode, status, validator/fixer availability, dan confidence.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine/FixContracts.cs
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Application

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat typed eligibility result dengan reason code.
B. Blok Manual/Report dan finding non-open; Confirm memerlukan explicit item approval.
C. Tambahkan unit tests matrix.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- UI tidak menjadi authority eligibility.
- Rule catalog Auto tanpa registered fixer tetap ineligible.
- Reason dapat ditampilkan user.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] UI tidak menjadi authority eligibility.
- [ ] Rule catalog Auto tanpa registered fixer tetap ineligible.
- [ ] Reason dapat ditampilkan user.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S7-T03 — Create/update draft fix plan API

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Owner dapat membuat draft dari selected findings dan mengubah selection sebelum approval.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Application/Contracts.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S7-T03: Create/update draft fix plan API.

Tujuan task:
Owner dapat membuat draft dari selected findings dan mengubah selection sebelum approval.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Application/Contracts.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement create/get/update/delete draft endpoints dengan ownership.
B. Validate audit completed, source version current/available, dan item eligibility.
C. Tambahkan idempotency dan ProblemDetails.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No duplicate item.
- Plan draft expired/stale dapat dideteksi.
- User B tidak dapat melihat/mengubah plan.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No duplicate item.
- [ ] Plan draft expired/stale dapat dideteksi.
- [ ] User B tidak dapat melihat/mengubah plan.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S7-T04 — Before/after preview contract

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Preview harus menjelaskan planned mutation tanpa menyentuh DOCX atau membuat version baru.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine`
- `backend/src/Ppki.Application/Contracts.cs`
- `backend/services/Ppki.Api/Program.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S7-T04: Before/after preview contract.

Tujuan task:
Preview harus menjelaskan planned mutation tanpa menyentuh DOCX atau membuat version baru.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine
- backend/src/Ppki.Application/Contracts.cs
- backend/services/Ppki.Api/Program.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Definisikan fixer preview interface dan normalized before/after payload.
B. Implement preview untuk page size, margins, font/size, alignment, spacing, indent existing fix targets.
C. Expose preview summary endpoint per plan.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Preview deterministic.
- Tidak mengandung full paragraph text.
- Preview failure tidak mengubah plan/source file.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Preview deterministic.
- [ ] Tidak mengandung full paragraph text.
- [ ] Preview failure tidak mengubah plan/source file.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S7-T05 — Conflict dan dependency detection

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Cegah dua item merencanakan mutation bertentangan pada property/location yang sama.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine`
- `backend/src/Ppki.DocxEngine/ParsedModels.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S7-T05: Conflict dan dependency detection.

Tujuan task:
Cegah dua item merencanakan mutation bertentangan pada property/location yang sama.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine
- backend/src/Ppki.DocxEngine/ParsedModels.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Definisikan mutation key dan ordering/dependency model.
B. Detect conflict duplicate/contradictory, stale anchor, dan fixer dependency.
C. Tambahkan unit tests kombinasi layout/paragraph/heading.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Conflict menghasilkan actionable reason.
- Safe independent fixes tetap batchable.
- No implicit last-write-wins.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Conflict menghasilkan actionable reason.
- [ ] Safe independent fixes tetap batchable.
- [ ] No implicit last-write-wins.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S7-T06 — Explicit approval dan immutable snapshot

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Approval menyimpan siapa, kapan, source checksum, plan hash, dan exact item preview.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Application`
- `backend/src/Ppki.Domain/Entities.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S7-T06: Explicit approval dan immutable snapshot.

Tujuan task:
Approval menyimpan siapa, kapan, source checksum, plan hash, dan exact item preview.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Application
- backend/src/Ppki.Domain/Entities.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement approve endpoint transactional dengan revalidation eligibility/staleness.
B. Hitung canonical plan hash dan freeze snapshot.
C. Queue apply job hanya setelah commit approval.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Double approval idempotent.
- Source checksum mismatch membuat plan stale.
- Audit trail mencatat approval.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Double approval idempotent.
- [ ] Source checksum mismatch membuat plan stale.
- [ ] Audit trail mencatat approval.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S7-T07 — Frontend fix selection dan confirmation

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** User dapat memilih safe fixes, melihat preview, memahami Manual/Report, lalu approve.

### File/konteks minimum yang harus dibaca

- `apps/web/src/app/documents/[id]/page.tsx`
- `apps/web/src/components`
- `apps/web/src/lib/api.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S7-T07: Frontend fix selection dan confirmation.

Tujuan task:
User dapat memilih safe fixes, melihat preview, memahami Manual/Report, lalu approve.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/app/documents/[id]/page.tsx
- apps/web/src/components
- apps/web/src/lib/api.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan checkbox selection dengan Fix All Safe yang hanya eligible.
B. Buat fix plan review page/drawer grouped by domain/location.
C. Tambahkan confirmation dengan total perubahan, conflicts, dan explicit approve button.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak ada auto-apply saat checkbox dipilih.
- Confirm items dibedakan jelas dari Auto.
- Stale plan memaksa refresh/review ulang.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak ada auto-apply saat checkbox dipilih.
- [ ] Confirm items dibedakan jelas dari Auto.
- [ ] Stale plan memaksa refresh/review ulang.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S7-T08 — Fix plan integration dan security tests

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Buktikan plan hanya dapat dibentuk dari finding valid milik owner dan approval tidak memodifikasi DOCX.

### File/konteks minimum yang harus dibaca

- `backend/tests`
- `apps/web`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S7-T08: Fix plan integration dan security tests.

Tujuan task:
Buktikan plan hanya dapat dibentuk dari finding valid milik owner dan approval tidak memodifikasi DOCX.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests
- apps/web

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Test mixed audit rejection, cross-user access, ineligible Manual/Report, stale checksum.
B. Test preview deterministic dan source checksum unchanged.
C. Test approval queues tepat satu apply job.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Original object storage byte unchanged.
- Tidak ada apply sebelum approval.
- Cleanup test aman.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Original object storage byte unchanged.
- [ ] Tidak ada apply sebelum approval.
- [ ] Cleanup test aman.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---
