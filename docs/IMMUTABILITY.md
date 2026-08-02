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
document version, profile version, requester, and creation time never change.
`started_at` is set once when processing is claimed. `completed_at` is required
for terminal states and cannot precede `started_at`; a queued cancellation may
have no start time. Terminal rows reject every update and delete.

While processing, the worker may set the resolved snapshot hash/count and then
finding counts, score, a generic failure message, and terminal state. Once a
resolved hash exists, it and `applicable_rule_count` are immutable. Completion
requires a lowercase 64-character hash and a count equal to the persisted
snapshot rows. Persisted failure text is bounded, single-line, and generic.

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
4. validates the DOCX from the immutable version;
5. in another transaction, inserts findings while the job is processing and
   transitions the job to completed.

Unique keys `(audit_job_id, rule_code)` and `(audit_job_id, ordinal)`, plus the
locked-row retry check, prevent duplicate snapshots. A retry reuses and verifies
the persisted snapshot rather than resolving a new historical meaning. A
validation failure transitions a still-processing job to `Failed` with a
generic message; partial snapshot and completion transactions roll back.

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

S1-T04 does not implement the S1-T05 append-only audit trail, an end-user
retention workflow, FixPlan, reviewer workflow, export, or the S1-T06 security
integration suite. No Storage policy or old migration is changed.
