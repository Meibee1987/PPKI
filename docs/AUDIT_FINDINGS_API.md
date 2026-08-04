# Audit findings read API

S3-T01 exposes authenticated, read-only audit results. These endpoints do not
re-run validators, mutate a DOCX, create a document version, or offer a fix.

## Audit summary

`GET /api/audits/{auditId}` returns the audit status, immutable document and
profile version references, `DocumentKindSnapshot`, resolved-rule-set hash,
applicable-rule count, persisted finding count, and counts grouped by severity,
domain, and fix mode. Counts are aggregated from persisted audit findings joined
to persisted rule snapshots; the live rule catalog is not consulted.

The response also contains `scoreState`, nullable `score`, nullable
`scorePolicyVersion`, and nullable `scoreBreakdown`. Until an explicit scoring
policy version is persisted with an audit, `scoreState` is `NotConfigured` and
the numeric fields are null. Failed audits expose only the stable code
`audit-processing-failed` and the safe message `Audit processing failed.`;
stored dependency or exception detail is never returned.

## Findings list

`GET /api/audits/{auditId}/findings` accepts these optional exact filters:

| Parameter | Values |
| --- | --- |
| `severity` | `Error`, `Warning`, or `Info` (case-insensitive) |
| `fixMode` | `Auto`, `Confirm`, `Manual`, or `Report` (case-insensitive) |
| `domain` | exact persisted snapshot domain |
| `ruleCode` | exact persisted rule-code snapshot |
| `validationKey` | exact persisted validation-key snapshot |
| `sort` | omitted or `default` |
| `page` | at least 1; default 1; offset must remain below the 10,000-finding cap |
| `pageSize` | 1 through 100; default 25 |

Unknown enum/sort values, overlong text filters, and out-of-range pagination
return HTTP 400 Problem Details with a stable `code`. The query supplies
`page`, `pageSize`, `totalCount`, and `items`. Ownership, filtering, and count
are executed by the database. The query materializes at most the formal 10,000
finding cap for that owned, filtered audit. Numeric structural ordering and
pagination then run over that bounded set. Actual and expected JSON are
deserialized only for the selected result page.

Default ordering is persisted rule-snapshot ordinal, severity rank
(`Error`, `Warning`, `Info`), domain, location category, numeric body-element
index, numeric section index, numeric paragraph index, numeric run index,
compact location, rule code, and finding ID. Document-level locations (all four
numeric indexes null) use category 0 and precede element-level category 1.
Within element-level locations, a missing index sorts before a present index at
that hierarchy level; numeric comparison ensures 2 and 9 precede 10, and 10
precedes 11. Strings use ordinal comparison. Finding ID is only the final
tie-breaker. Pagination is applied after this total ordering, so pages are
repeatable for an immutable audit without relying on insertion or raw JSON
ordering. No migration or new index is required for the bounded MVP path.

Action-availability and location-kind filters are not exposed because neither
is a persisted formal field. Every returned item reports action availability
as `None`; S3-T01 does not infer an action from `FixMode`.

## Fix-plan preview

`POST /api/audits/{auditId}/fix-plan-preview` accepts a read-only computation
request containing 1 through 100 finding UUIDs. Duplicate IDs are normalized;
request order is not meaningful. Historical finding/rule/audit snapshots are
the only plan data, and the ownership-filtered query selects only requested
findings before materialization. Missing, foreign-audit, and foreign-owner
selections return the same safe 404 contract. Invalid input returns 400 Problem
Details. A semantic conflict is HTTP 200 with state `Conflict`.

The endpoint does not infer support from `FixMode`. The production capability
registry is intentionally empty until a formal provider exists, so current
previews are `NotAvailable` and finding action availability stays `None`. It
never saves a plan, accesses storage, downloads or mutates a DOCX, creates a
version, or exposes Apply. See [FIX_PLAN_PREVIEW.md](FIX_PLAN_PREVIEW.md).

## Finding detail

`GET /api/audits/{auditId}/findings/{findingId}` returns the finding and audit
IDs, safe document-version reference, persisted ordinal/rule/domain/validation
key/element, severity/fix-mode/source snapshots, finding state, reason/message
code, safe
actual/expected/location JSON, confidence, and action availability `None`.
The nested query requires the finding to belong to the route audit.

All three routes traverse audit to document version to document owner in the
database query before materialization. A missing resource and another user's
resource both return 404. Authentication remains required by the existing
`/api` route group, with database RLS retained as defense in depth.

Responses never add document/paragraph text, filename, storage bucket/key,
signed URL, raw XML, parser diagnostics, stack trace, or exception details.
Historical values come only from `AuditFinding`, `AuditRuleSnapshot`, and
`AuditJob`; live `RuleDefinition` data cannot replace them.

Not included: preview UI, Confirm workflow, Apply Fix, DOCX mutation, new
document version, ignore/manual review workflow, re-audit, rollback, export,
or lecturer review.

## Frontend consumer

The S3-T02 read-only UI consumes these endpoints at `/audits/[auditId]` and
`/audits/[auditId]/findings/[findingId]`. List filters and pagination are stored
in URL query parameters and passed through to this API without client-side
re-sorting. See `docs/FINDINGS_UI.md` for presentation and privacy boundaries.
The document-detail handoff uses the API's explicit per-collection ordering but
selects the greatest audit `CreatedAt` across versions, so the latest-audit link
does not accidentally depend on nested array insertion order.

## Before/after comparison

`GET /api/fix-executions/{executionId}/comparison` derives a deterministic,
read-only comparison from a completed source audit and completed resulting
re-audit. It exposes the separate statuses `StillDetected`, `Changed`,
`NoLongerDetected`, and `NewlyDetected`; these are not persisted resolution
states. Raw actual/expected JSON and internal identity/fingerprint values are
never returned. See [AUDIT_COMPARISON.md](AUDIT_COMPARISON.md) for readiness,
pairing, duplicate, filter, pagination, historical-isolation, and privacy
contracts.
