# Sprint 04 — DOCX parser v1 yang stabil dan dapat diuji

**Sprint goal:** Mengubah Open XML menjadi ParsedDocument internal yang cukup kaya untuk 30 rule MVP, tanpa bergantung pada layout Microsoft Word.

## Exit criteria

- [ ] Parser membaca section/page setup, effective formatting, heading/numbering, table/image/caption/field.
- [ ] Location anchor stabil dan tidak bergantung hanya pada nomor halaman.
- [ ] Diagnostic membedakan unsupported vs corrupt.
- [ ] Golden tests mencakup variasi direct formatting dan styles.
- [ ] Parser tidak menulis kembali file input.

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

## S4-T01 — Versioned ParsedDocument contract

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Definisikan model internal immutable yang cukup untuk validator MVP dan dapat berevolusi tanpa mengekspos Open XML type.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.DocxEngine/ParsedModels.cs`
- `backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs`
- `backend/src/Ppki.RuleEngine`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S4-T01: Versioned ParsedDocument contract.

Tujuan task:
Definisikan model internal immutable yang cukup untuk validator MVP dan dapat berevolusi tanpa mengekspos Open XML type.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.DocxEngine/ParsedModels.cs
- backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs
- backend/src/Ppki.RuleEngine

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan document metadata, section, paragraph/run, heading path, table, image, caption, field, dan diagnostics model.
B. Tambahkan stable IDs/indices serta schema version.
C. Update existing validators dan test agar memakai contract baru.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak ada `OpenXmlElement` keluar dari DocxEngine.
- Model tidak menyimpan full text di log; text tetap tersedia in-memory seperlunya.
- Serialization test contract tersedia.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak ada `OpenXmlElement` keluar dari DocxEngine.
- [ ] Model tidak menyimpan full text di log; text tetap tersedia in-memory seperlunya.
- [ ] Serialization test contract tersedia.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S4-T02 — Parse section, page setup, header/footer, dan page numbering

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Baca properti section yang diperlukan rule layout dan page numbering.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs`
- `backend/src/Ppki.DocxEngine/ParsedModels.cs`
- `backend/tests/fixtures/docx`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S4-T02: Parse section, page setup, header/footer, dan page numbering.

Tujuan task:
Baca properti section yang diperlukan rule layout dan page numbering.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs
- backend/src/Ppki.DocxEngine/ParsedModels.cs
- backend/tests/fixtures/docx

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Parse page size/orientation, margins, section break type, header/footer distance, mirror/odd-even settings.
B. Parse numbering format/restart dan field page number pada header/footer.
C. Tambahkan fixture multi-section dan golden assertions.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Missing property menghasilkan null/diagnostic, bukan nilai palsu.
- Landscape A4 dikenali.
- Input checksum tidak berubah.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Missing property menghasilkan null/diagnostic, bukan nilai palsu.
- [ ] Landscape A4 dikenali.
- [ ] Input checksum tidak berubah.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S4-T03 — Resolve effective paragraph dan run formatting

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Hitung formatting efektif dari docDefaults, basedOn styles, paragraph style, run style, dan direct formatting.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs`
- `backend/src/Ppki.DocxEngine/ParsedModels.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S4-T03: Resolve effective paragraph dan run formatting.

Tujuan task:
Hitung formatting efektif dari docDefaults, basedOn styles, paragraph style, run style, dan direct formatting.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs
- backend/src/Ppki.DocxEngine/ParsedModels.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Implement style inheritance dengan cycle protection.
B. Resolve font ASCII/highAnsi/eastAsia, size, bold, italic, underline, alignment, spacing, indent, keep/widow/page break.
C. Tambahkan fixtures untuk style inheritance dan mixed runs.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Direct formatting menang atas style.
- Unknown theme font didiagnosis, bukan diasumsikan.
- Mixed formatting direpresentasikan tanpa mengambil run pertama saja.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Direct formatting menang atas style.
- [ ] Unknown theme font didiagnosis, bukan diasumsikan.
- [ ] Mixed formatting direpresentasikan tanpa mengambil run pertama saja.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S4-T04 — Parse heading, outline level, dan numbering

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Deteksi heading berdasarkan style/outline/numbering secara deterministik dan bangun heading path.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs`
- `backend/src/Ppki.DocxEngine/ParsedModels.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S4-T04: Parse heading, outline level, dan numbering.

Tujuan task:
Deteksi heading berdasarkan style/outline/numbering secara deterministik dan bangun heading path.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs
- backend/src/Ppki.DocxEngine/ParsedModels.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Parse numbering definitions, abstract numbering, level text, start/restart.
B. Klasifikasikan heading level dan rendered number token tanpa mengubah teks.
C. Tambahkan fixture heading manual vs structured dan diagnostics confidence.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak semua paragraf bold dianggap heading.
- Heading path stabil untuk location.
- Ambiguous heading diberi confidence/diagnostic.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak semua paragraf bold dianggap heading.
- [ ] Heading path stabil untuk location.
- [ ] Ambiguous heading diberi confidence/diagnostic.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S4-T05 — Parse tables, images, captions, fields, dan TOC

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Ekstrak object yang diperlukan validator tabel/gambar/daftar isi MVP.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs`
- `backend/src/Ppki.DocxEngine/ParsedModels.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S4-T05: Parse tables, images, captions, fields, dan TOC.

Tujuan task:
Ekstrak object yang diperlukan validator tabel/gambar/daftar isi MVP.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs
- backend/src/Ppki.DocxEngine/ParsedModels.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Parse table rows/cells/borders/alignment/width serta paragraph relationship.
B. Parse drawing/image size/anchor/relationship dan caption candidates di sekitar object.
C. Parse field code untuk SEQ/TOC/REF dan hasil cached secara aman.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak mengekstrak binary image ke log.
- Caption candidate mempunyai before/after relation dan confidence.
- TOC field dibedakan dari daftar manual.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak mengekstrak binary image ke log.
- [ ] Caption candidate mempunyai before/after relation dan confidence.
- [ ] TOC field dibedakan dari daftar manual.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S4-T06 — Stable location anchors dan display labels

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Finding dapat menunjuk lokasi stabil tanpa mengandalkan page number yang belum dirender.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.DocxEngine/ParsedModels.cs`
- `backend/src/Ppki.RuleEngine/RuleContracts.cs`
- `backend/src/Ppki.RuleEngine/AuditRunner.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S4-T06: Stable location anchors dan display labels.

Tujuan task:
Finding dapat menunjuk lokasi stabil tanpa mengandalkan page number yang belum dirender.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.DocxEngine/ParsedModels.cs
- backend/src/Ppki.RuleEngine/RuleContracts.cs
- backend/src/Ppki.RuleEngine/AuditRunner.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Definisikan LocationAnchor typed: section, paragraph, run range, heading path, table/image/caption ID.
B. Tambahkan display label terlokalisasi dan optional estimated page.
C. Migrasikan validator existing dari anonymous object location.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- API tetap mengembalikan JSON object stabil.
- Anchor dapat dipakai fixer untuk menemukan element kembali.
- No full paragraph text di location payload.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] API tetap mengembalikan JSON object stabil.
- [ ] Anchor dapat dipakai fixer untuk menemukan element kembali.
- [ ] No full paragraph text di location payload.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S4-T07 — Parser safety limits dan diagnostics

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Parser menangani dokumen besar/aneh secara bounded dan menghasilkan diagnostic yang dapat ditindaklanjuti.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs`
- `backend/src/Ppki.DocxEngine/ParsedModels.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S4-T07: Parser safety limits dan diagnostics.

Tujuan task:
Parser menangani dokumen besar/aneh secara bounded dan menghasilkan diagnostic yang dapat ditindaklanjuti.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.DocxEngine/OpenXmlDocxParser.cs
- backend/src/Ppki.DocxEngine/ParsedModels.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan limit paragraph/run/table/image/field dan cancellation checks.
B. Bedakan corrupt package, encrypted/unsupported, missing main part, dan limit exceeded.
C. Tambahkan timing/metric tanpa menulis content.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak ada infinite style/numbering recursion.
- Temporary resource dibersihkan.
- Error user tidak memuat stack trace.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak ada infinite style/numbering recursion.
- [ ] Temporary resource dibersihkan.
- [ ] Error user tidak memuat stack trace.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S4-T08 — Golden parser matrix

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Buat corpus sintetis yang mewakili variasi DOCX utama dan snapshot expected model yang mudah direview.

### File/konteks minimum yang harus dibaca

- `backend/tests/fixtures/docx`
- `backend/tests`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S4-T08: Golden parser matrix.

Tujuan task:
Buat corpus sintetis yang mewakili variasi DOCX utama dan snapshot expected model yang mudah direview.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests/fixtures/docx
- backend/tests
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan fixtures: basic, multi-section, style inheritance, headings, tables/captions, fields/TOC, malformed.
B. Buat normalized snapshot agar timestamp/relationship IDs tidak membuat flaky.
C. Dokumentasikan cara menambah fixture baru.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Test deterministik lintas OS sejauh mungkin.
- Fixture original checksum diverifikasi.
- Snapshot tidak menyimpan data pribadi.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Test deterministik lintas OS sejauh mungkin.
- [ ] Fixture original checksum diverifikasi.
- [ ] Snapshot tidak menyimpan data pribadi.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---
