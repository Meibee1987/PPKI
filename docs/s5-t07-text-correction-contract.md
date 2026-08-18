# S5-T07 text correction contract

Detector identity is `ppki-text-correction-detector/1.0`; its immutable server catalog is
`ppki-text-correction-catalog/1.0`. Detection uses exact ordinal matching plus Unicode
letter/digit/mark token boundaries. Search is confined to the detector. Every detected
occurrence is immediately converted to `text-anchor/1.0`; decision, context, planning, and
apply never relocate by text.

Correction proposals are purpose-specific evidence rather than `AuditFinding`. This avoids
changing immutable rule snapshots, public rules, scoring, parser schema, or formatting
AutoApply classification. A proposal stores source audit/version/SHA, detector/catalog rule,
typed coordinate/fingerprint anchor JSON, suggestion, and hashes. It has no source sentence,
paragraph, target phrase, context, or raw XML column.

`UseSuggestion`, `EditManual`, and `Ignore` are append-only events. Only `EditManual` stores
the exact validated replacement; `UseSuggestion` references the proposal suggestion hash and
`Ignore` carries no replacement. The latest sequence is effective. Authoritative
`public.user_profiles.role = 'PPKIAdmin'` is enforced by server authorization and RLS; admins
share the same evidence.

A batch contains at most 100 latest accepted decisions from one current audit/version. Its
FixExecution snapshot is reference-only (`decisionId`, `anchorHash`, `replacementHash`). The
worker resolves purpose-specific evidence server-side, resolves every anchor against the
original source, rejects overlaps and incompatible run/hyperlink semantics, then uses
`text-exact-replacement/1.0`. It never performs search or global replacement. Equivalent
multi-run formatting/container semantics are supported; incompatible semantics and hyperlink
boundary crossings fail closed. Nodes and relationships are preserved.

The existing hardened FixExecution publication path creates exactly one child
`DocumentVersion`, advances the current pointer transactionally, and queues the existing
versioned renderer/page map. A durable lifecycle worker creates one canonical re-audit, runs
the same detector on the result version, and verifies each operation by translated structural
paragraph/start lineage. Old proposals and anchors remain bound to their old version.

Transient context is separately authorized and bounded by `text-correction-privacy/1.0`.
Bulk lists are DB-paginated and contain no excerpts. Logs, safe failures, metrics labels,
storage keys, and generic FixExecution JSON do not contain source context or replacement text.
