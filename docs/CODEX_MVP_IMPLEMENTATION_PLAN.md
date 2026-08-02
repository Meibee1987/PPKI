# Rencana Sprint dan Prompt Codex sampai MVP — Supabase Edition

Dokumen ini adalah indeks implementasi untuk repository `ppki-smart-formatter-supabase-starter`. Setiap task dirancang untuk **satu sesi Codex/agent** agar konteks tidak terlalu besar.

## Cara penggunaan

1. Buat branch per task, misalnya `feat/S3-T02-docx-validation`.
2. Buka file sprint terkait di `docs/codex-prompts/`.
3. Salin hanya blok **Prompt untuk Codex** pada satu task.
4. Setelah agent selesai, review diff dan jalankan command verifikasi sendiri.
5. Merge hanya bila checklist task selesai; lalu mulai chat/agent baru untuk task berikutnya.
6. Jangan menggabungkan dua task besar dalam satu prompt.

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

## Scope MVP yang dikunci

- Institusi: IPB.
- Pedoman: PPKI IPB Edisi Ke-4 (2019).
- Jenis dokumen pilot: Skripsi.
- Input utama: DOCX.
- Platform: Next.js + ASP.NET Core + .NET Worker + Supabase Postgres/Auth/Storage + Open XML SDK.
- Rule target: minimal 30 validator deterministik; katalog 317 rule tetap source data.
- Flow: upload original → audit log → pilih/preview/approve fix → version baru → re-audit → export → reviewer approval.
- Di luar MVP: universitas lain, override dosen tanpa evidence/governance, generative rewriting, plagiarism checker, kolaborasi real-time.

## Indeks sprint

- [Sprint 00 — Fondasi repository dan baseline yang dapat direproduksi](codex-prompts/sprint-00-foundation.md) — 6 task
- [Sprint 01 — Schema Supabase, RLS, integritas data, dan audit trail](codex-prompts/sprint-01-supabase-data-security.md) — 6 task
- [Sprint 02 — Supabase Auth, session SSR, RBAC, dan ownership](codex-prompts/sprint-02-auth-rbac.md) — 6 task
- [Sprint 03 — Upload DOCX aman dan document versioning immutable](codex-prompts/sprint-03-document-upload-versioning.md) — 8 task
- [Sprint 04 — DOCX parser v1 yang stabil dan dapat diuji](codex-prompts/sprint-04-docx-parser.md) — 8 task
- [Sprint 05 — Rule resolution, audit worker, scoring, dan findings persistence](codex-prompts/sprint-05-audit-engine.md) — 8 task
- [Sprint 06 — Audit progress, summary, finding log, dan keputusan manual](codex-prompts/sprint-06-audit-ui.md) — 8 task
- [Sprint 07 — Fix plan, preview, konflik, dan explicit approval](codex-prompts/sprint-07-fix-plan-approval.md) — 8 task
- [Sprint 08 — Safe fix engine, DocumentVersion baru, dan re-audit otomatis](codex-prompts/sprint-08-fix-engine-reaudit.md) — 10 task
- [Sprint 09 — Coverage minimal 30 rule PPKI IPB untuk Skripsi](codex-prompts/sprint-09-rule-coverage-30.md) — 8 task
- [Sprint 10 — Version history, export DOCX, dan laporan audit JSON/PDF](codex-prompts/sprint-10-export-history.md) — 8 task
- [Sprint 11 — Reviewer workflow, hardening, pilot, dan rilis MVP](codex-prompts/sprint-11-review-release.md) — 9 task

## Definition of Done MVP

- [ ] Signup/login/logout dan protected routes bekerja.
- [ ] Ownership dan RLS mencegah akses lintas user.
- [ ] DOCX valid dapat di-upload; original immutable dan checksum terverifikasi.
- [ ] Audit asynchronous reproducible dengan profile version dan resolved rule-set hash.
- [ ] Minimal 30 rule PPKI implemented dengan tests dan coverage report.
- [ ] Finding memuat source, actual, expected, location, severity, fix mode, confidence/status.
- [ ] User melihat audit log sebelum perubahan dan dapat memberi keputusan manual/ignore dengan alasan.
- [ ] Fix plan mempunyai preview, conflict detection, dan explicit approval.
- [ ] Fix membuat DocumentVersion baru; original tidak berubah; output lolos reparse.
- [ ] Re-audit otomatis merekonsiliasi finding.
- [ ] Version history dan export DOCX/JSON/PDF private tersedia dengan checksum/signed URL.
- [ ] Reviewer hanya dapat melihat shared exact version dan memberi Approved/Changes Requested.
- [ ] Security, observability, performance pilot, migration, rollback, dan release checklist tersedia.
- [ ] Tidak ada bug P0/P1 terbuka saat rilis pilot.
