# Fix-plan preview

S3-T03 adds a versioned, deterministic remediation-planning contract and the
authenticated `POST /api/audits/{auditId}/fix-plan-preview` computation. POST
is used only for structured selection input. The operation does not persist a
plan, change a finding or audit, access storage, load or mutate a DOCX, create a
`DocumentVersion`, or start a re-audit. There is no Apply endpoint.

## Selection and ownership

The request contains only finding selection:

```json
{ "findingIds": ["00000000-0000-0000-0000-000000000001"] }
```

At least one and at most 100 non-empty UUIDs are accepted. Duplicate IDs are
normalized and sorted, so client order and duplicates do not change the plan.
The client cannot supply owner, expected value, target, operation kind,
validation key, document version, or rule metadata.

The database selects only requested findings. It joins each finding to its
`AuditRuleSnapshot` by audit and rule code, then filters through audit,
immutable document version, document, and authenticated owner before
materialization. Queries use no tracking. Missing/foreign findings and another
user's resources follow the same safe 404 contract. Invalid input returns 400
Problem Details with a stable code. Authentication comes from the `/api` group.

## Historical input and runtime capability

Input is restricted to `AuditJob`, `AuditFinding`, `AuditRuleSnapshot`,
immutable `DocumentVersion` identity/SHA-256, and the explicit runtime
remediation registry. `DocumentKindSnapshot`, validation key, domain, element,
severity, fix mode, ordinal, and resolved-rule-set hash remain historical. The
planner never reads live `RuleDefinition`, profile-rule mapping, current
document type, validators, parser, storage, or frontend-supplied expected data.

`FixMode` is metadata, not proof that remediation exists. An explicitly
registered `RemediationCapability` declares capability ID/version, validation
key, operation kind, required snapshot fields, confirmation requirement,
whether a mutation implementation exists, preview-provider ID, safe description
code, and merge permission. Registration is explicit and ordinal; duplicate
keys and invalid descriptors fail with controlled codes. There is no reflection
or assembly scanning.

The production registry is intentionally empty because no safe formal provider
exists. Production findings are therefore `Unsupported`, previews are
`NotAvailable`, and existing findings action availability remains `None`.
Runtime capability may change in a later deployment; it is not a mutation of a
historical finding. No capability was invented from `Auto` or `Confirm`.

## Versioned result

Planner version is `fix-plan-preview/1.0`. A preview returns audit/source
version identity and SHA-256, resolved-rule-set hash, document-kind snapshot,
planner version, counts, ordered items, typed operations, conflicts, state,
stable diagnostics, and a lowercase SHA-256 plan hash.

Item dispositions are:

- `Planned`: a registered provider produced a valid typed operation.
- `Unsupported`: no capability is registered for the validation key.
- `Conflict`: operations target one semantic property incompatibly; none wins.
- `InvalidSnapshot`: persisted or provider-required typed data is invalid.

States are:

- `Ready`: all items are planned without conflict.
- `PartiallyReady`: a plan exists alongside unsupported or invalid items.
- `NotAvailable`: no selected item has a usable capability.
- `InvalidSnapshot`: invalid inputs prevent every operation.
- `Conflict`: at least one semantic target conflicts; HTTP remains 200.
- `AuditIncomplete`: the audit is not `Completed`.
- `InvalidSelection` and `InvalidConfiguration` are formal states whose API
  boundary failures return safe 400 Problem Details instead of a partial plan.

An operation contains only its kind, capability ID/version, rule/validation
identity, source finding IDs, typed structural location, property identifier,
bounded allowlisted expected-value type, confirmation flag, ordinal,
precondition code, and summary code. It contains no delegate, internal type,
raw arbitrary JSON, document text, or mutation implementation. Preview does not
promise that a future Apply will succeed.

## Ordering, merge, conflict, and hash

Items use persisted rule ordinal, rule code, structural location, then finding
ID as final tie-breaker. Operations/conflicts use semantic-target ordering.
Findings are never deduplicated by rule code; different locations remain
different operations. Identical target and meaning merge only when all matching
capabilities explicitly permit it and capability ID/version agree. All source
finding IDs remain. Different expected descriptors or incompatible kinds at
the same scope/location/property conflict. Severity and rule ordinal never
choose a winner.

The canonical hash projection contains planner version; audit/source identity
and SHA-256; resolved-rule-set hash; document-kind snapshot; ordered finding
identities plus canonical actual/expected/location digests; dispositions;
ordered capability IDs/versions and operations; conflicts; and state. Object
properties and arrays are deterministic, and location numbers use invariant
culture. Time, random IDs, insertion order, human exception messages, and live
rules are excluded. Capability, expected, source, or rule-set version changes
change the hash.

Selected JSON is limited to 16,384 characters per field and depth 8. Responses
contain no document/paragraph text, filename, storage path, signed URL, raw
XML/DOCX, stack trace, dependency exception, token, or service-role data. The
feature makes no network call and writes no plan-body log.

Run `npm run test:fix-plan-preview` for focused verification.

Still deferred: preview UI, Confirm workflow, Apply Fix, DOCX mutation, new
document version, re-audit, rollback, export, manual/ignore workflow, and
lecturer review.

S4-T06 mengonsumsi exact preview ini pada halaman audit. Eligibility berasal
dari disposition item server, bukan FixMode. Perubahan selection membatalkan
preview, dan parser browser menyimpan count/hash/state tetapi membuang payload
operation. Lihat [REMEDIATION_UI.md](REMEDIATION_UI.md).
