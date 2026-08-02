# Database security contract

S1-T02 applies a least-privilege Data API boundary. All application tables in
`public` have RLS enabled. `anon` receives no table privileges. The
`authenticated` role has only the SELECT grants and policies below; it has no
direct INSERT, UPDATE, or DELETE grant on any application table.

| Table | anon | authenticated | API / worker |
| --- | --- | --- | --- |
| `user_profiles` | none | SELECT own profile | trusted server-side read/write |
| `documents` | none | SELECT owned rows | trusted server-side business writes |
| `document_versions` | none | SELECT through owned document | trusted server-side business writes |
| `audit_jobs` | none | SELECT through owned document | trusted server-side business writes |
| `audit_findings` | none | SELECT through owned document | trusted server-side business writes |
| `document_types` | none | SELECT | API writes only |
| `formatting_profiles` | none | SELECT only when an active/effective version exists | API/seed writes only |
| `profile_versions` | none | SELECT active/effective rows only | API/seed writes only |
| `rules` | none | none | API/seed reads and writes only |
| `profile_rules` | none | none | API/seed writes only |

Policy names use `<table>_select_<scope>`. Every authenticated policy includes
`(select auth.uid()) is not null`; ownership policies compare it against the
owner derived from the database relationship, never from client input.

`documents`, `document_versions`, `audit_jobs`, and `audit_findings` follow the
ownership chain defined in [DATABASE_CONTRACT.md](DATABASE_CONTRACT.md). A
requester of an audit does not receive access merely by being its requester:
document ownership is the authority.

## Grants and RLS

Grants answer whether a role can attempt an operation; RLS decides which rows
are visible after a permitted attempt. Both are required. This migration first
revokes all table privileges from `anon` and `authenticated`, then grants only
the stated SELECT operations. There are no authenticated write policies.

`FORCE ROW LEVEL SECURITY` is intentionally not applied. The Auth profile
trigger, seed/migration flow, and trusted server-side API/worker require their
server-side path. Their ability to bypass RLS is not authorization: API
endpoints still derive the principal from the verified token and filter every
by-ID read/write by document ownership. The worker executes queued work but is
not an owner or requester.

The frontend currently uses Supabase directly only for Auth; it uses the ASP.NET
Core API for business data. Consequently, rules and profile-rule assignments
remain Data API-private, and no direct frontend database write is needed.

## Local verification

Run `npx supabase start`, `npx supabase db reset`, then `npm run
test:rls-local`. The cross-platform Node smoke test does not start Supabase and
fails fast when the local stack is unavailable. It creates the fixed synthetic
users `user-a@example.invalid` and `user-b@example.invalid`, creates two
complete ownership chains through local admin paths, verifies Data API access
with their real user tokens, and removes fixture rows and users even after an
assertion failure. Its output contains assertion names and `PASS`/`FAIL` only;
keys, tokens, passwords, connection strings, and response bodies are never
printed.

S1-T03 covers Storage policies. S1-T04 adds immutable records, S1-T05 adds the
append-only audit trail, and S1-T06 may add a full local Supabase integration
suite. No Storage policy is created by S1-T02.
