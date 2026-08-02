# Sprint 09 — Coverage minimal 30 rule PPKI IPB untuk Skripsi

**Sprint goal:** MVP mempunyai tepat terdokumentasi minimal 30 validator yang berguna, dengan test, confidence, applicability, dan safe fix classification.

## Exit criteria

- [ ] Minimal 30 rule implemented dan registered.
- [ ] Setiap validator mempunyai unit/golden test.
- [ ] Coverage report membedakan implemented/partial/manual.
- [ ] False positive mekanis dievaluasi pada corpus.
- [ ] Auto fix hanya tersedia untuk subset yang aman.

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

## S9-T01 — Rule coverage manifest dan quality gate

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Buat manifest satu-satunya untuk mapping rule code, validation key, fixer key, implementation status, version, dan test coverage.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Infrastructure/RuleCatalogImporter.cs`
- `rules/ppki-ipb-2019/rules.json`
- `backend/src/Ppki.RuleEngine`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S9-T01: Rule coverage manifest dan quality gate.

Tujuan task:
Buat manifest satu-satunya untuk mapping rule code, validation key, fixer key, implementation status, version, dan test coverage.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Infrastructure/RuleCatalogImporter.cs
- rules/ppki-ipb-2019/rules.json
- backend/src/Ppki.RuleEngine
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Refactor mapping existing 9 rules ke manifest compiled code.
B. Tambahkan startup/test gate duplicate/missing catalog code dan missing test metadata.
C. Generate `docs/RULE_COVERAGE_MVP.md`.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Katalog source tidak diubah hanya untuk status runtime.
- Manifest minimal 30 target code.
- Build gagal bila implemented mapping tidak valid.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Katalog source tidak diubah hanya untuk status runtime.
- [ ] Manifest minimal 30 target code.
- [ ] Build gagal bila implemented mapping tidak valid.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S9-T02 — Heading validators wave 1

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement validator: HDG-001, HDG-003, HDG-004, HDG-005, HDG-006.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine`
- `backend/src/Ppki.DocxEngine`
- `rules/ppki-ipb-2019/rules.json`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S9-T02: Heading validators wave 1.

Tujuan task:
Implement validator: HDG-001, HDG-003, HDG-004, HDG-005, HDG-006.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine
- backend/src/Ppki.DocxEngine
- rules/ppki-ipb-2019/rules.json

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Gunakan heading classification/numbering parsed model.
B. Implement actual/expected/location/confidence per rule, bukan satu finding ambigu.
C. Tambahkan tests roman number, uppercase exceptions, bold, period/underline, centered.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Rule applies_to dihormati.
- Text exception tidak memaksa false positive nama ilmiah secara buta.
- Fix eligibility sesuai safe/confirm policy.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Rule applies_to dihormati.
- [ ] Text exception tidak memaksa false positive nama ilmiah secara buta.
- [ ] Fix eligibility sesuai safe/confirm policy.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S9-T03 — Heading validators wave 2

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement validator: HDG-007, HDG-008, HDG-009, HDG-011, HDG-013.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine`
- `backend/src/Ppki.DocxEngine`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S9-T03: Heading validators wave 2.

Tujuan task:
Implement validator: HDG-007, HDG-008, HDG-009, HDG-011, HDG-013.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine
- backend/src/Ppki.DocxEngine

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Validasi numbering subbab/sub-subbab dan left alignment.
B. Implement title-case exceptions Indonesia secara bounded/configured.
C. Validasi bold/underline/period dan regular style level 3.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Ambiguous manual headings menghasilkan confidence rendah/ManualReview, bukan auto text fix.
- Unit tests Indonesian conjunction/preposition.
- Location heading path tersedia.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Ambiguous manual headings menghasilkan confidence rendah/ManualReview, bukan auto text fix.
- [ ] Unit tests Indonesian conjunction/preposition.
- [ ] Location heading path tersedia.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S9-T04 — Abstract validators untuk Skripsi

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement ABS-001, ABS-003, ABS-004, ABS-007, ABS-009.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine`
- `backend/src/Ppki.DocxEngine`
- `rules/ppki-ipb-2019/rules.json`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S9-T04: Abstract validators untuk Skripsi.

Tujuan task:
Implement ABS-001, ABS-003, ABS-004, ABS-007, ABS-009.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine
- backend/src/Ppki.DocxEngine
- rules/ppki-ipb-2019/rules.json

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Deteksi section Abstrak/Abstract dengan heading classifier dan boundaries.
B. Hitung paragraph/word, detect citation field/pattern secara conservative, parse keywords count/order.
C. Tambahkan bilingual fixtures dan edge cases.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Word count rule hanya narrative, bukan heading/keywords.
- Citation detection confidence/false positive documented.
- Tidak meringkas abstrak otomatis.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Word count rule hanya narrative, bukan heading/keywords.
- [ ] Citation detection confidence/false positive documented.
- [ ] Tidak meringkas abstrak otomatis.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S9-T05 — Structure validators MVP

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement STR-001, STR-021, STR-022 untuk urutan bagian awal, TOC coverage, dan kebutuhan daftar tambahan.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine`
- `backend/src/Ppki.DocxEngine`
- `rules/ppki-ipb-2019/rules.json`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S9-T05: Structure validators MVP.

Tujuan task:
Implement STR-001, STR-021, STR-022 untuk urutan bagian awal, TOC coverage, dan kebutuhan daftar tambahan.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine
- backend/src/Ppki.DocxEngine
- rules/ppki-ipb-2019/rules.json

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat section classifier untuk heading canonical/synonym yang terdokumentasi.
B. Bandingkan required sequence sesuai Skripsi dan TOC field vs actual headings.
C. Hitung table/figure/appendix untuk kebutuhan list ketika count > 1.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Confidence rendah bila heading tidak dapat diklasifikasi.
- Tidak memindahkan section otomatis pada MVP.
- Fixtures missing/out-of-order/manual TOC.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Confidence rendah bila heading tidak dapat diklasifikasi.
- [ ] Tidak memindahkan section otomatis pada MVP.
- [ ] Fixtures missing/out-of-order/manual TOC.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S9-T06 — Table/Figure caption validators MVP

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Implement TBL-012, FIG-003, FIG-007 untuk numbering/spacing dan posisi judul/caption.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine`
- `backend/src/Ppki.DocxEngine`
- `rules/ppki-ipb-2019/rules.json`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S9-T06: Table/Figure caption validators MVP.

Tujuan task:
Implement TBL-012, FIG-003, FIG-007 untuk numbering/spacing dan posisi judul/caption.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine
- backend/src/Ppki.DocxEngine
- rules/ppki-ipb-2019/rules.json

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Gunakan caption-object relationship dan SEQ field bila ada.
B. Validate title table di atas dan caption figure di bawah, number/spacing yang dapat diukur.
C. Tambahkan fixtures caption style, manual text, multi-line.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Ambiguous relationship tidak auto-fix.
- Caption actual/expected/location menunjuk object dan paragraph.
- No image binary logged.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Ambiguous relationship tidak auto-fix.
- [ ] Caption actual/expected/location menunjuk object dan paragraph.
- [ ] No image binary logged.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S9-T07 — Rule applicability, confidence, dan false-positive review

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Uji 30 rule pada corpus sintetis dan pilot sanitized untuk mengurangi false positive.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.RuleEngine`
- `backend/tests/fixtures/docx`
- `docs/RULE_COVERAGE_MVP.md`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S9-T07: Rule applicability, confidence, dan false-positive review.

Tujuan task:
Uji 30 rule pada corpus sintetis dan pilot sanitized untuk mengurangi false positive.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.RuleEngine
- backend/tests/fixtures/docx
- docs/RULE_COVERAGE_MVP.md

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan table-driven applicability tests per DocumentKind/section/object.
B. Definisikan confidence threshold dan policy ManualReview.
C. Catat known limitations per rule di coverage report.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Target false positive rule mekanis <5% pada corpus terlabel jika corpus cukup.
- Tidak mengubah official requirement.
- Rule partial tidak diklaim fully implemented.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Target false positive rule mekanis <5% pada corpus terlabel jika corpus cukup.
- [ ] Tidak mengubah official requirement.
- [ ] Rule partial tidak diklaim fully implemented.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S9-T08 — 30-rule end-to-end audit acceptance

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Buat satu DOCX acceptance sintetis dengan campuran compliant/non-compliant untuk membuktikan 30 rule tampil benar.

### File/konteks minimum yang harus dibaca

- `backend/tests`
- `apps/web`
- `docs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S9-T08: 30-rule end-to-end audit acceptance.

Tujuan task:
Buat satu DOCX acceptance sintetis dengan campuran compliant/non-compliant untuk membuktikan 30 rule tampil benar.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/tests
- apps/web
- docs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat expected findings manifest by rule code/location class.
B. Run parser+audit dan assert no unexpected implemented rule omission.
C. Dokumentasikan hasil/limitations dan screenshot checklist manual UI bila diperlukan.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Minimal 30 rule registered; test membuktikan implemented set.
- No reliance pada random page rendering.
- Original fixture immutable.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Minimal 30 rule registered; test membuktikan implemented set.
- [ ] No reliance pada random page rendering.
- [ ] Original fixture immutable.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run build
```


---
