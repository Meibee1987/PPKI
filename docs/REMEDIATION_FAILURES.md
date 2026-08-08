# Remediation failure and conflict hardening

S4-T05 hardens the existing asynchronous Fix execution. It adds no Fix
capability, frontend, partial apply, rollback, merge editor, parser/rule change,
or synchronous audit. The persisted approved plan, source version, plan hash,
provider ID/version, execution ID, and operation order remain immutable across
every attempt.

## Typed failures and safe status

Internal categories are `Conflict`, `InvalidInput`, `InvalidSource`,
`InvalidPlan`, `CapabilityUnavailable`, `TransientInfrastructure`, and
`TerminalInfrastructure`. `FixFailureCatalog` owns the bounded safe codes.
Client status contains only category/code, attempt information, safe lease
state, lineage IDs, counts, hashes, and timestamps. It never contains exception
details, paths, filenames, URLs, plan/operation payloads, text, XML, or secrets.

Only `TransientInfrastructure` is retried. `fix-retry/1.0` uses at most three
attempts and a fixed five-second backoff without jitter. Retry reuses the exact
persisted plan and provider version; it never replans or reads live rules.
Conflicts, invalid source/plan data, and unavailable exact providers are
terminal. A transient failure at the third attempt becomes terminal `Failed`.

## Claim fencing and stale workers

An atomic `FOR UPDATE SKIP LOCKED` claim sets a new UUID `claim_token`, advances
`attempt_count`, and sets the lease. An expired `Processing` row can be
reclaimed only with a different token. Heartbeat, retry/failure, NoChange, and
publish require execution ID, `Processing`, and the exact active token. A stale
worker cannot renew, upload after the pre-upload fence, publish, or change
state. Terminal rows cannot be reclaimed.

## Current source, Storage, and publish

Acceptance verifies the source version is current; the insert trigger locks the
document and repeats that check. The worker verifies it before download and in
the serializable final transaction while holding document/execution row locks.
`fix-source-version-superseded` creates no result version and never moves the
current pointer backward.

The result object key is deterministic from execution ID. Output is validated
and hashed before create-only upload. An existing object is reused only when
size and SHA-256 match; different bytes are `fix-result-object-conflict` and
are never overwritten. Finalization allocates the next version under the
document lock, inserts one child of the exact source, advances current, and
completes the execution in one database transaction. Unique version/result
constraints are final guards. Replay reads the canonical result. `NoChange`
creates no permanent object/version and does not move current.

Storage and PostgreSQL cannot share one transaction. If finalization fails
after an attempt created an object, the worker deletes only that attempt-owned
object. It never deletes an identical pre-existing orphan. Cleanup failure is
`result-cleanup-failed`; no partial version row is committed.

## Downstream and verification

Re-audit and resolution still require `Completed` plus valid result lineage.
Failed and NoChange executions cannot create a re-audit, `Applied`, or
`Verified` evidence. Review disposition cannot hide or mutate Fix failure.

Run `npm run test:remediation-hardening` and then
`npm run test:remediation-hardening-local` twice. The local smoke is bounded,
uses only local PostgreSQL/RLS/API/Storage, does not reset the database, and
does not delete a volume.

S4-T06 memetakan kategori/kode aman ke pesan Bahasa Indonesia. Retry otomatis
ditampilkan sebagai pekerjaan sistem; UI tidak menawarkan Apply ulang atau
manual retry. Kode tidak dikenal tetap generik dan detail dependency mentah
tidak dirender. Lihat [REMEDIATION_UI.md](REMEDIATION_UI.md).
