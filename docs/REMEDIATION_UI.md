# Remediation workflow UI

S4-T06 adds the internal PPKIAdmin remediation workspace to the existing audit
route. It changes no backend endpoint, state transition, rule, capability,
parser, score, or database schema. Authorization remains the exact
database-authoritative PPKIAdmin filter; the browser never reads a role claim,
sends an actor ID, or treats visibility as a security boundary. PPKIAdmin
accounts share the same business resources under the S4-T04 contract.

## User flow

`/audits/{auditId}` retains the bounded server-paginated finding list. Its
checkboxes are honestly labeled as remediation candidates; the list does not
infer capability from FixMode, rule code, severity, or expected text. The
explicit FixPlan preview is the final server-authoritative eligibility decision
for the exact selection and displays rejected/unsupported/conflicting items.
Selection is limited to the active audit and 100 IDs and resets when the audit
changes. A selection change invalidates its preview.

The explicit preview shows only counts, state, and safe diagnostics. Operations
and approved-plan JSON are discarded by the typed parser. Apply requires a
keyboard-accessible confirmation explaining that a new immutable
DocumentVersion will be created and re-audit is still required. The body
contains only exact finding IDs and plan hash. One ephemeral UUID idempotency
key represents one user intent and is retained for a lost-response retry; a new
preview/intent receives a new key. There is no optimistic completion.

The execution monitor has one non-overlapping bounded poller, stops at a
terminal state, aborts on unmount, refreshes when the tab becomes visible, and
backs off after network failures. It presents attempts, automatic retry, lease
state, safe category/code, result version, and the five canonical states. It
has no manual retry or cancel action. Safe failure codes map to Indonesian
messages; unknown codes use generic text and raw errors, paths, URLs, object
keys, filenames, claim tokens, plans, and exceptions are never rendered.

A completed execution with a result version enables the existing canonical
re-audit command. The UI polls that audit, then reads the deterministic
comparison and asks the existing reconciliation endpoint to refresh resolution
evidence. The four comparison groups are displayed exactly as returned by the
server; the browser performs no matching. Failed, Processing, Queued, and
result-less NoChange executions cannot start re-audit.

## Finding detail and review

The finding detail remains lazy and historical. Page or line is shown only if
supplied by the backend; structural indexes remain structural positions, and
absent data says “Lokasi rinci tidak tersedia”. The browser never downloads
DOCX content or invents a target phrase.

Resolution and review are separate cards. `Applied` is not verified, and
`Ignored`, `AcceptedRisk`, and `ManualRemediationReported` explicitly remain
non-verified dispositions. Request actions use the three documented request
types; decision actions and report availability come from server permissions
and `allowedDecisions`. Notes are React text, limited to 1,000 characters,
linked to an accessible counter/error, and never logged. Commands require
confirmation. Canonical state is reloaded after every command or HTTP 409.

## Errors, accessibility, performance, and privacy

HTTP 401 follows the existing login redirect. HTTP 403 is an access-denied
state, 404 is non-enumerating, 409 invalidates stale preview and reloads state,
and 5xx uses generic retry copy. Dialogs have a title/description, native
keyboard handling, visible focus, and restore focus to their trigger. Statuses
have text and asynchronous state uses bounded `aria-live` announcements.

Finding data stays server-paginated at at most 100 rows per request. Preview is
sent only for an explicit selection, detail/history loads only when opened,
previous requests are aborted, and polling follows only the active execution
and re-audit. No response body, note, finding text, or plan is stored in browser
persistence. Desktop, tablet, and mobile layouts are supported.

The backend has no endpoint that lists every historical FixExecution for an
audit. The UI monitors the canonical execution created in the current workflow
and can discover related IDs through a finding resolution read model, but does
not invent a complete execution-history feed.

## Verification

Run `npm run test:remediation-ui` for typed/parser/presentation/architecture
tests. `npm run test:remediation-ui-local` is explicitly an API-backed local
integration aggregate; it is not browser E2E. It exercises local hardening,
re-audit, comparison, resolution, review, shared-admin, non-admin, auth, and
privacy contracts without reset or hosted Supabase.

Manual browser verification: **pending** (browser connector was unavailable and
was not retried). The required checklist is:

1. Login with an existing PPKIAdmin account.
2. Open an audit with many findings.
3. Navigate list and detail using only the keyboard.
4. Preview selected findings and inspect planned plus rejected/ineligible items.
5. Verify confirmation-dialog focus trap and focus return.
6. Apply the exact plan once and check rapid double-click does not duplicate it.
7. Monitor execution through its canonical states.
8. Inspect failure rendering when a safe failure fixture is available.
9. Start canonical re-audit from an eligible completed execution.
10. Inspect all four server-derived comparison groups.
11. Confirm Ignore and Accepted Risk commands.
12. Verify a review note over 1,000 characters is rejected.
13. Exercise refresh and tab concurrency, including stale-state HTTP 409 handling.
14. Repeat the workflow at a tablet viewport.
15. Perform a screen-reader and status-announcement sanity check.

There is no rollback, partial apply, manual retry/cancel, client DOCX editor,
new capability, signup operation, role management, or direct Storage access.
