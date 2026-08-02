# Sprint 08 — Safe fix engine, DocumentVersion baru, dan re-audit otomatis

**Sprint goal:** Menerapkan hanya approved safe mutations pada working copy, memvalidasi output, membuat version baru, dan mengaudit ulang.

## Exit criteria

- [ ] Original DOCX tidak berubah.
- [ ] Fixer registry deterministic dan hanya menjalankan approved item.
- [ ] Output dapat dibuka/parse kembali.
- [ ] Change log before/after tersimpan.
- [ ] Re-audit otomatis menunjukkan finding fixed atau masih failing.

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

## S8-T01 — Fixer registry dan apply job lifecycle

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Buat arsitektur apply job terpisah dari audit job dan registry fixer berdasarkan FixKey/ValidationKey.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine/FixContracts.cs`
- `backend/services/Ppki.Worker`
- `backend/src/Ppki.Domain/Entities.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T01: Fixer registry dan apply job lifecycle.

Tujuan task:
Buat arsitektur apply job terpisah dari audit job dan registry fixer berdasarkan FixKey/ValidationKey.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine/FixContracts.cs
- backend/services/Ppki.Worker
- backend/src/Ppki.Domain/Entities.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan FixJob entity/status/attempt/lease dan worker claim atomic.
B. Definisikan `IRuleFixer` preview/apply contract dan registry startup validation.
C. Tambahkan failure/retry policy idempotent.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Approved plan tepat satu active FixJob.
- Unknown fixer menggagalkan sebelum output upload.
- Log tidak memuat content.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Approved plan tepat satu active FixJob.
- [ ] Unknown fixer menggagalkan sebelum output upload.
- [ ] Log tidak memuat content.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S8-T02 — Working copy materialization dan package safety

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Download source ke temporary working copy yang terisolasi dan selalu dibersihkan.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs`
- `backend/src/Ppki.FixEngine`
- `backend/services/Ppki.Worker`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T02: Working copy materialization dan package safety.

Tujuan task:
Download source ke temporary working copy yang terisolasi dan selalu dibersihkan.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs
- backend/src/Ppki.FixEngine
- backend/services/Ppki.Worker

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat temp workspace abstraction dengan random directory dan restrictive access bila platform mendukung.
B. Verify source checksum sebelum apply dan clone byte-for-byte.
C. Pastikan cleanup pada success/failure/cancellation.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Source object tidak pernah dibuka write.
- Checksum clone sama sebelum mutation.
- Temp filename tidak berasal langsung dari user input.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Source object tidak pernah dibuka write.
- [ ] Checksum clone sama sebelum mutation.
- [ ] Temp filename tidak berasal langsung dari user input.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S8-T03 — Page size dan margin fixers

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement fixer untuk PPKI-LAY-003 dan PPKI-LAY-008..011 pada section anchor yang approved.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine`
- `backend/src/Ppki.DocxEngine/ParsedModels.cs`
- `backend/tests/fixtures/docx`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T03: Page size dan margin fixers.

Tujuan task:
Implement fixer untuk PPKI-LAY-003 dan PPKI-LAY-008..011 pada section anchor yang approved.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine
- backend/src/Ppki.DocxEngine/ParsedModels.cs
- backend/tests/fixtures/docx

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Gunakan Open XML units/twips converter teruji.
B. Apply hanya section targeted dan preserve orientation exception sesuai preview.
C. Tambahkan golden before/after parser assertions.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- A4 portrait/landscape ditangani.
- Section non-target tidak berubah.
- Reparse menghasilkan expected values.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] A4 portrait/landscape ditangani.
- [ ] Section non-target tidak berubah.
- [ ] Reparse menghasilkan expected values.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S8-T04 — Body font dan size fixer

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement fixer PPKI-LAY-005 tanpa merusak heading, table special blocks, atau mixed semantic runs yang tidak eligible.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine`
- `backend/src/Ppki.DocxEngine`
- `backend/tests/fixtures/docx`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T04: Body font dan size fixer.

Tujuan task:
Implement fixer PPKI-LAY-005 tanpa merusak heading, table special blocks, atau mixed semantic runs yang tidak eligible.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine
- backend/src/Ppki.DocxEngine
- backend/tests/fixtures/docx

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tentukan mutation strategy style-first vs direct formatting dan documentasikan tradeoff.
B. Apply pada anchors yang dihasilkan finding/plan saja.
C. Preserve bold/italic/superscript/subscript dan non-Latin font attributes.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak mengubah text.
- Run special field/citation tidak rusak.
- Golden reparse lulus.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak mengubah text.
- [ ] Run special field/citation tidak rusak.
- [ ] Golden reparse lulus.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S8-T05 — Paragraph spacing, indent, dan alignment fixers

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement PPKI-LAY-017, 018, 019 pada body paragraph eligible.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine`
- `backend/src/Ppki.DocxEngine`
- `backend/tests/fixtures/docx`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T05: Paragraph spacing, indent, dan alignment fixers.

Tujuan task:
Implement PPKI-LAY-017, 018, 019 pada body paragraph eligible.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine
- backend/src/Ppki.DocxEngine
- backend/tests/fixtures/docx

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Set line spacing single, first-line indent 1 cm, dan justified pada target anchor.
B. Preserve left/right indent/list/hanging properties yang tidak diminta.
C. Tambahkan golden tests paragraph in/out table/list/heading.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- No change pada headings/tables yang excluded.
- No text mutation.
- Before/after change log akurat.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] No change pada headings/tables yang excluded.
- [ ] No text mutation.
- [ ] Before/after change log akurat.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S8-T06 — Safe heading fixers MVP

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement subset heading fixer yang deterministik dan sudah mempunyai stable heading anchor.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine`
- `backend/src/Ppki.DocxEngine`
- `backend/src/Ppki.RuleEngine`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T06: Safe heading fixers MVP.

Tujuan task:
Implement subset heading fixer yang deterministik dan sudah mempunyai stable heading anchor.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine
- backend/src/Ppki.DocxEngine
- backend/src/Ppki.RuleEngine

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Mulai dari font size/bold/alignment/punctuation/decorations; jangan auto-renumber ambigu.
B. Require Confirm untuk perubahan text capitalization/punctuation bila preview mengubah text.
C. Tambahkan golden tests dan fixer eligibility.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak mengubah heading classification secara diam-diam.
- Auto hanya format non-semantic.
- Text-changing fix selalu explicit Confirm.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak mengubah heading classification secara diam-diam.
- [ ] Auto hanya format non-semantic.
- [ ] Text-changing fix selalu explicit Confirm.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S8-T07 — Output validation, upload, dan DocumentVersion baru

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Sebelum publish, output harus lolos package open/reparse dan metadata version dibuat transactional.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.FixEngine`
- `backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs`
- `backend/src/Ppki.Domain/Entities.cs`
- `backend/services/Ppki.Worker`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T07: Output validation, upload, dan DocumentVersion baru.

Tujuan task:
Sebelum publish, output harus lolos package open/reparse dan metadata version dibuat transactional.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.FixEngine
- backend/src/Ppki.Infrastructure/SupabaseFileStorage.cs
- backend/src/Ppki.Domain/Entities.cs
- backend/services/Ppki.Worker

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Open package read-only, parse ulang, verify no missing main parts, hitung checksum/size.
B. Upload ke `documents-versions` key baru lalu finalize DocumentVersion parent/source relation.
C. Implement compensation/status untuk upload atau DB failure.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Original/current old version tetap tersedia.
- CurrentVersionNo hanya naik setelah output valid.
- Duplicate retry tidak membuat dua version.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Original/current old version tetap tersedia.
- [ ] CurrentVersionNo hanya naik setelah output valid.
- [ ] Duplicate retry tidak membuat dua version.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S8-T08 — Persist FixAction change log dan item outcomes

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Simpan outcome per approved item: applied/skipped/failed, before/after, anchor, fixer version.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`
- `supabase/migrations`
- `backend/src/Ppki.FixEngine`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T08: Persist FixAction change log dan item outcomes.

Tujuan task:
Simpan outcome per approved item: applied/skipped/failed, before/after, anchor, fixer version.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs
- supabase/migrations
- backend/src/Ppki.FixEngine

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambah entity/table FixAction/FixItemResult.
B. Persist bounded JSON payload dan error reason aman.
C. Update plan/job aggregate status dari item outcomes.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Change log append-only.
- No full paragraph text.
- Partial failure policy eksplisit.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npx supabase db lint

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Change log append-only.
- [ ] No full paragraph text.
- [ ] Partial failure policy eksplisit.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npx supabase db lint
```


---

## S8-T09 — Automatic re-audit dan finding reconciliation

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Setelah version baru sukses, queue audit baru dengan profile snapshot yang tepat dan hubungkan hasil ke fix plan.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Worker`
- `backend/src/Ppki.RuleEngine/AuditRunner.cs`
- `backend/src/Ppki.Domain/Entities.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T09: Automatic re-audit dan finding reconciliation.

Tujuan task:
Setelah version baru sukses, queue audit baru dengan profile snapshot yang tepat dan hubungkan hasil ke fix plan.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Worker
- backend/src/Ppki.RuleEngine/AuditRunner.cs
- backend/src/Ppki.Domain/Entities.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Queue re-audit idempotent setelah commit version.
B. Reconcile source finding menjadi Fixed/StillFailing/PartiallyFixed berdasarkan rule/location outcome.
C. Expose status chain pada API.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Re-audit memakai new DocumentVersion.
- Audit source historis immutable.
- No false Fixed tanpa audit result.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Re-audit memakai new DocumentVersion.
- [ ] Audit source historis immutable.
- [ ] No false Fixed tanpa audit result.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S8-T10 — Golden fixer dan corruption regression matrix

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Buktikan fix engine tidak merusak file dan hanya mengubah expected properties.

### File/konteks minimum yang harus dibaca

- `backend/tests/fixtures/docx`
- `backend/tests`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S8-T10: Golden fixer dan corruption regression matrix.

Tujuan task:
Buktikan fix engine tidak merusak file dan hanya mengubah expected properties.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests/fixtures/docx
- backend/tests

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan fixtures multi-section, mixed styles, lists, tables, fields, headers.
B. Compare normalized parsed before/after dan checksum original.
C. Test cancellation/failure/duplicate retry.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Output dapat dibuka Open XML tanpa repair.
- Original byte identical.
- Test deterministic.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Output dapat dibuka Open XML tanpa repair.
- [ ] Original byte identical.
- [ ] Test deterministic.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---
