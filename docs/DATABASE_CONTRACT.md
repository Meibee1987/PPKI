# Database contract

`202608010001_initial_schema.sql` is treated as applied, immutable migration
history. S1-T01 adds `202608020001_ownership_integrity.sql`; it is additive and
does not invent values for historical data.

## Ownership

```text
auth.users
  -> user_profiles
  -> documents
  -> document_versions
  -> audit_jobs
  -> audit_rule_snapshots
  -> audit_findings
```

`user_profiles.id` is the `auth.users.id` UUID and has one profile per auth
user. Its FK uses `CASCADE`; however, deletion of an Auth user with owned
documents, created versions, or requested jobs is blocked by their `NO ACTION`/
`RESTRICT` relationships until an explicit retention/disposal workflow exists.
`documents.owner_user_id`,
`document_versions.created_by_user_id`, and (for all new jobs)
`audit_jobs.requested_by_user_id` reference `auth.users(id)`. An audit worker
is an executor, never the requester. No email is a foreign key and the API
derives these IDs from the authenticated principal rather than request input.

Findings have no separate owner. Their ownership is derived through audit job,
document version, and document. S1-T02 enables RLS for all application tables:
authenticated users can only read their ownership chain and cannot write
business data directly. S1-T03 defines private canonical Storage keys and
server-only object access; see [STORAGE_SECURITY.md](STORAGE_SECURITY.md).

## Tables and relationships

| Table | Responsibility | Delete behavior |
| --- | --- | --- |
| `user_profiles` | One profile per Auth user | Auth user cascade, subject to other Auth references blocking user deletion |
| `document_types` | Reference type and academic work kind | referenced by documents with no action |
| `formatting_profiles` | Named PPKI source edition | referenced by profile versions with no action |
| `profile_versions` | Versioned configuration status | profile no action; assignments cascade |
| `rules` | Stable, source-data rule definition | referenced by assignments/findings with no action/restrict |
| `documents` | One user-owned thesis document and its type | document type is no action; auth owner is no action |
| `document_versions` | Insert-only uploaded version metadata | document and parent relationships are restricted |
| `audit_jobs` | One requested audit of one document version against one profile version | document/profile versions are restricted; requester auth user is restricted |
| `audit_rule_snapshots` | Insert-only resolved rule semantics for one audit | audit job and source rule are restricted |
| `audit_findings` | Historical result rows belonging to one audit | audit job and rule are restricted |
| `audit_trail_events` | Append-only operational facts for critical document, Storage, and audit mutations | Auth actor/owner references are restricted; resource IDs are historical snapshots without cascading FKs |
| `profile_rules` | Rule assignment for one profile version | profile version cascade; rule restrict |

S4-T01 adds two nullable, paired relationships on `audit_jobs`:
`source_audit_job_id` references the completed historical audit and
`source_fix_execution_id` references its completed fix execution, both with
`RESTRICT`. The audit's existing `document_version_id` points to the exact
execution result. A unique constraint on `source_fix_execution_id` permits one
canonical re-audit per execution; ordinary and legacy audits keep both fields
null and no backfill is performed.

`formatting_profiles -> profile_versions` is restricted and version numbers are
unique per profile. `profile_versions -> profile_rules -> rules` prevents a
duplicate rule assignment. Rule codes remain unique and stable.

## Integrity rules

- Documents require an owner, valid document type, non-blank title of at most
  512 characters, `Active` or `Archived` status, and `updated_at >= created_at`.
- Versions use the repository names `version_no`, `storage_key`, and
  `size_bytes`. Their version number is positive and unique per document;
  versions after version 1 require a parent; the parent cannot be itself and a
  trigger requires it to belong to the same document.
- A checksum is lowercase hexadecimal SHA-256, exactly 64 characters.
  `size_bytes` is positive. A storage key is a non-empty private logical key:
  it cannot be a URL, root path, backslash path, or contain `..`.
- Audit job statuses are `Queued`, `Processing`, `Completed`, `Failed`, and
  `Cancelled`. Terminal jobs require `completed_at`; completion cannot precede
  start; completed jobs require a lowercase 64-character resolved rule-set
  hash. Counts cannot be negative. Persisted failure text is generic, while
  detailed diagnostics remain worker-only.
- Audit completion requires `applicable_rule_count` to equal the immutable
  resolved rule snapshot rows. The worker persists the full snapshot and its
  canonical SHA-256 before validation; terminal jobs cannot change or be
  deleted.
- A lineage audit must match the source audit's profile, document-kind
  snapshot, resolved hash/count, and exact immutable rule-snapshot set. Its
  requester and result version must match the completed fix execution, and the
  source/result versions must belong to one document. A deferred trigger
  rejects partial or different clones and copied initial findings.
- Findings retain rule-code, severity, fix-mode, and supported source-reference
  snapshots (source section, PDF page, and printed page) alongside JSON
  actual/expected/location values. JSON is an object,
  array, or JSON `null`; JSON null is permitted when a validator has no actual
  or expected value. Findings never contain complete DOCX or paragraph text.
  Severity is `Error`, `Warning`, or `Info`; fix mode is `Auto`, `Confirm`,
  `Manual`, or `Report`.
- Profile version statuses are `Draft`, `Active`, or `Retired`.

The new checks are `NOT VALID` to avoid a data-lossful or arbitrary backfill.
PostgreSQL enforces them for every new or changed row. In particular,
`requested_by_user_id` and finding snapshots are required for new rows; legacy
rows may remain incomplete until an auditable remediation migration is chosen.

## Immutability and future work

Document versions, resolved rule snapshots, terminal audit jobs, and their
findings are protected by S1-T04 PostgreSQL triggers, including against
`service_role`. See [IMMUTABILITY.md](IMMUTABILITY.md) for the state machine,
canonical hash, maintenance boundary, and atomic worker flow. S1-T05 adds a
separate append-only operational trail; an event is neither a job nor a
finding. Its resource reference is retained as a UUID snapshot and cannot
cascade with operational data. See [AUDIT_TRAIL.md](AUDIT_TRAIL.md) for its
event, actor, correlation, metadata, and source contracts. RLS policy and
least-privilege grants are defined by S1-T02/S1-T04/S1-T05; see
[DATABASE_SECURITY.md](DATABASE_SECURITY.md) for the access matrix.
Re-audit lineage and worker behavior are described in
[REAUDIT_ORCHESTRATION.md](REAUDIT_ORCHESTRATION.md).

S4-T03 adds `finding_resolution_cases` (one immutable case per source finding)
and `finding_resolution_events` (append-only remediation facts). Every FK is
`RESTRICT`; there is no backfill, so a legacy finding without a case reads as
`Open`. Owner-only RLS follows case -> finding -> audit -> version -> document.
Browser clients receive SELECT only. Unique case, sequence, and deterministic
source-event identities plus triggers and serializable transactions enforce
replay and concurrency safety. See [FINDING_RESOLUTION.md](FINDING_RESOLUTION.md).

S4-T04 adds one immutable `finding_review_cases` row per reviewed finding and
append-only `finding_review_events`. All foreign keys are `RESTRICT`; no
historical backfill occurs. The additive admin-only correction admits only an
exact database-authoritative `PPKIAdmin` and intentionally permits operational
self-approval. Browser clients receive admin-gated read access only and cannot
change profile roles or review rows. See
[FINDING_REVIEW.md](FINDING_REVIEW.md).

The S4-T04 final closure keeps `documents.owner_user_id` and derived ownership
chains as immutable provenance, not as an access boundary between internal
PPKIAdmin accounts. Migration `202608060001_shared_ppki_admin_access.sql`
provides shared read visibility without ownership transfer or assignment;
backend business routes use the same database-authoritative admin gate.

S4-T05 migration `202608060002_remediation_failure_conflict_hardening.sql`
adds claim token, bounded attempt/backoff, typed safe failure, and result-object
evidence to `fix_execution_jobs`. Its replacement trigger keeps request/plan
identity immutable, locks the document while accepting a current source,
validates claim transitions, prevents stale finalization, and requires exact
result parent/creator/hash/size lineage. Older migrations remain unchanged.
