# Append-only audit trail

S1-T05 adds a server-only operational audit trail. It records who caused a
critical mutation, what resource changed, when it happened, and which request
or audit job ties related events together. It is not a replacement for an
audit job or its validation evidence:

- an `audit_job` is the stateful execution of a document audit;
- an `audit_finding` is an immutable validation result belonging to that job;
- an `audit_trail_event` is an append-only operational fact about a critical
  document, Storage, or audit transition.

Ordinary reads, worker poll misses, individual findings, and individual rule
snapshot rows are deliberately not events. Authorization denials are not
recorded because the repository does not yet have a safe, non-enumerating
security-event route. Configuration failures that occur before the database is
ready remain startup diagnostics rather than database events. Formatting
profile, profile-version, and profile-rule changes are also omitted from the
MVP because the repository has no runtime mutation route for that seeded
catalog.

## Event catalog and source of truth

| Action | Source | Resource | Safe metadata |
| --- | --- | --- | --- |
| `document.created` | database trigger | document | none |
| `document.status_changed` | database trigger | document | previous/new status |
| `document.version_created` | database trigger | document version | version number |
| `document.upload_completed` | API application writer | document version | size and normalized MIME type |
| `document.download_authorized` | API application writer | document version | download kind |
| `storage.orphan_cleanup` | API best-effort writer, only after cleanup succeeds | storage object | generic cleanup reason |
| `audit.requested` | API application writer | audit job | audit status |
| `audit.processing_started` | database trigger with worker transaction context | audit job | previous/new status |
| `audit.rule_snapshot_created` | worker application writer, once per resolved set | audit rule snapshot aggregate | applicable-rule count |
| `audit.completed` | database trigger with worker transaction context | audit job | status, rule count, finding count |
| `audit.failed` | database trigger with worker transaction context | audit job | status and generic failure category |
| `audit.cancelled` | database trigger | audit job | previous/new status |

A re-audit creation writes the existing `audit.requested` action in the same
transaction as its new audit and cloned snapshots. Its correlation is the new
audit ID and its causation ID is the source fix-execution ID. The lineage IDs
are persisted on the audit job rather than duplicated into unrestricted event
metadata. Replay returns the existing audit and does not append another
request event.

Trigger-generated events cover mutations that must remain visible even if an
application call site is missed. Aggregate application events prevent hundreds
of snapshot or finding events. Each action has exactly one source of truth, so
the application does not duplicate its trigger-generated counterpart.

`storage.orphan_cleanup` is the only explicitly best-effort event and uses the
`service/api` actor: it describes
recovery after the primary database transaction failed, and an audit-write
failure must not recreate the orphan it just removed. All other application
events are committed in the same database transaction as their business
mutation. No transaction spans DOCX parsing or validation.

## Actor and request context

Actors are `user`, `service`, or `system`. A user actor requires an Auth user
UUID and has no service name. A service actor requires one of `api`, `worker`,
`database`, or `maintenance` and has no user UUID. A system actor has neither.
The API derives user identity from the validated principal; request DTOs never
supply the actor or resource owner.

Every event has a UUID correlation ID. API upload/download requests create one
server-side UUID. An audit request and all worker/database events for that job
use the audit-job UUID, so one job can be traced end to end. An optional
causation UUID and a bounded normalized request ID are supported but are not
invented when the current request pipeline has no trusted value.

The writer places actor and correlation values in transaction-local PostgreSQL
settings. Business triggers consume them when present and otherwise fall back
to a system/database actor and a database-generated correlation UUID. The
settings reset at transaction end and are context only: they grant no
authorization and cannot bypass immutability or append-only enforcement.

## Safe metadata

Metadata must be a JSON object whose top-level keys are limited to:

`version_number`, `previous_status`, `new_status`, `audit_status`,
`applicable_rule_count`, `finding_count`, `file_size_bytes`, `mime_type`,
`failure_category`, `cleanup_reason`, and `download_kind`.

Values must be scalar. Arbitrary objects from request bodies are rejected.
Document/paragraph text, thesis title, user filename, Storage path, signed URL,
Supabase URL, email, name, NIM, IP address, raw user agent, access token, secret
or service key, connection string, finding actual/expected values, exception
message, and stack trace are forbidden. Failures use only a generic category.

## Database enforcement and access

`public.audit_trail_events` has no `updated_at`, deletion marker, or raw-error
column. PostgreSQL `BEFORE UPDATE` and `BEFORE DELETE` triggers raise the
generic error `Audit trail events are append-only.` for every runtime role,
including `service_role`. Only the actual table owner in a private direct
database session can perform reviewed offline maintenance. No JWT claim,
public RPC, or Data API route exposes a bypass, and foreign keys never cascade
delete events.

RLS is enabled with no permissive policies. `anon` and `authenticated` have no
table privileges. `service_role` has INSERT only; application authorization is
still required because bypassing RLS is not authorization. The private trigger
helper is security-definer with an empty search path, fully qualified objects,
validated parameters, and no execute grant for runtime roles.

The partial unique key `(action, resource_type, resource_id, correlation_id)`
prevents retrying one semantic resource event in the same correlation from
creating duplicates. Legitimate repeated actions use a new correlation. In
particular, worker retries retain the audit-job correlation and cannot duplicate
a terminal event.

## History and scope

The migration starts recording events when it is applied. Earlier activity is
not backfilled with guessed actors or timestamps. Runtime retention/purge and
an audit-history UI are not implemented. S1-T06 now exercises this contract in
the local process-to-process suite documented in
[SECURITY_INTEGRATION_TESTS.md](SECURITY_INTEGRATION_TESTS.md). S1-T05 does not
change Auth flow, existing RLS migrations, or Storage policies.

S4-T03 finding-resolution events are their own canonical append-only evidence
stream. They are not duplicated into `audit_trail_events`, avoiding two
semantic identities for one observation. The stream stores only safe resource
IDs, controlled state/classification values, and source timestamps; never
finding content or comparison fingerprints.

S4-T04 follows the same single-source principle: `finding_review_events` is the
canonical request/decision trail and is not duplicated into this generic
operational table. It records bounded notes only in the authorized review
stream; application logs and generic metadata never receive note content.

S4-T05 stores remediation attempt/category/safe-code/result evidence on the Fix
execution. Normal worker logs may include IDs, attempt, category/code, and
transition only. They never include the full claim token, approved plan,
document content, filename, Storage path/URL, raw exception, or stack trace.
Storage/DB compensation is documented in
[REMEDIATION_FAILURES.md](REMEDIATION_FAILURES.md).
