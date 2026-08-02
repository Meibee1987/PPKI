# Developer command inventory

This inventory records the command sources found during S0-T03 and the
canonical replacements. Run canonical commands from the repository root.

| Need | Previous command sources | Canonical command |
| --- | --- | --- |
| Restore, build, test backend | Baseline docs and Dockerfiles use `dotnet`; CI previously used Release configuration | `npm run verify` (or the documented individual commands in `BASELINE_VERIFICATION.md`) |
| Install, config test, typecheck, build web | Baseline uses `npm --prefix apps/web`; CI previously changed directory and omitted config test | `npm run verify` |
| Validate Compose | Baseline uses example environment explicitly | `npm run verify` |
| Check API health manually | API runtime | `Invoke-WebRequest http://localhost:8080/health/live` and `/health/ready` |
| Generate/check synthetic DOCX fixtures | `backend/tests/fixtures/docx/README.md` | `npm run fixtures:generate`, then `npm run fixtures:check` |
| Run local API/worker security integration | Sprint 01 security closure | `npm run test:security-integration-local` (requires an already-running local Supabase stack; never starts or resets it) |
| Start stack | README, Supabase setup, bootstrap scripts used `docker compose up --build` | `npm run dev:up` |
| Rebuild stack | Not previously standardized | `npm run dev:rebuild` |
| Status and logs | Not previously documented as root scripts | `npm run dev:status`, `npm run dev:logs[:api|:worker|:web]` |
| Stop stack | Older documentation used `docker compose down` | `npm run dev:down` |
| Supabase CLI install | Bootstrap and setup documentation used `npm install` | `npm ci` before the existing `supabase:*` scripts |

`npm run verify` is implemented in `scripts/developer.mjs` with no external
task-runner dependency. The script owns the stage order and output, which avoids
duplicating Windows and Linux shell logic. CI splits those same backend and web
commands into readable jobs, while its offline hygiene gates use `npm run
check:secrets` and `npm run check:migrations`. CI does not use a local `.env` or
connect to hosted Supabase.

Dockerfiles remain build-image definitions rather than developer entry points:
the API and worker restore then publish with .NET 10, and the web image runs
`npm ci` then `npm run build`. Historical `docs/codex-prompts/` files describe
past implementation prompts and are not command references.
