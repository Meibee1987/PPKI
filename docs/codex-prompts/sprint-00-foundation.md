# Sprint 00 — Fondasi repository dan baseline yang dapat direproduksi

**Sprint goal:** Membuktikan starter dapat dibangun, dikonfigurasi, dan diuji secara konsisten sebelum fitur MVP ditambah.

## Exit criteria

- [ ] `dotnet build backend/PpkiSmartFormatter.slnx` berhasil.
- [ ] `dotnet test backend/PpkiSmartFormatter.slnx` berhasil.
- [ ] `npm --prefix apps/web run typecheck` dan `build` berhasil.
- [ ] `docker compose config` valid dan environment penting tervalidasi fail-fast.
- [ ] CI menjalankan pemeriksaan backend, frontend, migration, dan kebocoran secret.

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

## S0-T01 — Audit struktur repository dan baseline build

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Pastikan struktur project konsisten dan semua baseline build/test dapat dijalankan tanpa mengubah perilaku produk.

### File/konteks minimum yang harus dibaca

- `AGENTS.md`
- `README.md`
- `compose.yaml`
- `backend/PpkiSmartFormatter.slnx`
- `apps/web/package.json`
- `.github/workflows/ci.yml`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S0-T01: Audit struktur repository dan baseline build.

Tujuan task:
Pastikan struktur project konsisten dan semua baseline build/test dapat dijalankan tanpa mengubah perilaku produk.

Baca hanya konteks minimum berikut terlebih dahulu:
- AGENTS.md
- README.md
- compose.yaml
- backend/PpkiSmartFormatter.slnx
- apps/web/package.json
- .github/workflows/ci.yml

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Inventarisasi project, script, Dockerfile, dan dependency; catat mismatch path atau command.
B. Perbaiki hanya error build/typecheck/test yang benar-benar menghalangi baseline.
C. Tambahkan `docs/BASELINE_VERIFICATION.md` berisi command dan expected result yang aktual.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Semua command baseline lolos atau kegagalan eksternal terdokumentasi jelas.
- Tidak ada fitur bisnis baru.
- Tidak ada secret atau environment lokal masuk Git.

Command verifikasi minimum:
- dotnet restore backend/PpkiSmartFormatter.slnx
- dotnet build backend/PpkiSmartFormatter.slnx --no-restore
- dotnet test backend/PpkiSmartFormatter.slnx --no-build
- npm --prefix apps/web ci
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build
- docker compose config

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Semua command baseline lolos atau kegagalan eksternal terdokumentasi jelas.
- [ ] Tidak ada fitur bisnis baru.
- [ ] Tidak ada secret atau environment lokal masuk Git.

### Command verifikasi

```bash
  dotnet restore backend/PpkiSmartFormatter.slnx
  dotnet build backend/PpkiSmartFormatter.slnx --no-restore
  dotnet test backend/PpkiSmartFormatter.slnx --no-build
  npm --prefix apps/web ci
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
  docker compose config
```


---

## S0-T02 — Validasi konfigurasi dan fail-fast startup

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Aplikasi harus gagal cepat dengan pesan aman ketika konfigurasi wajib Supabase belum diisi atau masih placeholder.

### File/konteks minimum yang harus dibaca

- `.env.example`
- `compose.yaml`
- `backend/services/Ppki.Api/Program.cs`
- `backend/services/Ppki.Worker/Program.cs`
- `backend/src/Ppki.Infrastructure/SupabaseOptions.cs`
- `apps/web/src/lib/supabase/server.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S0-T02: Validasi konfigurasi dan fail-fast startup.

Tujuan task:
Aplikasi harus gagal cepat dengan pesan aman ketika konfigurasi wajib Supabase belum diisi atau masih placeholder.

Baca hanya konteks minimum berikut terlebih dahulu:
- .env.example
- compose.yaml
- backend/services/Ppki.Api/Program.cs
- backend/services/Ppki.Worker/Program.cs
- backend/src/Ppki.Infrastructure/SupabaseOptions.cs
- apps/web/src/lib/supabase/server.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Definisikan daftar setting wajib untuk web, API, dan worker.
B. Implementasikan options validation/fail-fast tanpa menampilkan nilai secret.
C. Tambahkan unit test atau startup test untuk missing value dan placeholder value.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Pesan error menyebut nama variable, bukan nilainya.
- `NEXT_PUBLIC_*` tidak menerima secret key.
- Konfigurasi default non-secret tetap terdokumentasi di `.env.example`.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run typecheck
- docker compose config

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Pesan error menyebut nama variable, bukan nilainya.
- [ ] `NEXT_PUBLIC_*` tidak menerima secret key.
- [ ] Konfigurasi default non-secret tetap terdokumentasi di `.env.example`.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run typecheck
  docker compose config
```


---

## S0-T03 — Standardisasi local developer commands

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Sediakan command tunggal dan terdokumentasi untuk restore, verify, test, dan start tanpa menyembunyikan kegagalan.

### File/konteks minimum yang harus dibaca

- `package.json`
- `apps/web/package.json`
- `README.md`
- `scripts/bootstrap-supabase.ps1`
- `scripts/bootstrap-supabase.sh`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S0-T03: Standardisasi local developer commands.

Tujuan task:
Sediakan command tunggal dan terdokumentasi untuk restore, verify, test, dan start tanpa menyembunyikan kegagalan.

Baca hanya konteks minimum berikut terlebih dahulu:
- package.json
- apps/web/package.json
- README.md
- scripts/bootstrap-supabase.ps1
- scripts/bootstrap-supabase.sh

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan script root lint/typecheck/build/test yang memanggil command project yang benar.
B. Selaraskan script PowerShell dan shell untuk setup Supabase serta validasi prerequisite.
C. Perbarui README dengan jalur Windows dan macOS/Linux yang ringkas.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Script berhenti non-zero saat subcommand gagal.
- Script tidak menulis secret ke stdout.
- Tidak bergantung pada path absolut developer.

Command verifikasi minimum:
- npm run verify
- git diff --check

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Script berhenti non-zero saat subcommand gagal.
- [ ] Script tidak menulis secret ke stdout.
- [ ] Tidak bergantung pada path absolut developer.

### Command verifikasi

```bash
  npm run verify
  git diff --check
```


---

## S0-T04 — Perkuat CI baseline

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** CI harus menjadi gate minimal untuk backend, frontend, SQL migration, dan secret hygiene.

### File/konteks minimum yang harus dibaca

- `.github/workflows/ci.yml`
- `package.json`
- `apps/web/package.json`
- `backend/Directory.Packages.props`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S0-T04: Perkuat CI baseline.

Tujuan task:
CI harus menjadi gate minimal untuk backend, frontend, SQL migration, dan secret hygiene.

Baca hanya konteks minimum berikut terlebih dahulu:
- .github/workflows/ci.yml
- package.json
- apps/web/package.json
- backend/Directory.Packages.props

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Pisahkan job backend dan frontend serta aktifkan cache yang aman.
B. Tambahkan pemeriksaan `supabase/migrations/*.sql` dapat diparse/lint secara masuk akal tanpa kredensial hosted.
C. Tambahkan secret scan sederhana dan `git diff --check`/format check.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- CI tidak memerlukan Supabase secret.
- Semua job mempunyai timeout.
- Failure log tidak mencetak environment secret.

Command verifikasi minimum:
- git diff --check
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] CI tidak memerlukan Supabase secret.
- [ ] Semua job mempunyai timeout.
- [ ] Failure log tidak mencetak environment secret.

### Command verifikasi

```bash
  git diff --check
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run build
```


---

## S0-T05 — Buat struktur test corpus dan aturan fixture

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Siapkan struktur corpus yang aman untuk golden parser/fixer test tanpa memasukkan karya ilmiah nyata.

### File/konteks minimum yang harus dibaca

- `AGENTS.md`
- `backend/tests`
- `backend/src/Ppki.DocxEngine`
- `backend/src/Ppki.FixEngine`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S0-T05: Buat struktur test corpus dan aturan fixture.

Tujuan task:
Siapkan struktur corpus yang aman untuk golden parser/fixer test tanpa memasukkan karya ilmiah nyata.

Baca hanya konteks minimum berikut terlebih dahulu:
- AGENTS.md
- backend/tests
- backend/src/Ppki.DocxEngine
- backend/src/Ppki.FixEngine

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat folder `backend/tests/fixtures/docx` dan README aturan sanitasi fixture.
B. Tambahkan generator fixture kecil atau satu DOCX sintetis minimal yang dapat direproduksi.
C. Tambahkan helper checksum/copy untuk memastikan original fixture tidak dimutasi test.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Fixture tidak mengandung data pribadi.
- Test dapat dijalankan offline.
- Checksum original diverifikasi sebelum dan sesudah test.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- git diff --check

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Fixture tidak mengandung data pribadi.
- [ ] Test dapat dijalankan offline.
- [ ] Checksum original diverifikasi sebelum dan sesudah test.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  git diff --check
```


---

## S0-T06 — Tambahkan health dan diagnostics yang aman

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Health check harus membedakan liveness dan readiness tanpa mengungkap secret atau isi dokumen.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/services/Ppki.Worker/Program.cs`
- `compose.yaml`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S0-T06: Tambahkan health dan diagnostics yang aman.

Tujuan task:
Health check harus membedakan liveness dan readiness tanpa mengungkap secret atau isi dokumen.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/services/Ppki.Worker/Program.cs
- compose.yaml

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan endpoint liveness yang tidak bergantung eksternal.
B. Tambahkan readiness untuk koneksi database dan konfigurasi storage dengan timeout.
C. Selaraskan Docker healthcheck dan dokumentasi troubleshooting.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Readiness gagal saat database tidak dapat diakses.
- Response tidak menampilkan connection string atau key.
- Worker mempunyai startup log ringkas tanpa data dokumen.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- docker compose config

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Readiness gagal saat database tidak dapat diakses.
- [ ] Response tidak menampilkan connection string atau key.
- [ ] Worker mempunyai startup log ringkas tanpa data dokumen.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  docker compose config
```


---
