# Codex prompts per sprint

Gunakan **satu task per sesi agent**. Jangan meminta agent mengerjakan satu sprint penuh sekaligus.

## Urutan

- [00 — Fondasi repository dan baseline yang dapat direproduksi](sprint-00-foundation.md)
- [01 — Schema Supabase, RLS, integritas data, dan audit trail](sprint-01-supabase-data-security.md)
- [02 — Supabase Auth, session SSR, RBAC, dan ownership](sprint-02-auth-rbac.md)
- [03 — Upload DOCX aman dan document versioning immutable](sprint-03-document-upload-versioning.md)
- [04 — DOCX parser v1 yang stabil dan dapat diuji](sprint-04-docx-parser.md)
- [05 — Rule resolution, audit worker, scoring, dan findings persistence](sprint-05-audit-engine.md)
- [06 — Audit progress, summary, finding log, dan keputusan manual](sprint-06-audit-ui.md)
- [07 — Fix plan, preview, konflik, dan explicit approval](sprint-07-fix-plan-approval.md)
- [08 — Safe fix engine, DocumentVersion baru, dan re-audit otomatis](sprint-08-fix-engine-reaudit.md)
- [09 — Coverage minimal 30 rule PPKI IPB untuk Skripsi](sprint-09-rule-coverage-30.md)
- [10 — Version history, export DOCX, dan laporan audit JSON/PDF](sprint-10-export-history.md)
- [11 — Reviewer workflow, hardening, pilot, dan rilis MVP](sprint-11-review-release.md)

## Guardrail singkat

- Baca `AGENTS.md`.
- Original DOCX immutable.
- Secret Supabase tidak boleh masuk log/Git/browser.
- Rule mekanis deterministik; no generative AI.
- Migration additive.
- Parser/fixer change wajib golden DOCX test.
- Final response agent harus jujur tentang test yang tidak dijalankan.
