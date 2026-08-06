# Historical record immutability

S1-T04 makes document versions and resolved audit evidence immutable at the
PostgreSQL boundary. Enforcement uses small `BEFORE` triggers in addition to
RLS because the trusted server role bypasses ordinary row policies.

## Insert-only records

`document_versions` is insert-only. Every file mutation or future fixer must
create a new version with a new canonical Storage object. An existing version
cannot be updated or deleted by `anon`, `authenticated`, `service_role`, the
API, or the worker. Its document, version number, parent, creator, bucket, key,
filename, MIME, size, checksum, and creation time remain historical facts.
`documents -> document_versions` uses `RESTRICT`, so document deletion cannot
silently cascade through history.

`audit_rule_snapshots` is also insert-only. A snapshot contains rule code,
domain/subdomain, context selector, element, requirement JSON, validation key
and parameter JSON, severity, fix mode, source-reference JSON, resolution layer
and precedence, deterministic ordinal, and snapshot schema version. It may
retain the source rule ID for integrity, but never relies on that live row to
explain an old audit. It contains no document text, student data, or runtime
validator result.

Database-owner maintenance is the only trigger exception. It requires a
private direct table-owner session and is not exposed through the Data API,
JWT claims, `service_role`, or a public maintenance function. It is intended
only for explicitly reviewed retention/remediation. Runtime hard-delete and
retention APIs do not exist.

## Audit state machine

The runtime transitions are:

```text
Queued -> Processing -> Completed
                    `-> Failed
                    `-> Cancelled
Queued ----------------> Cancelled
```

`Queued -> Completed`, `Queued -> Failed`, `Processing -> Queued`, and every
transition out of `Completed`, `Failed`, or `Cancelled` are rejected. Job ID,
document version, profile version, nullable document-kind snapshot, requester,
and creation time never change.
`started_at` is set once when processing is claimed. `completed_at` is required
for terminal states and cannot precede `started_at`; a queued cancellation may
have no start time. Terminal rows reject every update and delete.

S4-T01 re-audits add nullable `source_audit_job_id` and
`source_fix_execution_id`. Both are null for ordinary/legacy audits and both
are required for a re-audit. Once present, the lineage, result document
version, source profile version, document-kind snapshot, requester, resolved
hash/count, and creation time are immutable. A unique source fix-execution
reference selects one canonical re-audit without rewriting historical rows.

While processing, the worker may set the resolved snapshot hash/count and then
finding counts, score, a generic failure message, and terminal state. Once a
resolved hash exists, it and `applicable_rule_count` are immutable. Completion
requires a lowercase 64-character hash and a count equal to the persisted
snapshot rows. Persisted failure text is bounded, single-line, and generic.

New audit jobs capture `document_kind_snapshot` from the selected document type
when the job is created. Validation reads only this audit-owned value, never the
live document/type relation, so later metadata changes cannot alter
applicability during recovery or replay. The column remains nullable solely for
historical rows and is not backfilled from current data; validators retain
their safe null-context behavior. This context is separate from the resolved
rule-set snapshot and is not part of its hash.

## Canonical rule-set hash

`ResolvedRuleSetHasher` writes UTF-8 JSON with a fixed outer property order,
sorts rules by deterministic ordinal and rule code, recursively sorts JSON
object properties, and emits SHA-256 as 64 lowercase hexadecimal characters.
JSON whitespace, input enumeration order, audit/job IDs, timestamps, user IDs,
document IDs, document content, and runtime results are excluded.

The hash includes requirement JSON, validation key/parameters, severity, fix
mode, source reference, layer, precedence, ordinal, context, and
`snapshot_schema_version`. A semantic rule change therefore changes the hash.

## Atomic worker flow

The worker conditionally claims one queued ID with `UPDATE ... WHERE status =
'Queued'`; only the worker that updates one row proceeds. `AuditRunner` then:

1. resolves assigned implemented rules, with the existing implemented-catalog
   fallback for profiles that predate explicit assignments;
2. builds and hashes canonical snapshots;
3. locks the audit row and, in one transaction, inserts all snapshots and sets
   their hash/count;
4. validates the DOCX from the immutable version using the audit's immutable
   nullable document-kind snapshot;
5. in another transaction, inserts findings while the job is processing and
   transitions the job to completed.

Unique keys `(audit_job_id, rule_code)` and `(audit_job_id, ordinal)`, plus the
locked-row retry check, prevent duplicate snapshots. A retry reuses and verifies
the persisted snapshot rather than resolving a new historical meaning. A
validation failure transitions a still-processing job to `Failed` with a
generic message; partial snapshot and completion transactions roll back.

For a re-audit, the API transaction clones the source audit's exact snapshot
rows before the queued audit commits. A deferred database trigger verifies the
complete clone, and the canonical hasher verifies the source and clone against
the persisted source hash. The existing worker then reads those precloned rows
and the result `DocumentVersion`; it does not resolve live rules for that job.
Source findings are not copied and remain immutable.

Findings can only be inserted for a processing audit. After their parent is
terminal they cannot be updated or deleted. API responses use finding and rule
snapshot fields rather than current `rules` values. Future ignore/fix/reviewer
actions must use separate action records; they must not rewrite findings.

## Local verification and scope

After explicit approval to reset local synthetic data, run:

```powershell
npx supabase db reset
npm run test:immutability-local
npm run test:rls-local
npm run test:storage-local
```

The immutability smoke uses the local Data API with `service_role` for runtime
attempts and a direct local table-owner session only for deterministic setup and
cleanup. Output is assertion name plus `PASS`/`FAIL`; it never prints keys,
tokens, connection strings, object paths, or document content.

S1-T05 now adds the separate append-only operational audit trail documented in
[AUDIT_TRAIL.md](AUDIT_TRAIL.md). Its events describe immutable operations;
they do not replace audit jobs, rule snapshots, or findings. Runtime retention,
FixPlan, reviewer workflow, export, and the S1-T06 security integration suite
remain out of scope. No Storage policy or old migration is changed.

The S4-T01 orchestration and its non-destructive local smoke are documented in
[REAUDIT_ORCHESTRATION.md](REAUDIT_ORCHESTRATION.md).

S4-T03 adds immutable finding-resolution case identities and append-only
evidence events without updating findings, rule snapshots, audits, executions,
or document versions. Database triggers reject case update/delete and event
update/delete; current state is projected from the last sequence. See
[FINDING_RESOLUTION.md](FINDING_RESOLUTION.md).

S4-T04 adds immutable `finding_review_cases` and append-only
`finding_review_events`. Manual dispositions never update a finding, resolution
event, audit, snapshot, execution, version, score, or DOCX. See
[FINDING_REVIEW.md](FINDING_REVIEW.md). The additive admin-only correction
permits operational self-approval but does not weaken event immutability or make
review evidence equivalent to S4-T03 verified resolution.

S4-T05 keeps approved plan and execution identity immutable while adding
operational lease fields guarded by an exact per-attempt fencing token.
Attempts never decrease and are capped at three. Only typed transient failure
may return `Processing -> Queued`; terminal executions stay immutable.
Successful publish inserts exactly one new child `DocumentVersion` and advances
current in the same serializable transaction. Superseded source, NoChange,
failure, or cleanup failure creates no partial version.
