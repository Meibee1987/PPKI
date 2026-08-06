# Internal finding review

S4-T04 adds an internal, append-only review disposition beside the automatic
S4-T03 resolution state. It does not update an `AuditFinding`, score, audit,
rule snapshot, comparison, fix execution, DOCX, or `DocumentVersion`.

## Authorization

`public.user_profiles.role` is authoritative and maps exactly to the typed
roles `Student`, `Reviewer`, `PPKIAdmin`, and `UnitAdmin`. Token role claims,
email domains, names, frontend flags, and request data are not authorization
inputs. One reusable endpoint filter protects the entire `/api` business group
and queries `public.user_profiles.role` before invoking an operation. Only the
exact role `PPKIAdmin` is admitted. `Student`, `Reviewer`, `UnitAdmin`, missing,
and unknown roles fail closed across document, audit, fix, re-audit, comparison,
resolution, and review APIs. There is no reviewer assignment model or
role-management endpoint.

This small internal application deliberately uses operational self-approval,
not separation of duties. A `PPKIAdmin` may request, decide, and report manual
remediation for a finding from their own document. Every action still uses the
authenticated actor ID and an append-only event, with the same idempotency and
concurrency contract.

All exact database-role PPKIAdmin accounts share internal business resources.
`owner_user_id` remains immutable provenance for document lineage, storage
paths, and audit metadata; it is not an authorization boundary between Admin A
and Admin B. No assignment or ownership transfer is required.

## Separate states

The read model returns both `resolutionState` and `reviewState`. Resolution is
one of `Open`, `Applied`, `ReauditPending`, `VerifiedResolved`, or
`VerifiedStillDetected`. Review is independently `NoReview`, `PendingReview`,
`NeedsRevision`, `ManualRemediationApproved`, `ManualRemediationReported`,
`Rejected`, `Ignored`, or `AcceptedRisk`.

`Ignored`, `AcceptedRisk`, and `ManualRemediationReported` are not verified
resolution. They do not remove findings or change counts, severity, score, or
comparison results. A new request is rejected after S4-T03 has produced
`VerifiedResolved`; earlier review history remains readable.

## Commands and transitions

All command routes require an `Idempotency-Key` UUID. Actor identity comes from
the authenticated principal. Notes are optional, trimmed, at most 1,000
characters, and reject control characters.

- `POST /api/audits/{auditId}/findings/{findingId}/review-requests` accepts
  `ManualRemediation`, `Ignore`, or `AcceptedRisk` from a `PPKIAdmin`.
- `POST /api/finding-reviews/{reviewCaseId}/decisions` accepts the matching
  PPKIAdmin decision, including self-review. Cross-decisions are rejected.
- `POST /api/finding-reviews/{reviewCaseId}/manual-remediation-reports` is
  available to `PPKIAdmin` after `ManualRemediationApproved`.
- `GET /api/audits/{auditId}/findings/{findingId}/review` is read-only and
  returns both state dimensions, bounded history, and server-derived permissions.

Transitions are `NoReview -> PendingReview`; pending may become approved,
ignored, accepted risk, needs revision, or rejected according to the requested
disposition; `NeedsRevision -> PendingReview`; and manual approval may become
manual reported. Rejected, ignored, accepted risk, and manual reported are
terminal in S4-T04.

## Persistence and verification

`finding_review_cases` has one immutable case per historical finding.
`finding_review_events` is append-only with unique per-case sequence,
idempotency identity, and deterministic source-event key. RLS uses
`public.is_ppki_admin()` and permits only an exact database-role PPKIAdmin to
read; authenticated browser writes are denied.
Database predicates, triggers, and the application authorization service all
read the exact database role. No historical backfill is performed.

## Account provisioning

Local versioned Supabase configuration sets `enable_signup = false`, the login
page has no registration link, and `/signup` contains no sign-up operation.
Accounts are created manually by a trusted operator and provisioned with
`user_profiles.role = 'PPKIAdmin'` outside the application. For hosted Supabase,
an operator must disable new-user signup in the hosted project's Auth settings
and verify an anonymous `signUp` attempt is rejected before deployment. The
exact dashboard control is hosted operational state and cannot be enforced by
this repository. Never expose the service-role credential to the browser, and
do not add an application role-elevation endpoint.

Migrations `202608050003_admin_only_internal_access.sql` and
`202608050004_remove_legacy_no_self_review_predicate.sql` are additive because
`202608050002_finding_review_workflow.sql` was already applied. It replaces the
stored no-self-review trigger logic and authenticated RLS predicates, then
removes the obsolete non-owner helper without a reset, backfill, or edit to an
applied migration.

Migration `202608060001_shared_ppki_admin_access.sql` replaces the remaining
owner-scoped authenticated SELECT policies with the same exact
`public.is_ppki_admin()` predicate. It does not change historical ownership,
grant browser writes, or alter an older migration.

Run `npm run test:finding-review` offline and
`npm run test:finding-review-local` twice against local Supabase. The smoke is
deterministic, bounded, and non-destructive.

S4-T05 adds no review UI or transition. Ignore, accepted risk, and manual
disposition neither hide nor mutate a Fix-execution failure and remain
independent of automatic resolution evidence.
