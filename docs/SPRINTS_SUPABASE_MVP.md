# MVP sprints — Supabase baseline

## Sprint 0 — Hosted foundation
- SB-S0-01 Create Supabase project and Auth URLs.
- SB-S0-02 Apply SQL migration and create private buckets.
- SB-S0-03 Fill `.env`; verify API, worker, and web containers.
- SB-S0-04 Register/login and verify `user_profiles` trigger.
- SB-S0-05 Upload one DOCX and verify Storage + metadata.

## Sprint 1 — Secure upload/versioning
- Enforce ownership on every document endpoint.
- Validate DOCX ZIP signature/MIME/size.
- Add signed-download endpoint and immutable version paths.
- Run the local-only API/worker security integration suite documented in
  `SECURITY_INTEGRATION_TESTS.md`; hosted deployment verification remains a
  separate deployment concern.

## Sprint 2 — Parser hardening
- Section/page setup, effective styles, headings/numbering, tables/images/captions.
- Stable location anchors and parser diagnostics.
- Golden DOCX tests.

## Sprint 3 — Audit engine
- Resolved rule-set hash and profile snapshot.
- Harden nine starter validators; add heading validators.
- Idempotent/concurrency-safe job claiming.

## Sprint 4 — Audit log UI
- Progress, summary, filters, detail drawer, PPKI source, retry.

## Sprint 5 — Fix approval
- Fix plan/items, Auto/Confirm eligibility, before-after preview, explicit approval.

## Sprint 6 — Fix engine/re-audit
- Clone to `documents-versions`, apply safe fixers, change log, output validation, automatic re-audit.

## Sprint 7 — 30-rule coverage
- Abstract, structure, table and figure captions, TOC checks.

## Sprint 8 — Export/history
- Version history, final DOCX, JSON/PDF report in `audit-reports`, signed downloads.

## Sprint 9 — Reviewer/release
- Share to reviewer, approve/request changes, security/performance, 10–20 DOCX pilot, MVP tag.

## Prompt Codex rinci

Rencana rinci per task dan prompt copy-paste untuk agent tersedia di:

- `docs/CODEX_MVP_IMPLEMENTATION_PLAN.md`
- `docs/codex-prompts/README.md`
- `docs/codex-prompts/sprint-00-foundation.md` sampai `sprint-11-review-release.md`

Gunakan satu task per sesi agent agar konteks tetap kecil.
