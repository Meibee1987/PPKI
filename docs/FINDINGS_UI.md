# Audit findings UI

S3-T02 adds an authenticated, read-only presentation of the S3-T01 audit read
model. It does not run validators, calculate scores, change finding state, or
modify a DOCX.

## Routes and data flow

- `/audits/[auditId]` shows the audit summary and paginated findings.
- `/audits/[auditId]/findings/[findingId]` loads one finding from the nested
  detail endpoint. Query parameters are retained on the back link.
- The document detail page links to the latest audit by its returned audit ID.

`GET /api/documents/{id}` formally projects document versions by descending
`VersionNo` and audits within each version by descending immutable `CreatedAt`.
The EF `Include` only materializes the related collections; the ordering is
applied explicitly by the response projection. Because grouping newest
versions first does not prove which audit is newest across every version, the
document page does not depend on `flatMap(...).at(0)`. It selects the greatest
valid audit `CreatedAt` across all returned versions, with audit ID as the
deterministic tie-breaker. Contract tests lock both the API projection and the
order-independent frontend selection.

Both routes are protected by the existing Supabase SSR session proxy. Client
requests obtain the current Supabase access token and send it to the ASP.NET
Core API. No owner ID is accepted from the route, and the backend remains the
authorization boundary. A missing audit and another user's audit both receive
the same safe not-found presentation.

Protected route matching uses exact path segments: `/documents`,
`/documents/...`, `/audits`, and `/audits/...`. Similar prefixes such as
`/audits-other` are public-path matches rather than audit routes, while
`/auth/callback` remains outside the protected application routes.

The typed client validates controlled audit status, severity, fix mode, score
state, and action availability values at runtime. Invalid response shapes fail
with a controlled message. API error bodies contribute only a bounded stable
problem code; titles, details, and exception data are not surfaced.

## Summary and audit states

The summary presents audit status, applicable and persisted-finding counts,
severity/domain/fix-mode totals, timestamps, document-kind snapshot, and a
compact copyable resolved-rule-set hash. Queued, Processing, Failed, Cancelled,
and Completed states have distinct text. There is no automatic polling or
retry/re-audit action; a user can reload the page when a running audit changes.

Score rendering uses the backend state and value without recalculation:

- `Calculated` shows the supplied number and policy version.
- `NotConfigured` says that scoring is not configured and shows no invented
  zero, 100, progress bar, or percentage.
- `AuditIncomplete`, `InvalidConfiguration`, and `NotApplicable` have separate
  non-numeric messages.

## Filters and pagination

The supported URL parameters are `severity`, `fixMode`, `domain`, `ruleCode`,
`validationKey`, `page`, and `pageSize`. Text values remain exact filters.
Applying a filter resets `page` to 1. Clear-all restores page 1 and the API
default page size 25. Page size is bounded to 100. Unknown enums, overlong text,
and invalid pagination values are ignored and normalized to safe defaults.

Only the requested page is fetched. Items are rendered in backend order and
are never merged, re-sorted, or filtered in the browser. Empty compliant
audits, empty filtered results, and stale empty pages have different messages.

## Finding presentation and privacy

Each list item shows rule code, domain, textual severity, fix mode, reason,
location, bounded actual/expected fields, action availability, and a link to
the detail endpoint. `None` is shown as no available action and never creates a
fix button.

Location indexes are retained unchanged in data but displayed one-based as
Bagian, Elemen dokumen, Paragraf, and Segmen format. A location with no indexes
is displayed as `Dokumen`. Partial or unknown locations use a safe fallback;
page numbers and section titles are never inferred.

Actual and expected values use a depth- and item-bounded definition list. Known
fields receive human-readable labels, safe unknown scalar fields have a bounded
fallback, strings are truncated, and null remains distinct from zero or false.
Keys for text, title, filename, path, URL, XML, content, stack, and exception
data are suppressed at every supported depth. The UI uses no raw JSON dump,
HTML injection, analytics/logging, browser storage, live rule catalog, database
connection, storage path, or signed URL.

## Accessibility and responsive behavior

Pages use heading hierarchy, semantic sections/lists/definition lists, visible
focus, form labels, textual status/severity, keyboard-native links and buttons,
bounded live regions, and accessible pagination labels. Copy feedback is
announced. Summary and filter grids collapse at tablet and mobile breakpoints;
finding comparison panels and metadata become a single column, long identifiers
wrap, and pagination remains inside the viewport.

## Verification

Run the focused pure Node tests without a hosted Supabase connection:

```powershell
npm run test:findings-ui
```

Also run web configuration tests, typecheck, production build, S3-T01/backend
regression commands, repository hygiene checks, and `npm run verify`.

Not included: auto-fix, Confirm preview, manual/ignore workflow, re-audit,
scoring-policy configuration, export, lecturer review, or finding-resolution
state mutation.
