# Text correction privacy contract

This document is normative for `text-correction-privacy/1.0`. “MUST”, “MUST
NOT”, and “MAY” define requirements for S5-T07 and later work. S5-T06 creates
no detector, suggestion generator, correction command, public endpoint, text
mutation, or correction database row.

## 1. Classification and persistence matrix

All six classes are restricted PPKI business data even when they are derived or
hashed.

| Class | Meaning | Persistence rule |
| --- | --- | --- |
| `SourceText` | Text already present in the source DOCX | MUST NOT be duplicated in correction persistence |
| `SourceExcerpt` | Bounded target/context returned for display | Transient response only; MUST NOT enter durable DB, cache, log, or telemetry |
| `SuggestedReplacement` | Future provider-generated replacement | MAY exist only in purpose-specific, bounded proposal evidence |
| `AdminReplacement` | Future admin-authored executable intent | MAY exist only in a purpose-specific append-only intent/event |
| `AnchorEvidence` | S5-T05 hashes, coordinates, version, and spans | MAY be persisted without source text |
| `DerivedMetadata` | IDs, rule/category, status, page, confidence, safe diagnostics | MAY be persisted when bounded and purpose-specific |

`SourceText`, full documents, paragraphs, sentences, and unbounded excerpts
MUST NOT be stored in `AuditFinding.ActualValueJson`, `ExpectedValueJson`,
`LocationJson`, recommendation fields, review notes, resolution events,
approved-plan JSON, fix diagnostics, orchestration records, page-map records,
logs, exceptions, metrics, object keys, or URLs. The immutable
`DocumentVersion` remains the canonical source.

Generic review notes and resolution metadata MUST NOT be repurposed for source
excerpts, suggestions, or manual replacements. Purpose-specific storage is
mandatory.

## 2. Transient source excerpts

An authorized read MAY derive an excerpt on demand from the exact immutable
DOCX using `text-anchor/1.0`. The transient response contains exact target text,
a bounded surrounding context, prefix/suffix truncation flags, anchor identity,
lineage IDs, and optional page number.

- target: maximum 256 Unicode scalar values;
- context outside the target: maximum 512 scalars, split deterministically
  between prefix and suffix;
- combined duplicated target-plus-context response content: maximum 1,024
  scalars;
- slicing MUST occur on scalar boundaries, never through a surrogate pair;
- the result MUST NOT be persisted, durably cached, or automatically rendered
  through `ToString()`/JSON into logs.

The internal materialization service verifies authoritative authorization,
DocumentVersion ID, source SHA-256, structural location, paragraph/target/context
fingerprints, and source spans. It never searches or relocates. It opens the
package read-only and checks the SHA again after inspection.

## 3. Suggested replacement evidence

A future suggestion MAY be persisted only in a dedicated proposal entity with:

- contract version;
- exact AuditJob, AuditFinding, and DocumentVersion IDs;
- exact anchor hash and anchor contract version;
- provider ID and version;
- validated replacement value and scalar length, bounded to 256 scalars;
- deterministic proposal identity;
- generated state and evidence timestamp.

It MUST NOT contain the source target, sentence, paragraph, document text, or a
generic JSON copy of them. The semantic proposal identity uses the domain
`ppki:text-correction-proposal:v1` and excludes timestamps/randomness.

## 4. Admin replacement intent

A future accepted suggestion or manual edit MAY be persisted only as an
append-only purpose-specific intent/event containing exact finding and source
DocumentVersion IDs, anchor hash, validated replacement value, actor PPKIAdmin,
idempotency key, decision evidence timestamp, and contract version. It MUST use
`ON DELETE RESTRICT`, immutable historical semantics, and canonical uniqueness.
It MUST NOT contain source text or a source excerpt.

The semantic intent identity uses `ppki:text-correction-admin-intent:v1` and is
bound to finding, source version, anchor, actor, idempotency key, and replacement
fingerprint. S5-T06 does not create this table or accept this intent through an
API.

## 5. Replacement validation and Unicode

Allowed replacement text preserves the exact Unicode scalar sequence; no NFC,
NFD, case, whitespace, or compatibility normalization is performed. A non-BMP
character counts as one scalar. Maximum length is 256 scalars.

Null, empty, whitespace-only, unpaired surrogates, NUL/C0/C1 controls, tabs,
CR/LF, Unicode line/paragraph separators, bidi controls, and over-limit values
MUST fail closed. Paragraph-breaking replacement is unsupported. The public
failure remains `correction-replacement-invalid`; sensitive input MUST NOT be
included in errors.

## 6. Hashing and identifiers

Hashes use SHA-256 over UTF-8, explicit domain separation, and byte-length-
prefixed canonical fields. Defined domains are:

- `ppki:text-correction-replacement:v1`;
- `ppki:text-correction-proposal:v1`;
- `ppki:text-correction-admin-intent:v1`.

Serialization is fixed-order and culture-invariant. Semantic hashes exclude
time, randomness, and implicit platform formatting. Raw source/replacement text
MUST NOT appear in URLs, route parameters, storage/object paths, metric labels,
span names, correlation IDs, safe codes, or exception messages. A hash is
evidence, not a reversible substitute or permission to retain unnecessary text.

## 7. Authorization

Correction evidence and transient context are available only when the exact
database-authoritative `public.user_profiles.role` is `PPKIAdmin`. Admin A and
Admin B share access. Student, Reviewer, UnitAdmin, missing, unknown, and
spoofed token-claim roles are denied. Authorization MUST use the existing
`IInternalAdminAuthorizationService` path and MUST occur before materialization.
Missing and unauthorized resources retain the existing non-enumerating API
convention. No public correction endpoint exists in S5-T06.

## 8. Page maps, failures, and logging

The optional page number comes from version-specific `page-map/1.0` data and is
presentation metadata only. An unavailable page does not invalidate an exact
anchor. A stale anchor cannot be repaired using a page number.

Allowed safe failures are:

- `correction-anchor-stale`;
- `correction-anchor-unsupported`;
- `correction-context-unavailable`;
- `correction-replacement-invalid`;
- `correction-evidence-conflict`.

Logs and telemetry MAY contain IDs, states, counts, safe codes,
provider/version IDs, durations, and justified hash identifiers. They MUST NOT
contain source excerpt, target, suggestion, admin replacement, paragraph text,
or automatic serialization of correction DTOs. Exception content MUST remain
text-free.

## 9. Historical and retention semantics

Future proposal and intent evidence is immutable/append-only and remains tied
to its exact DocumentVersion and anchor. Creating v2 MUST NOT update, relocate,
or migrate v1 evidence. Old evidence remains historical; v2 requires a new
anchor and proposal. Deletes of referenced audit, finding, or version rows MUST
be restricted when purpose-specific persistence is introduced.

## 10. Database decision and S5-T07 handoff

No migration is justified in S5-T06 because no proposal or admin intent is
created yet. Adding an unused generic text table would increase disclosure risk.
S5-T07 MUST introduce an additive, purpose-specific schema only when it creates
proposal evidence, with PPKIAdmin-only RLS, database-authoritative role checks,
bounded checks, append-only triggers, restricted foreign keys, deterministic
identity/idempotency, and no raw source duplication.

Before S5-T07 exposes any command or read endpoint, it MUST reuse the shared
replacement validator and transient context service, prove non-enumerating
authorization, add log guards, and demonstrate that every mutation creates a
new `DocumentVersion`. It MUST NOT trust client-created offsets, anchor fields,
provider identity, or replacement target selection.
