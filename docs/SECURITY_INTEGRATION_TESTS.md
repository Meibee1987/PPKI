# Local database security integration suite

S1-T06 closes Sprint 01 with a process-to-process security suite. It launches
the compiled ASP.NET Core API and two worker processes against the running
Supabase local stack, authenticates two synthetic users through local Auth, and
exercises the existing product endpoints. It does not add a test endpoint,
start Supabase, reset the database, read a local `.env`, or contact a hosted
project.

Run from the repository root after Supabase local is running and the .NET
packages have been restored:

```powershell
npm run test:security-integration-local
```

The harness obtains local development values from `supabase status -o env`,
requires both the API and PostgreSQL hosts to be loopback addresses, and
matches the running database container to `supabase/config.toml`. It chooses an
unused loopback port for the API, builds with `--no-restore`, waits for
`/health/live` and `/health/ready`, and captures API/worker logs in a temporary
directory. All child processes and temporary logs are removed in `finally`.

The suite never runs `supabase start` or `supabase db reset`. A reset destroys
local development data and must be run separately only after explicit approval.
A port/health timeout fails the suite rather than hanging indefinitely.

## Integration surface and gaps

The existing API exposes authenticated profile, rule summary, document list,
document upload, document detail (including versions), audit request, audit
status, findings, and original download routes. There is no standalone version
list/detail route, report download route, document mutation route, retry route,
or test-only route. Version authorization is therefore covered through
document detail, audit request, and original download. The report and version
Storage buckets are checked for privacy, but no product lifecycle writes them
in Sprint 01.

Bearer tokens are validated by calling local GoTrue `/auth/v1/user`. The API
uses the validated subject as the owner and ignores additional owner/Storage
fields in multipart input. The API's PostgreSQL role is the table owner and can
bypass RLS, so every by-ID endpoint also filters through the document ownership
chain. The worker conditionally changes `Queued` to `Processing`; two real
workers race the same queued job during the suite.

## Principal/resource assertion matrix

| Principal | Resource and operation | Expected result/visibility | Cleanup |
| --- | --- | --- | --- |
| anonymous | protected API list | 401; no body data | none |
| anonymous | Data API application tables | denied | none |
| anonymous | direct original Storage read | denied | none |
| user A | API upload/list/detail/audit/status/findings/download for A | allowed; owner derived from token | database owner and Storage server cleanup |
| user A | direct Data API SELECT | only A's ownership chain; audit trail hidden | database owner cleanup |
| user A | direct Data API writes and direct Storage writes | denied | none |
| user B | own upload/list | allowed and owned by B even with spoofed A owner field | database owner and Storage server cleanup |
| user B | A document detail/version audit/audit status/findings/download | masked 404; no A identifiers or signed URL | none |
| user B | direct Data API SELECT | only B's ownership chain | database owner cleanup |
| API server | business inserts and signed URL creation | allowed through database-owner/Storage credentials only after endpoint authorization | process stopped; owner cleanup |
| worker | queue claim, snapshot/finding insert, terminal transition, original read | one claim and one terminal result | process stopped; owner cleanup |
| `service_role` direct | table operations | follows explicit grants; document SELECT and authenticated writes remain unavailable | temporary smoke grants are revoked by component tests |
| database owner | fault setup, inspection, and cleanup | local direct session only; never exposed through API/RPC | temporary functions/triggers dropped |
| browser principals | three private buckets, upload/delete/read | all direct operations denied | Storage server cleanup |
| signed URL holder | one original object before expiry | read succeeds; URL never logged or persisted | object deleted during cleanup |

Every fixture belongs to `user-a@example.invalid` or
`user-b@example.invalid`; IDs and passwords are generated at runtime. The
committed DOCX fixtures are synthetic and are only read.

## Lifecycle and security assertions

The owner lifecycle is Auth -> API upload -> document/version row -> private
canonical original object -> audit request -> competing workers -> immutable
rule snapshots -> findings -> Completed audit -> authorized signed download.
The suite checks checksum, size, canonical path, private buckets, snapshot hash
and count, finding availability, and the corresponding append-only events.

For user B, all existing A-resource routes return the contract's masked 404.
The response scanner ensures the body does not reveal A's IDs, title, checksum,
finding data, or signed URL. The suite separately proves that the API database
principal owns the table—and therefore bypasses RLS—while the endpoint still
rejects user B.

Direct Data API coverage checks document isolation, snapshot ownership, denied
writes, reference grants, and the server-only audit trail. The established
RLS, Storage, immutability, and audit-trail component smoke tests are then run
as subprocess regressions instead of duplicating their complete matrices.

## Concurrency, retry, and fault injection

Two workers race one queued job. The suite requires one processing event, one
snapshot aggregate, unique snapshot rule/ordinal keys, one terminal event, and
unchanged snapshot/finding/event counts after further polling.

Faults are injected only by temporary database-owner triggers or by removing a
synthetic Storage object. They are never configured through a public endpoint:

- successful Storage upload followed by document INSERT failure must delete
  the orphan, leave no partial row, and write a generic cleanup event;
- snapshot INSERT failure must roll back all snapshots and end in `Failed`
  with generic failure metadata;
- finding INSERT failure must retain the completed snapshot transaction but
  roll back every finding and never emit a false Completed event;
- signed URL generation failure returns only a generic failure, creates no
  success event, and still performs ownership authorization first;
- a worker terminated after claim leaves one `Processing` job with no partial
  snapshot. The current Sprint 01 contract has no lease/requeue mechanism, so a
  replacement worker intentionally does not reclaim it. This recovery gap is
  recorded rather than changing the lifecycle for the test.

All fault functions and triggers are dropped in cleanup. Temporary parser DOCX
files are compared against a baseline and must leave no new files.

## Log, report, and cleanup hygiene

HTTP-client Information logging is suppressed because it includes complete
Storage request URLs. Logs and collected error responses are scanned in memory
for the actual local credentials, password, signed URL, bearer/header patterns,
connection strings, query signatures, and canonical Storage paths. Only
assertion names and PASS/FAIL are printed; log lines and response bodies are
never printed.

The ignored runtime report is
`artifacts/security-integration-summary.json`. It contains only suite version,
timestamp, `localOnly`, duration, totals, per-component counts, and cleanup
status. It contains no identities, credentials, URL, object path, or document
content.

Cleanup removes synthetic objects, events, findings, snapshots, jobs, versions,
documents, profiles, Auth users, fault functions, processes, and temporary
logs. The four component smoke tests also restore any temporary ACL grants.
Running the command twice must produce the same passing result.

## Test levels and CI strategy

- unit tests validate helpers, configuration, metadata, and deterministic code;
- static schema tests inspect migrations and source contracts offline;
- component smoke tests exercise one database/Storage boundary at a time;
- this suite launches real API/worker processes for local end-to-end coverage;
- hosted/staging deployment remains a separate deployment concern and is not
  implied by a local PASS.

The full suite remains a documented local/manual gate until it has accumulated
stable runs on CI runners. Regular CI continues to run unit/static and hygiene
checks. No hosted credential or automatic full-stack pull-request job is added.
If promoted later, use a `workflow_dispatch` job with `contents: read`, an
explicit timeout, local Supabase only, and the safe summary artifact. An actual
GitHub run must be reported separately from local verification.

For troubleshooting, stop conflicting API/worker processes, confirm the local
Supabase stack is healthy, restore/build .NET once, and rerun. A failed run is
safe to repeat because pre-cleanup handles fixtures left by an interrupted
previous attempt.

