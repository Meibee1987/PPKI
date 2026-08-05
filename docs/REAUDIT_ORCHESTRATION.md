# Re-audit orchestration

S4-T01 queues one new audit for the `DocumentVersion` produced by a completed
`FixExecution`. It does not modify the source audit or its findings or resolve
findings. S4-T02 subsequently derives a read-only comparison from the completed
pair; it does not change this orchestration. There is no frontend change.

## API and ownership

`POST /api/fix-executions/{executionId}/re-audit` is authenticated and has no
request body. Owner, source/result version, source audit, profile, document
kind, rule rows, and resolved hash are server-derived. The ownership filter is
applied through fix execution -> source audit -> source version -> document
owner before materialization.

A first successful request returns HTTP 202 and the queued audit. A replay
returns HTTP 200 and the same canonical audit. Malformed execution IDs return
400. Unknown and foreign executions share the same 404. Invalid historical
state or context returns 409 with a controlled diagnostic code. The endpoint
does not parse a DOCX or run validators synchronously.

The response contains only audit/status/timestamp and lineage/context IDs plus
the resolved hash and document-kind snapshot. It excludes filenames, storage
paths, URLs, document text, XML, finding JSON, tokens, secrets, connection
details, and exception detail.

## Historical context and atomic creation

The source execution and source audit must both be `Completed`; the execution
must have a result version, and source/result versions must belong to the same
document. The new audit uses exactly:

- the execution's result `DocumentVersion`;
- the source audit's `ProfileVersion`;
- the source audit's `DocumentKindSnapshot`;
- every source `AuditRuleSnapshot`, preserving all immutable semantic fields
  and deterministic ordinal;
- the source audit's `ApplicableRuleCount` and resolved-rule-set hash.

The service reads source snapshot rows directly. It does not query live
`RuleDefinition`, `ProfileRule`, `DocumentType`, the rule catalog, or source
findings. The canonical hasher verifies the source rows and cloned rows against
the persisted source hash. The queued audit, all cloned snapshots, and
`audit.requested` event commit in one transaction; failure leaves no partial
audit or clone. The new audit starts with zero findings/counts and a null score.

## Lineage, idempotency, and worker lifecycle

`audit_jobs.source_audit_job_id` and
`audit_jobs.source_fix_execution_id` are nullable for ordinary/legacy audits.
For re-audits they are paired, immutable references. The audit's existing
`document_version_id` is the result-version relationship. A unique constraint
on `source_fix_execution_id` makes the completed execution the natural
idempotency identity, so retries and concurrent requests converge on one audit.

Database triggers verify the completed source chain, exact result version,
same document, requester, profile, document kind, hash, count, clean queued
state, exact snapshot set, and absence of copied findings. A deferred constraint
trigger makes audit and snapshot insertion atomic at commit. No historical
backfill is performed.

The existing audit worker claims the new job through the normal
`Queued -> Processing -> Completed|Failed` lifecycle. `AuditRunner` parses the
new audit's result version. For a lineage audit it reuses and verifies the
precloned snapshots instead of resolving live rules. Findings and counts are
new results with new identities; source audit and source findings remain
unchanged. A worker failure retains lineage and follows the existing safe
failure contract.

## Local verification and deferred scope

Run the focused offline suite with `npm run test:reaudit`. With the canonical
local Supabase stack running and the additive migration applied, run
`npm run test:reaudit-local`. The runtime smoke uses deterministic bounded
synthetic resources, calls the authenticated endpoint concurrently, verifies
snapshot/context equality, replay, RLS, trigger immutability, and worker claim,
then leaves the synthetic result audit terminal so a development worker cannot
claim it later. It does not read environment files, print credentials, use a
hosted Supabase project, or reset the database.

The derived before/after read model is documented in
[AUDIT_COMPARISON.md](AUDIT_COMPARISON.md). Ignore/accepted-risk behavior,
approval, and all UI work remain explicitly deferred.

S4-T03 observes this immutable canonical lineage through an independently
replayable reconciliation command. Queued/processing jobs produce pending
evidence and completed jobs are verified with the shared comparison engine.
Neither re-audit completion nor failure is rolled back by resolution
reconciliation. Approval, ignore, accepted risk, and UI remain deferred.
