# Deterministic Wave 1 layout validators

S2-T05 validates the nine layout keys selected by the rule catalog importer.
S2-T06 reuses the same registry, finding, ordering, deduplication, limit, and
snapshot contracts for the supported structural keys documented in
`docs/WAVE1_STRUCTURAL_VALIDATORS.md`. Layout validator results are unchanged.

## Rule and snapshot inventory

The 317-entry `rules/ppki-ipb-2019/rules.json` is source data and intentionally
does not contain `validation_key` or executable validation parameters.
`RuleCatalogImporter` maps nine layout rule codes to stable keys. S2-T06 adds
separate HDG/ABS mappings without changing any layout mapping or `rules.json`.
All unmapped entries receive `manual.not-implemented` and
`IsImplemented=false`.

An immutable `AuditRuleSnapshot` contains rule code, domain/subdomain,
`AppliesTo`, element, requirement JSON, validation key, validation JSON,
severity, fix mode, source reference, layer, precedence, ordinal, and schema
version. The existing snapshot builder writes `{}` for validation JSON. The
default contract therefore comes from the explicit value-bearing validation
key. Optional test/profile snapshots may provide the documented parameters;
the validator never parses prose or uses a rule code to choose behavior.

| Validation key | Rule | Validator | Selector | Actual contract | Expected contract |
| --- | --- | --- | --- | --- | --- |
| `section.page-size-a4` | PPKI-LAY-003 | `PageSizeA4Validator` | all sections | width x height, twips, state/provenance | 11906 x 16838 twips; rotation accepted |
| `section.margin-left-4cm` | PPKI-LAY-008 | `MarginLeftValidator` | all sections | effective left margin twips | 2268 twips |
| `section.margin-right-3cm` | PPKI-LAY-009 | `MarginRightValidator` | all sections | effective right margin twips | 1701 twips |
| `section.margin-top-3cm` | PPKI-LAY-010 | `MarginTopValidator` | all sections | effective top margin twips | 1701 twips |
| `section.margin-bottom-3cm` | PPKI-LAY-011 | `MarginBottomValidator` | all sections | effective bottom margin twips | 1701 twips |
| `body.font-times-new-roman-12` | PPKI-LAY-005 | `BodyFontValidator` | visible runs in normal body paragraphs | effective ASCII/High ANSI family and size with independent provenance | Times New Roman; 24 half-points |
| `body.line-spacing-single` | PPKI-LAY-017 | `LineSpacingValidator` | normal body paragraphs | effective value and rule | 240 twips and `auto` |
| `body.first-line-indent-1cm` | PPKI-LAY-018 | `FirstLineIndentValidator` | normal body paragraphs | effective first-line indent | 567 twips |
| `body.justified` | PPKI-LAY-019 | `JustifiedValidator` | normal body paragraphs | effective alignment enum | `Justified` |

No layout key unambiguously represents page orientation, paragraph spacing
before/after, hanging/left/right indent, or complex-script size. Those
properties are not guessed. Supported heading and abstract/summary rules are
defined in the structural-validator document; table/figure/caption/citation/
reference rules and the remaining catalog are unsupported by Wave 1.

## Contract, registry, and applicability

`IDocumentRuleValidator` receives a `RuleValidationContext` containing the
persisted snapshot, parsed document, optional immutable document kind, bounded
options, and cancellation token.
It returns `Applicable`, `NotApplicable`, `Unsupported`, or
`InvalidRuleConfiguration`, zero or more candidates, and a safe diagnostic
code. `DocumentRuleValidatorRegistry` rejects duplicate keys and exposes keys
in ordinal order. An unknown key is `Unsupported`, never a pass.

Supported `AppliesTo` values are exact `Semua` and `All`. The fixed selectors
are `all-sections`, `normal-body-paragraphs`, and
`visible-runs-in-normal-body-paragraphs`. A different selector is unsupported.
Normal body scope excludes structural headings, tables, header/footer
inventories, footnotes/endnotes/comments, paragraphs with no visible content,
and hidden/deleted/empty runs. Semantic zones are not used to validate section
presence or ordering.

## Units and configuration

Raw Open XML units remain authoritative. Conversion uses invariant decimal
arithmetic and `MidpointRounding.AwayFromZero`:

- inch to twips: value x 1440;
- centimetre to twips: value x 144000 / 254;
- millimetre to twips: value x 14400 / 254;
- point to twips: value x 20;
- point to half-point: value x 2.

Unknown units and malformed JSON are invalid rule configuration. Comparison is
exact after normalization. Tolerance is zero unless `tolerance` and its unit
are explicitly present in snapshot validation JSON. Supported optional fields
are property-specific: page width/height/rotation, margin value, font family/
size/slots, line value/rule, first-line value, accepted alignment values, and
the exact selector.

## Findings, ordering, and limits

A finding candidate contains a controlled message key, deterministic compact
location, actual value, expected value, property order, and confidence. Actual
JSON is limited to property, raw/normalized safe formatting value, unit,
resolution state, provenance source/style/inheritance, safe diagnostic code,
and section/paragraph/run indexes. Expected JSON contains accepted normalized
values, unit, optional configured tolerance, validation key, and contract
source. Neither object contains document text, filename, storage path, identity,
raw XML, or exception details.

The semantic unique key is rule code + location + property + normalized actual.
Ordering is snapshot ordinal, compact location, property order, then normalized
actual. Duplicate candidates are removed before the global default cap of
10,000. Validators also stop collecting at the same bound. The canonical
finding projection excludes runtime database IDs and supports repeated and
parallel hash tests.

## Audit integration

After parsing, `AuditRunner` sends the already-persisted ordered snapshots to
`DocumentLayoutValidationEngine`. It maps candidates to `AuditFinding` using
severity, fix mode, rule code, rule ID, and source reference from that snapshot.
It does not reconstruct a live rule for validation. Unsupported or invalid
persisted validation configuration fails the processing audit with the existing
generic failure strategy rather than silently completing as compliant.

Applicable-rule count, snapshot hash, audit states, finding persistence,
score calculation, timestamps at the persistence boundary, and audit trail
events retain their existing contracts. Actual/expected content is never added
to audit trail metadata.

Parser diagnostics describe safe extraction/resolution issues. Validator
findings describe a mismatch against one immutable rule snapshot. Audit trail
events record lifecycle/resource actions and bounded scalar metadata; they do
not store either parser content or finding actual/expected payloads.

## Tests

```powershell
npm run test:layout-validators
npm run test:docx-parser
npm run verify
```

Tests cover conversion boundaries, registry behavior, effective formatting and
provenance, section/paragraph/run scope, missing and zero values, theme fonts,
privacy, fixture golden results, stable locations/order/dedup/limits, snapshot
isolation, repeated/parallel projections, and fixture immutability.

## Explicitly deferred

Not implemented: heading compliance, abstract/ringkasan validation, semantic
section ordering, required/missing sections, table of contents, tables,
figures/captions, citations/references, rendered-page validation, or auto-fix.
