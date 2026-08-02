# Baseline verification

This repository has a .NET 10 backend and a Node.js 24 web application. The
canonical verification command, run from the repository root, is:

```powershell
npm run verify
```

It stops at the first failed stage and returns that failure as a non-zero exit
code. The individual commands below document the fixed sequence used by the
entry point; do not substitute `npm install` for `npm ci`.

## Backend

```powershell
dotnet restore backend/PpkiSmartFormatter.slnx
dotnet build backend/PpkiSmartFormatter.slnx --no-restore
dotnet test backend/PpkiSmartFormatter.slnx --no-build
```

Expected result: restore and build complete with zero warnings and zero errors;
the `Ppki.RuleEngine.Tests` test suite passes.

## Web

```powershell
npm --prefix apps/web ci
npm --prefix apps/web run test:config
npm --prefix apps/web run typecheck
npm --prefix apps/web run build
```

Expected result: dependencies install from `apps/web/package-lock.json`,
TypeScript type checking succeeds, and Next.js produces a production build.

The current dependency installation reports three high-severity npm audit
findings and an `allow-scripts` notice for `sharp`. These are dependency audit
warnings and do not fail the typecheck or build commands.

## Compose configuration

For a validation that does not load local secrets, use the example environment
file explicitly:

```powershell
docker compose --env-file .env.example config --quiet
```

Expected result: the Compose configuration validates successfully. Running the
stack additionally requires valid local Supabase configuration; never commit or
print values from `.env`.

## Required Supabase configuration

The API and worker require `SUPABASE_URL`, `SUPABASE_PUBLISHABLE_KEY`,
`SUPABASE_SECRET_KEY`, and `SUPABASE_DB_CONNECTION`. The three storage bucket
names are required by the services and default to `documents-original`,
`documents-versions`, and `audit-reports` when not overridden.

The web application requires `NEXT_PUBLIC_SUPABASE_URL`,
`NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY`, and `NEXT_PUBLIC_API_BASE_URL`.
Only the URL and publishable key may use the `NEXT_PUBLIC_` prefix; a secret or
service-role key is rejected. API base URL, CORS origin, ports, and worker poll
interval have non-secret local defaults.

API and worker validate configuration before opening a database or storage
connection. They reject missing, blank, placeholder, malformed, and non-HTTPS
hosted Supabase URL values. Error messages name a setting but never echo its
value.

## CI alignment

CI separates `backend`, `frontend`, `repository-hygiene`, and `database` jobs
so a failed gate is immediately identifiable. The backend and frontend jobs use
the same individual commands listed above; the frontend build injects only the
non-secret public example values needed for static web compilation. CI does not
connect to hosted Supabase and does not require or read a local `.env`.

The repository-hygiene job runs the developer runner tests, `git diff --check`,
the tracked-file secret scan, and Compose validation with `.env.example`. The
database job runs the offline migration checker. Run the new checks locally
with `npm run check:secrets` and `npm run check:migrations`; their companion
test commands are `npm run test:secret-hygiene` and `npm run test:migrations`.
The migration checker deliberately uses only local filesystem checks and no
Supabase CLI, so it neither needs a CLI version nor contacts a hosted project.

## Verified baseline

On 2026-08-02, the backend commands were verified in the official .NET 10 SDK
container and the web commands in the official Node 24 container. The host did
not have a .NET SDK installed, and its local npm cache was locked (`EPERM`), so
the isolated containers were used to avoid changing system configuration.
