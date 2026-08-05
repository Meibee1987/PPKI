# Deterministic audit comparison

S4-T02 exposes a derived, read-only comparison between the immutable findings
of a completed source audit and its completed resulting re-audit. It creates no
comparison rows, resolution state, audit event, document version, or DOCX
mutation. There is no frontend feature in this scope.

## Route and readiness

`GET /api/fix-executions/{executionId}/comparison` requires authentication. The
ownership predicate is applied through fix execution, source audit, source
document version, and document owner before materialization. Unknown and foreign
executions both return 404. A malformed route/filter/page returns a safe 400.
Incomplete execution/audit state, missing result audit, lineage mismatch, or
historical-context mismatch returns 409 with a bounded diagnostic code.

The execution and both audits must be `Completed`. The result audit must point
to the execution, source audit, and execution result version. Profile version,
`DocumentKindSnapshot`, resolved-rule-set hash, applicable-rule count, and every
semantic field of the ordered `AuditRuleSnapshot` set must match exactly.

## Identity, fingerprint, and pairing

The target identity is derived only from immutable snapshots: rule ordinal and
code, domain, validation key, element, canonical structural location, and the
actual/expected property name. Actual value and severity are deliberately not
part of this target identity. Finding UUID is only a last display tie-breaker
when all semantic data is identical and cannot alter aggregate classification.

Actual JSON is parsed with bounded length, depth, node, collection, and string
limits. Object properties are sorted ordinally, array order and JSON types are
preserved, and null remains different from empty. The canonical representation
is hashed with SHA-256 lowercase hex for internal pairing; neither JSON nor the
fingerprint is returned or logged.

Within each target group, exact actual fingerprints pair first as
`StillDetected`. Remaining items pair one-to-one in canonical order as
`Changed`; remaining source items become `NoLongerDetected`, and remaining
result items become `NewlyDetected`. Identical duplicates are never collapsed.
Insertion order, dictionary order, locale, and severity do not select a pair.

If a finding has neither a compact structural location nor a persisted
structural index, it is not considered safely pairable. Source/result findings
in that case are conservatively reported as `NoLongerDetected` and
`NewlyDetected`; no document-text or fuzzy heuristic is used.

## Response, filters, and ordering

The response contains lineage IDs, `comparisonState`, global summary,
pagination metadata, and safe before/after finding summaries. The summary has
source/result finding counts, counts for all four statuses, severity/domain
breakdowns, and source/result score state. Production scoring remains
`NotConfigured`/null; score delta is null unless both persisted interpretations
are numeric.

Optional exact filters are `status`, `severity`, `domain`, and `ruleCode`.
`sort` is omitted or `default`. Page defaults to 1, page size defaults to 25,
and page size is bounded to 1..100. Summary is always computed before filters
and pagination. Default order is status rank (`StillDetected`, `Changed`,
`NoLongerDetected`, `NewlyDetected`), rule ordinal, domain, validation key,
normalized location, rule code, then the final finding-ID display tie-breaker.

Before/after DTOs contain only snapshot labels, controlled reason/status fields,
structural source/location references, and allowlisted actual/expected display
fields. They exclude raw JSON, raw actual value, text, filename, storage path,
URL, XML, rule configuration, semantic key, fingerprint, stack trace, and
secrets.

## Historical isolation and verification

The service reads `FixExecutionJob`, source/result `AuditJob`,
`AuditRuleSnapshot`, and owned `AuditFinding` rows with no tracking. It does not
query live rules, profile rules, document types, current profile/version, rule
catalog/importer, parser, storage, or scoring policy, and never calls
`SaveChanges`.

Run `npm run test:audit-comparison` for the focused offline suite. With the
canonical local Supabase CLI stack active, run
`npm run test:audit-comparison-local`. The bounded smoke uses deterministic
synthetic lineage/findings and a loopback API, verifies owner/foreign behavior,
classification, duplicates, replay/order, global pagination summary, unchanged
row counts, and absent browser write privileges. It does not read `.env`, print
credentials, use hosted Supabase, or reset the database.

S4-T03 reuses the same public pure `AuditComparisonEngine` in-process to derive
resolution evidence. It does not duplicate pairing or call this GET route over
HTTP. `NoLongerDetected` maps to verified resolved; `StillDetected` and
`Changed` map to verified still detected; `NewlyDetected` does not mutate a
source case. The comparison endpoint remains read-only and unchanged.
