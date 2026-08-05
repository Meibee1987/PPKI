# Finding resolution state

S4-T03 persists remediation evidence separately from historical `AuditFinding`
and `AuditRuleSnapshot` rows. It adds no approval, rejection, ignore,
accepted-risk, manual-resolution, reviewer, or frontend workflow.

S4-T04 adds a second, independent manual-review dimension. `Ignored`,
`AcceptedRisk`, and `ManualRemediationReported` never change this automatic
resolution state and are never projected as `VerifiedResolved`. See
[FINDING_REVIEW.md](FINDING_REVIEW.md).

## State and evidence

An owned finding with no case is returned as `Open`; the GET route is read-only
and does not create a row. A canonical case is created only when reconciliation
observes evidence for a finding selected by an immutable completed
`FixExecution` plan.

| Last event | Current state | Evidence |
| --- | --- | --- |
| none | `Open` | no persisted remediation evidence |
| `FixAppliedObserved` | `Applied` | completed execution, exact approved selection, result version |
| `ReauditPendingObserved` | `ReauditPending` | canonical queued/processing re-audit |
| `VerificationResolvedObserved` | `VerifiedResolved` | shared comparison says `NoLongerDetected` |
| `VerificationStillDetectedObserved` | `VerifiedStillDetected` | shared comparison says `StillDetected` or `Changed` |

`Changed` remains unresolved because the target is still detected. A
`NewlyDetected` result never changes a source case; its new result finding is
independently `Open`. Identical duplicates retain separate cases and the exact
one-to-one pairing produced by `AuditComparisonEngine`.

## Persistence and reconciliation

`finding_resolution_cases` has one immutable identity per source finding and
retains the source audit and document-version lineage. It is not backfilled.
`finding_resolution_events` is append-only, has a monotonically increasing
per-case sequence, and a deterministic unique source-event key. Events retain
only resource IDs, controlled enum values, and timestamps from immutable source
resources. They contain no actual/expected JSON, text, filename, path, URL,
XML, snapshot, semantic key, or comparison fingerprint.

`POST /api/fix-executions/{executionId}/resolution-reconciliation` is bodyless.
The server derives selection, owner, source audit/version, result version,
canonical re-audit, snapshots, findings, and comparison classification. It
uses a serializable transaction; case identity, sequence, and source-event
unique constraints are final concurrency guards. Replay is safe, partial
failure rolls back the whole selected event set, and a unique/serialization
conflict retries by reading the canonical result. Pending returns 202; newly
persisted completed evidence returns 201; a completed replay returns 200.

The service reads the exact approved plan snapshot, immutable audit lineage,
and exact source/result rule snapshots. It never reads live `rules`,
`profile_rules`, `document_types`, current document version, storage, parser,
validator, or scoring configuration. Apply and re-audit completion do not
depend on this separately replayable observation.

## Read, authorization, and database boundary

`GET /api/audits/{auditId}/findings/{findingId}/resolution` returns the current
state, safe lineage IDs, bounded ascending events, count, and latest timestamp.
It uses owned, no-tracking queries. Unknown and foreign resources both return
404; authentication is required.

RLS derives ownership through case -> finding -> audit -> version -> document
owner. Authenticated browser clients have SELECT only. They cannot insert,
update, or delete cases/events. The backend/service role may insert; database
triggers reject case mutation and event update/delete, validate event payload
shape and immutable evidence lineage, and serialize sequence allocation.

The resolution events themselves are the canonical command evidence trail.
S4-T03 deliberately does not duplicate them into the generic operational
`audit_trail_events` catalog, avoiding a second event identity/source of truth.

Run `npm run test:finding-resolution` for the offline suite and
`npm run test:finding-resolution-local` against the local Supabase CLI stack.
The smoke is bounded and rerunnable, uses deterministic fixtures, does not
reset the database or delete volumes, and never connects to hosted Supabase.
