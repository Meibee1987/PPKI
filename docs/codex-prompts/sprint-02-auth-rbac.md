# Sprint 02 — Supabase Auth, session SSR, RBAC, dan ownership

**Sprint goal:** Membuat identitas user tervalidasi server-side dan seluruh endpoint bisnis memakai authorization yang konsisten.

## Exit criteria

- [ ] Login/signup/logout/callback stabil pada Next.js SSR.
- [ ] ASP.NET memverifikasi JWT/user tanpa mempercayai data browser.
- [ ] Ownership document diterapkan melalui service/policy terpusat.
- [ ] Role Student/Reviewer/PPKIAdmin/UnitAdmin mempunyai policy eksplisit.
- [ ] Test lintas user membuktikan tidak ada IDOR.

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

## S2-T01 — Perkuat Supabase Auth SSR di Next.js

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Session cookie harus di-refresh dengan benar dan route terlindungi tidak bergantung pada local storage.

### File/konteks minimum yang harus dibaca

- `apps/web/src/lib/supabase`
- `apps/web/src/proxy.ts`
- `apps/web/src/app/login/page.tsx`
- `apps/web/src/app/signup/page.tsx`
- `apps/web/src/app/auth/callback/route.ts`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S2-T01: Perkuat Supabase Auth SSR di Next.js.

Tujuan task:
Session cookie harus di-refresh dengan benar dan route terlindungi tidak bergantung pada local storage.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/lib/supabase
- apps/web/src/proxy.ts
- apps/web/src/app/login/page.tsx
- apps/web/src/app/signup/page.tsx
- apps/web/src/app/auth/callback/route.ts

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Audit pemakaian browser/server client dan cookie propagation.
B. Tambahkan guard untuk route document serta redirect yang aman.
C. Tangani expired session, callback error, dan email confirmation state dengan UX jelas.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak ada secret key di bundle browser.
- Open redirect dicegah.
- Expired session kembali ke login tanpa loop.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak ada secret key di bundle browser.
- [ ] Open redirect dicegah.
- [ ] Expired session kembali ke login tanpa loop.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---

## S2-T02 — Perkuat validasi token di ASP.NET API

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** API harus mengautentikasi bearer token secara aman, timeout-aware, dan tidak melakukan call berlebihan yang tidak perlu.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/SupabaseAuthentication.cs`
- `backend/services/Ppki.Api/Program.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S2-T02: Perkuat validasi token di ASP.NET API.

Tujuan task:
API harus mengautentikasi bearer token secara aman, timeout-aware, dan tidak melakukan call berlebihan yang tidak perlu.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/SupabaseAuthentication.cs
- backend/services/Ppki.Api/Program.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Audit implementasi saat ini dan pilih strategi validation yang sesuai konfigurasi Supabase project.
B. Tambahkan timeout, cancellation, cache metadata/user validation yang aman bila relevan, dan sanitized error.
C. Tambahkan test token missing, malformed, expired, dan user disabled.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- 401 dan 403 dibedakan benar.
- Token/response body Supabase tidak dicetak ke log.
- NameIdentifier selalu berasal dari `sub` tervalidasi.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- dotnet build backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] 401 dan 403 dibedakan benar.
- [ ] Token/response body Supabase tidak dicetak ke log.
- [ ] NameIdentifier selalu berasal dari `sub` tervalidasi.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  dotnet build backend/PpkiSmartFormatter.slnx
```


---

## S2-T03 — Typed role dan profile synchronization

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Role tidak lagi berupa string bebas di business logic dan profile user tetap sinkron dengan auth user.

### File/konteks minimum yang harus dibaca

- `backend/src/Ppki.Domain/Entities.cs`
- `backend/src/Ppki.Domain/Enums.cs`
- `backend/services/Ppki.Api/Program.cs`
- `supabase/migrations`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S2-T03: Typed role dan profile synchronization.

Tujuan task:
Role tidak lagi berupa string bebas di business logic dan profile user tetap sinkron dengan auth user.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/src/Ppki.Domain/Entities.cs
- backend/src/Ppki.Domain/Enums.cs
- backend/services/Ppki.Api/Program.cs
- supabase/migrations

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Tambahkan enum/value object role dan mapping EF.
B. Buat service `CurrentUser`/profile resolver; hilangkan duplikasi `EnsureProfileAsync` bila perlu.
C. Tambahkan migration/check constraint dan test sinkronisasi metadata minimal.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Role invalid ditolak.
- User baru mempunyai Student default.
- Email/full name update tidak mengubah role.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npx supabase db lint

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Role invalid ditolak.
- [ ] User baru mempunyai Student default.
- [ ] Email/full name update tidak mengubah role.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npx supabase db lint
```


---

## S2-T04 — Centralize document ownership authorization

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Hilangkan query ownership ad hoc dan sediakan policy/service reusable untuk document, version, audit, finding, dan download.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Application`
- `backend/src/Ppki.Infrastructure/PpkiDbContext.cs`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S2-T04: Centralize document ownership authorization.

Tujuan task:
Hilangkan query ownership ad hoc dan sediakan policy/service reusable untuk document, version, audit, finding, dan download.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Application
- backend/src/Ppki.Infrastructure/PpkiDbContext.cs

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat service authorization/query scope berdasarkan current user.
B. Refactor endpoint existing untuk menggunakan service tersebut.
C. Tambahkan test IDOR dengan dua user pada setiap resource type.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Resource lintas user menghasilkan 404 atau 403 sesuai kebijakan konsisten.
- Tidak ada endpoint yang mengambil entity berdasarkan ID tanpa ownership scope.
- Query tetap no-tracking untuk read path.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Resource lintas user menghasilkan 404 atau 403 sesuai kebijakan konsisten.
- [ ] Tidak ada endpoint yang mengambil entity berdasarkan ID tanpa ownership scope.
- [ ] Query tetap no-tracking untuk read path.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
```


---

## S2-T05 — Tambahkan authorization policy role untuk reviewer/admin

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Siapkan policy eksplisit untuk fitur reviewer/admin berikutnya tanpa memberi akses data sebelum sharing dibuat.

### File/konteks minimum yang harus dibaca

- `backend/services/Ppki.Api/Program.cs`
- `backend/src/Ppki.Domain/Enums.cs`
- `docs/architecture.md`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S2-T05: Tambahkan authorization policy role untuk reviewer/admin.

Tujuan task:
Siapkan policy eksplisit untuk fitur reviewer/admin berikutnya tanpa memberi akses data sebelum sharing dibuat.

Baca hanya konteks minimum berikut terlebih dahulu:
- backend/services/Ppki.Api/Program.cs
- backend/src/Ppki.Domain/Enums.cs
- docs/architecture.md

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Daftarkan policy Student, Reviewer, PPKIAdmin, UnitAdmin.
B. Tambahkan endpoint `/api/me` untuk identity/role yang aman.
C. Tambahkan test policy dan dokumentasi matriks akses.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Reviewer tidak otomatis dapat membaca semua dokumen.
- Admin endpoint placeholder tetap tertutup.
- Frontend dapat membaca role melalui API tanpa membaca service key.

Command verifikasi minimum:
- dotnet test backend/PpkiSmartFormatter.slnx
- npm --prefix apps/web run typecheck

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Reviewer tidak otomatis dapat membaca semua dokumen.
- [ ] Admin endpoint placeholder tetap tertutup.
- [ ] Frontend dapat membaca role melalui API tanpa membaca service key.

### Command verifikasi

```bash
  dotnet test backend/PpkiSmartFormatter.slnx
  npm --prefix apps/web run typecheck
```


---

## S2-T06 — Auth UI states dan E2E smoke test

**Dependency:** selesaikan task sebelumnya dalam sprint ini, kecuali task menyatakan dapat paralel.  
**Tujuan:** Lengkapi loading/error/empty state autentikasi dan smoke test alur login ke dashboard.

### File/konteks minimum yang harus dibaca

- `apps/web/src/app`
- `apps/web/src/components`
- `apps/web/src/lib/api.ts`
- `backend/tests`

### Prompt untuk Codex

```text
Anda bekerja pada repository PPKI IPB Smart Formatter — Supabase edition.
Kerjakan hanya task S2-T06: Auth UI states dan E2E smoke test.

Tujuan task:
Lengkapi loading/error/empty state autentikasi dan smoke test alur login ke dashboard.

Baca hanya konteks minimum berikut terlebih dahulu:
- apps/web/src/app
- apps/web/src/components
- apps/web/src/lib/api.ts
- backend/tests

Bagi implementasi menjadi bagian berikut dan selesaikan berurutan:
A. Buat reusable auth form errors dan pending state.
B. Tambahkan logout yang membersihkan session dan cache user.
C. Tambahkan E2E atau integration smoke test yang dapat dikonfigurasi untuk project test.

Batasan:
- Ikuti AGENTS.md.
- Jangan melanjutkan ke task lain.
- Jangan membuat perubahan di luar scope kecuali dibutuhkan agar build/test task ini lulus; jelaskan setiap perubahan tambahan.
- Jangan mencetak atau commit secret Supabase, token, connection string, signed URL, atau isi karya ilmiah.
- Pertahankan original DOCX immutable.
- Tambahkan test yang relevan dan jalankan command verifikasi yang tersedia.

Acceptance criteria:
- Tidak ada double submit.
- Error backend tidak menampilkan detail sensitif.
- Protected page tidak flash data sebelum auth resolved.

Command verifikasi minimum:
- npm --prefix apps/web run typecheck
- npm --prefix apps/web run build

Pada jawaban akhir, tampilkan ringkasan, file diubah, migration/API contract berubah, test/command dan hasil, serta risiko/verifikasi manual tersisa. Jangan klaim sukses bila command tidak dijalankan atau gagal.
```

### Checklist reviewer

- [ ] Tidak ada double submit.
- [ ] Error backend tidak menampilkan detail sensitif.
- [ ] Protected page tidak flash data sebelum auth resolved.

### Command verifikasi

```bash
  npm --prefix apps/web run typecheck
  npm --prefix apps/web run build
```


---
