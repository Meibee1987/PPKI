# Fix execution

S3-T04 accepts an exact deterministic S3-T03 preview and executes it
asynchronously. It never edits the uploaded source object. The worker downloads
the owned source, verifies its persisted SHA-256 and size, copies it to a
server-created temporary workspace, applies every operation in ordinal order,
validates the complete output, and only then creates one child
`DocumentVersion` and advances the document's current version.

## API

`POST /api/audits/{auditId}/fix-executions` requires authentication and an
`Idempotency-Key` header containing a non-empty UUID. Its JSON body contains
only `findingIds` and the lowercase SHA-256 `planHash`. Owner, document/version
IDs, capabilities, operations, paths, filenames, actual/expected values, and
full plans are server-derived and are never accepted from the client.

The API reloads the owned completed audit and immutable finding/rule snapshots,
normalizes the selection exactly as preview does, regenerates the preview,
requires `Ready`, checks that every operation has the exact apply-provider
version, and compares the supplied plan hash in constant time. A new job returns
HTTP 202. An exact idempotent replay returns the canonical existing job with
HTTP 200. Malformed input returns 400, unknown or foreign resources return the
same safe 404, and hash/state/capability/idempotency conflicts return 409.

`GET /api/audits/{auditId}/fix-executions/{executionId}` returns only safe
lifecycle data: source/result lineage, state, plan hash, operation counts,
timestamps, result SHA-256, and a controlled failure code. It never returns a
storage path, URL, filename, document content, raw XML, approved snapshot,
secret, or exception detail.

## State, idempotency, and concurrency

The lifecycle is `Queued -> Processing -> Completed|Failed|NoChange`. Terminal
jobs are immutable. A failed job is terminal; the same accepted source/plan
resolves to its canonical execution rather than silently starting a new one.
The database uniquely constrains `(audit_job_id, idempotency_key)` and
`(source_document_version_id, plan_hash)`. Workers claim with row locking and
`SKIP LOCKED`; an expired processing lease is recoverable. A deterministic
result object identity based on the execution ID lets a retry verify and reuse
an already-uploaded identical object without producing another version.

Version finalization runs in one serializable transaction while locking the
document row. The next `version_no` remains unique, the result points to the
source through `parent_version_id`, and the current version advances only after
the version row and completed execution state are durable. Upload/validation
failure does not create a version. A database failure after a new upload causes
best-effort orphan deletion.

## Initial production capability

The sole production capability is version `1.0` for validation key
`body.justified`, rule `PPKI-LAY-019`:

- target: exact main-document, visible, non-heading, non-table paragraph from
  the immutable body-element and paragraph indexes;
- precondition: rule/key/fix mode, structural location, property, expected
  value, and effective actual alignment must match the approved finding
  snapshot;
- mutation: set direct paragraph formatting `w:pPr/w:jc` to the Open XML
  justified value `both`;
- postcondition: the exact target parses with direct alignment `Justified` in
  parser schema 4.0;
- preserved data: paragraph/run text, all unrelated formatting, styles,
  sections, and package relationships.

The provider neither accesses storage nor persistence and never searches by
document text. It only mutates the controlled temporary copy. The worker checks
that text hashes remain unchanged, output is non-empty and within 50 MB, and
Open XML/parser reopening succeeds. It also compares the exact OPC package
inventory, content-type declarations, and canonical relationship identities
(owning part, relationship ID/type, target URI, and target mode). Only
`word/document.xml` may differ; every other part must remain byte-identical.
All operations are all-or-nothing; any controlled precondition, mutation, or
postcondition failure prevents upload/version finalization.

The local runtime database contract is exercised with
`npm run test:fix-execution-local`. The smoke is rerunnable and uses only the
canonical local Supabase PostgreSQL container. It verifies clean queued insert,
constraints, immutable identity, allowed and rejected lifecycle transitions,
result ownership lineage, lease recovery, concurrent `SKIP LOCKED` claims,
unique keys, owned/foreign RLS visibility, and denial of authenticated writes.
It prints only named PASS/FAIL results and never reads environment files or
prints local credentials.

The golden fixture pair is `minimal-invalid-layout.docx` (before) and
`minimal-invalid-layout-justified.docx` (expected after). No other validation
key has production apply capability in S3-T04.

S4-T01 adds an explicit, idempotent re-audit request after a completed
execution. It targets the execution result version while reusing the source
audit's exact profile, document-kind snapshot, resolved hash, and cloned rule
snapshots; see [REAUDIT_ORCHESTRATION.md](REAUDIT_ORCHESTRATION.md). Fix apply
itself still does not create or run an audit.

Ignore/accepted-risk workflow, approval, partial apply, rollback,
preview/apply/re-audit UI, export, and lecturer review remain deferred.

S4-T03 derives `Applied` evidence only from a completed execution whose exact
finding selection matches its immutable approved-plan snapshot and which has a
result version. It never accepts a selection from the reconciliation client.
The observation is a separate replayable transaction, so a resolution-write
failure cannot undo a completed fix execution.

## S4-T05 failure and conflict hardening

`Processing -> Queued` is allowed only for typed transient infrastructure
failure. `fix-retry/1.0` retains the exact approved snapshot and uses at most
three attempts with fixed backoff. Each claim/reclaim receives a new UUID
fencing token; heartbeat, retry, NoChange, failure, and completion require the
exact active token.

Acceptance and the worker require the source version to remain current. Final
publish repeats the check while locking document and execution rows. Result
upload is create-only at a key derived from execution ID: identical SHA/size is
reusable, different content conflicts, and database failure deletes only an
object created by that attempt. See
[REMEDIATION_FAILURES.md](REMEDIATION_FAILURES.md).
