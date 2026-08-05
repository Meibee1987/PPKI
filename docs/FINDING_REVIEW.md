# Internal finding review

S4-T04 adds an internal, append-only review disposition beside the automatic
S4-T03 resolution state. It does not update an `AuditFinding`, score, audit,
rule snapshot, comparison, fix execution, DOCX, or `DocumentVersion`.

## Authorization

`public.user_profiles.role` is authoritative and maps exactly to the typed
roles `Student`, `Reviewer`, `PPKIAdmin`, and `UnitAdmin`. Token role claims,
email domains, names, frontend flags, and request data are not authorization
inputs. An owner may request review and report manual remediation after an
approval. Only `PPKIAdmin` may decide a finding owned by another user. A
`PPKIAdmin` cannot decide a finding from their own document. `Reviewer`,
`UnitAdmin`, missing, and unknown roles fail closed and have no S4-T04 decision
permission. There is no reviewer assignment model or role-management endpoint.

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
  `ManualRemediation`, `Ignore`, or `AcceptedRisk` from the owner.
- `POST /api/finding-reviews/{reviewCaseId}/decisions` accepts the matching
  PPKIAdmin decision. Cross-decisions are rejected.
- `POST /api/finding-reviews/{reviewCaseId}/manual-remediation-reports` is
  owner-only after `ManualRemediationApproved`.
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
idempotency identity, and deterministic source-event key. RLS permits the owner
and a non-owner PPKIAdmin to read; authenticated browser writes are denied.
Database predicates, triggers, and the application authorization service all
read the exact database role. No historical backfill is performed.

Run `npm run test:finding-review` offline and
`npm run test:finding-review-local` twice against local Supabase. The smoke is
deterministic, bounded, and non-destructive.
