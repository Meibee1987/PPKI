# Wave 1 structural validators

S2-T06 closes Wave 1 with deterministic heading and abstract/summary
validation. It consumes parser schema `4.0`, immutable `AuditRuleSnapshot`
rows, the S2-T05 registry/finding engine, structural heading evidence,
effective formatting/provenance, semantic section ranges, and observed
systematics. It does not modify DOCX files and does not implement auto-fix.

## Snapshot inventory and classification

The source catalog contains 18 HDG, 19 ABS, and 25 STR rules. It contains no
`validation_key` field. `RuleCatalogImporter` maps only the 16 deterministic
rules marked supported below. The remaining rules retain
`manual.not-implemented` and `IsImplemented=false`; unsupported rules therefore
cannot be silently counted as compliant.
No LAY rule or existing layout validation key has an explicit heading or
semantic-section selector; the nine S2-T05 layout mappings remain unchanged.

Every snapshot retains the complete requirement object:
`{officialRequirement, expectedValuePattern}`, source reference
`{sourceSection,pdfPage,printedPage}`, severity, fix mode, applies-to value,
and validation JSON. The current catalog snapshot builder writes `{}` as
validation JSON. Supported defaults are deliberately encoded in their
value-bearing key. A supplied snapshot may override only the documented bounded
parameters. Rule code is evidence, not the validator dispatcher.

Status values are `SupportedDeterministic` (SD),
`SupportedWithBoundedTextInspection` (SBT), `ManualOrConfirm` (MC),
`RequiresRenderedLayout` (RRL), `RequiresExternalMetadata` (REM), and
`UnsupportedInWave1` (UW1). In the tables, Req is the snapshot
`expectedValuePattern`, Params is the current validation JSON, and Source is
`sourceSection / PDF page`. Severity and fix mode are copied unchanged to each
finding.

### HDG inventory

| Rule | Validation key | Applies | Req / Params | Sev / Fix | Source | Status | Validator or reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| HDG-001 | `heading.chapter-number-upper-roman-no-period` | Semua | `I, II, III ... tanpa titik` / `{}` | Error / Auto | Lampiran 16 butir 8–9 / 161 | SD | `ChapterNumberingValidator`; parsed numbering format/category only |
| HDG-002 | `heading.maximum-depth-3` | Semua | `Maksimum 3; tingkat 4 warning` / `{}` | Warning / Confirm | Lampiran 16; Lampiran 17 / 161 | SD | `HeadingDepthValidator`; default `maximumLevel=3` |
| HDG-003 | `heading.chapter-uppercase` | Semua | `UPPERCASE` / `{}` | Error / Auto | Lampiran 16 / 161 | SBT | `ChapterUppercaseValidator`; invariant bounded comparison |
| HDG-004 | `heading.chapter-bold` | Semua | `Bold` / `{}` | Error / Auto | Lampiran 16 / 161 | SD | `ChapterBoldValidator`; effective run formatting |
| HDG-005 | `heading.chapter-no-period-no-underline` | Semua | `No period; no underline` / `{}` | Error / Auto | Lampiran 16 / 161 | SBT | `ChapterDecorationValidator`; safe punctuation category plus effective underline |
| HDG-006 | `heading.chapter-centered` | Semua | `Centered` / `{}` | Error / Auto | Lampiran 16 / 161 | SD | `ChapterAlignmentValidator` |
| HDG-007 | `heading.subheading-decimal-left` | Semua | `1.1, 1.2 ...; left` / `{}` | Error / Auto | Lampiran 16 / 161 | SBT | `SubheadingNumberingAlignmentValidator`; bounded parsed-label category, not body text |
| HDG-008 | `manual.not-implemented` | Semua | `Title Case exceptions` / `{}` | Warning / Auto | Lampiran 16 / 161 | UW1 | Exception list exists only in non-snapshot implementation prose; no versioned parameter |
| HDG-009 | `heading.subheading-bold-no-period-no-underline` | Semua | `Bold; no period; no underline` / `{}` | Error / Auto | Lampiran 16 / 161 | SBT | `SubheadingDecorationValidator` |
| HDG-010 | `manual.not-implemented` | Semua | `Above 2 lines; below 1 line` / `{}` | Warning / Auto | Lampiran 16 / 161 | UW1 | “Line” has no formal twip/line-height conversion parameter |
| HDG-011 | `heading.subsubheading-decimal-left` | Semua | `1.1.1 ...; left` / `{}` | Error / Auto | Lampiran 16 / 162 | SBT | `SubSubheadingNumberingAlignmentValidator` |
| HDG-012 | `manual.not-implemented` | Semua | `Title Case exceptions` / `{}` | Warning / Auto | Lampiran 16 / 162 | UW1 | No versioned title-case exception parameter |
| HDG-013 | `heading.subsubheading-regular-no-period-no-underline` | Semua | `Regular; no period; no underline` / `{}` | Error / Auto | Lampiran 16 / 162 | SBT | `SubSubheadingDecorationValidator` |
| HDG-014 | `manual.not-implemented` | Semua | `Above 1.5 lines; below 1 line` / `{}` | Warning / Auto | Lampiran 16 / 162 | UW1 | No formal conversion contract |
| HDG-015 | `manual.not-implemented` | Semua | `Wrapped; single spacing` / `{}` | Warning / Auto | Lampiran 16 / 162 | RRL | Wrapping requires rendered layout; combined rule cannot be partially claimed |
| HDG-016 | `manual.not-implemented` | Semua | `Sentence case; regular` / `{}` | Warning / Auto | Lampiran 17 / 164 | MC | Sentence-case linguistic exceptions are not formally versioned |
| HDG-017 | `manual.not-implemented` | Semua | `a b c; then 1) 2) 3)` / `{}` | Warning / Auto | Lampiran 17 / 163 | UW1 | Context transition and expected numbering levels are not parameterized |
| HDG-018 | `manual.not-implemented` | Semua | `a) b) c); then (1) (2) (3)` / `{}` | Warning / Auto | Lampiran 17 / 163 | UW1 | Context transition and expected numbering levels are not parameterized |

### ABS inventory

| Rule | Validation key | Applies | Req / Params | Sev / Fix | Source | Status | Validator or reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| ABS-001 | `abstract.skripsi-language-pair` | Skripsi | `Both languages` / `{}` | Error / Confirm | 3.1.4 / 34 | SD | `SkripsiAbstractLanguagePairValidator`; Indonesian/English semantic kinds |
| ABS-002 | `manual.not-implemented` | Skripsi | `Name + title + supervisors; names uppercase` / `{}` | Error / Confirm | 3.1.4; Lampiran 4a / 34 | REM | Identity/title/supervisor metadata and parsing are outside Wave 1 |
| ABS-003 | `abstract.skripsi-narrative-paragraph-count-one` | Skripsi | `1 paragraph per language` / `{}` | Error / Auto | 3.1.4 / 34 | SD | `SkripsiAbstractParagraphCountValidator`; default `paragraphCount=1` |
| ABS-004 | `abstract.skripsi-word-count-max-200` | Skripsi | `≤200 words` / `{}` | Error / Confirm | 3.1.4 / 34 | SBT | `SkripsiAbstractWordCountValidator`; default `maximumWords=200` |
| ABS-005 | `manual.not-implemented` | Skripsi | `1 page total` / `{}` | Warning / Confirm | 3.1.4; Lampiran 4a / 34 | RRL | Page span is unavailable without rendering |
| ABS-006 | `manual.not-implemented` | Skripsi | required content checklist / `{}` | Warning / Manual | 3.1.4 / 34 | MC | Content quality/semantic checklist is manual; no AI/NLP |
| ABS-007 | `manual.not-implemented` | Skripsi | `No citations/tables/figures` / `{}` | Error / Confirm | 3.1.4 / 34 | MC | Citation/object patterns are not formally parameterized |
| ABS-008 | `manual.not-implemented` | Skripsi | `Defined acronym reused` / `{}` | Warning / Confirm | 3.1.4 / 34 | MC | Linguistic/acronym interpretation deferred |
| ABS-009 | `manual.not-implemented` | Skripsi | `≤5; alphabetical` / `{}` | Error / Auto | 3.1.4 / 34 | UW1 | Marker is observable, but keyword parsing/sort contract is absent |
| ABS-010 | `manual.not-implemented` | Skripsi | `Page i hidden` / `{}` | Error / Auto | 3.1.4 / 34 | RRL | Visible page-number result is rendered-layout behavior |
| ABS-011 | `abstract.skripsi-single-spacing-zero-paragraph-spacing` | Skripsi | `Single; before 0; after 0` / `{}` | Error / Auto | Lampiran 4b / 127 | SD | `SkripsiAbstractSpacingValidator`; effective 240/auto/0/0 twips |
| ABS-012 | `manual.not-implemented` | Laporan akhir | `≤1 page; multi-paragraph; single` / `{}` | Error / Confirm | Lampiran 5a / 128 | RRL | Combined rule includes rendered page count and vague paragraph count |
| ABS-013 | `summary.thesis-dissertation-language-pair` | Tesis/Disertasi | `Both languages` / `{}` | Error / Confirm | 3.1.5 / 35 | SD | `ThesisSummaryLanguagePairValidator`; Ringkasan/Summary kinds |
| ABS-014 | `manual.not-implemented` | Tesis/Disertasi | `≤2 pages each; single` / `{}` | Error / Confirm | 3.1.5; Lampiran 5c / 35 | RRL | Rendered page span required |
| ABS-015 | `manual.not-implemented` | Semua terkait | `Required lead line` / `{}` | Error / Confirm | 3.1.5 / 35 | REM | Identity/lead-line structure requires external metadata/template |
| ABS-016 | `manual.not-implemented` | Semua terkait | `Required content` / `{}` | Warning / Manual | 3.1.5 / 35 | MC | Content quality is manual |
| ABS-017 | `manual.not-implemented` | Semua terkait | `No citations/tables/figures` / `{}` | Error / Confirm | 3.1.5 / 35 | MC | Formal bounded citation/object contract absent |
| ABS-018 | `manual.not-implemented` | Semua terkait | `≤5; alphabetical` / `{}` | Error / Auto | 3.1.5 / 35 | UW1 | Keyword values and sorting deliberately not inspected |
| ABS-019 | `abstract-summary-single-spacing-zero-paragraph-spacing` | Semua terkait | `Single; before 0; after 0` / `{}` | Error / Auto | Lampiran 5a–5e / 128 | SD | `AbstractSummarySpacingValidator`; abstract and summary semantic ranges |

### STR inventory

No STR rule is mapped in Wave 1. This is intentional: the catalog does not
provide a formal required-section list, ordered semantic-kind sequence,
before/after constraint, optional group, or zone-placement parameter.
`DocumentSystematics` is observed parser data, not itself a compliance rule.

| Rule | Validation key | Applies | Req / Params | Sev / Fix | Source | Status | Deferred reason |
| --- | --- | --- | --- | --- | --- | --- | --- |
| STR-001 | `manual.not-implemented` | Semua | `Required sequence` / `{}` | Error / Confirm | 3.1 Bagian Awal / 33 | UW1 | Exact order/mode/optional groups absent; no sequence is guessed |
| STR-002 | `manual.not-implemented` | Semua | `Same content as cover` / `{}` | Error / Confirm | 3.1.2 / 34 | MC | Cross-section content comparison and logo exception |
| STR-003 | `manual.not-implemented` | Semua | `Official statement template` / `{}` | Error / Confirm | 3.1.3; Lampiran 3 / 34 | REM | No versioned template snapshot |
| STR-004 | `manual.not-implemented` | Semua | `Bogor + month/year + name + NIM` / `{}` | Error / Confirm | Lampiran 3 / 125 | REM | Identity/date metadata validation |
| STR-005 | `manual.not-implemented` | Laporan akhir/Tesis/Disertasi | `Official copyright template` / `{}` | Error / Confirm | Lampiran 6 / 135 | REM | No versioned template snapshot |
| STR-006 | `manual.not-implemented` | Semua | `Type + degree statement` / `{}` | Error / Confirm | 3.1.7; Lampiran 7 / 35 | REM | Degree/profile template metadata absent |
| STR-007 | `manual.not-implemented` | Semua | `TNR 14` / `{}` | Error / Auto | Lampiran 7a–7e / 136 | UW1 | Target semantic section/selector is not represented formally |
| STR-008 | `manual.not-implemented` | Semua | `Full names with degrees` / `{}` | Error / Confirm | 3.1.8; Lampiran 8 / 35 | REM | Identity and degree metadata |
| STR-009 | `manual.not-implemented` | Semua | `Even page facing approval` / `{}` | Error / Confirm | 3.1.8 / 35 | RRL | Rendered page parity required |
| STR-010 | `manual.not-implemented` | Semua | `Required fields` / `{}` | Error / Confirm | 3.1.9 / 36 | REM | Field list/template absent |
| STR-011 | `manual.not-implemented` | Semua | `Full names and degrees` / `{}` | Error / Confirm | 3.1.9 / 36 | REM | External identity metadata |
| STR-012 | `manual.not-implemented` | Semua | `Vertical one-by-one` / `{}` | Warning / Confirm | 3.1.9 / 36 | RRL | Visual signature layout required |
| STR-013 | `manual.not-implemented` | Laporan akhir | program/school approvers / `{}` | Error / Confirm | 3.1.9; Lampiran 9a / 36 | REM | Organization/profile metadata absent |
| STR-014 | `manual.not-implemented` | Skripsi non-FKH/SB | department/program head / `{}` | Error / Confirm | 3.1.9; Lampiran 9b / 36 | REM | Unit/profile metadata absent |
| STR-015 | `manual.not-implemented` | Skripsi FKH | vice dean academic / `{}` | Error / Confirm | 3.1.9; Lampiran 9c / 36 | REM | Unit/profile metadata absent |
| STR-016 | `manual.not-implemented` | Skripsi Sekolah Bisnis | program head / `{}` | Error / Confirm | 3.1.9 / 36 | REM | Unit/profile metadata absent |
| STR-017 | `manual.not-implemented` | Tesis/Disertasi | program head + dean / `{}` | Error / Confirm | 3.1.9; Lampiran 9d–9e / 36 | REM | Unit/profile metadata absent |
| STR-018 | `manual.not-implemented` | Semua | topic/time/location/funding / `{}` | Warning / Confirm | 3.1.10 / 36 | MC | Semantic content checklist |
| STR-019 | `manual.not-implemented` | Semua | relevant contributors / `{}` | Warning / Confirm | 3.1.10 / 36 | MC | Semantic content checklist |
| STR-020 | `manual.not-implemented` | Semua | `Relevant content only` / `{}` | Info / Manual | 3.1.10 / 36 | MC | Substantive review |
| STR-021 | `manual.not-implemented` | Semua | `TOC matches headings` / `{}` | Error / Auto | 3.1.11 / 36 | UW1 | TOC reconciliation explicitly deferred |
| STR-022 | `manual.not-implemented` | Semua | `Create list when count >1` / `{}` | Warning / Auto | 3.1.12 / 36 | UW1 | Cross-domain table/figure inventory rule deferred |
| STR-023 | `manual.not-implemented` | Semua | `May share page` / `{}` | Info / Auto | 3.1.12 / 36 | RRL | Informational rendered-page allowance, not a deterministic violation |
| STR-024 | `manual.not-implemented` | Semua | `Per template` / `{}` | Warning / Auto | Lampiran 11a–11d / 151 | UW1 | Versioned TOC style template absent |
| STR-025 | `manual.not-implemented` | Semua | `Number + title + page` / `{}` | Warning / Auto | Lampiran 12 / 155 | RRL | TOC/list rendered page values required |

## Heading applicability and findings

Heading validators consume only S2-T03 `ParsedHeading` entries. Confirmed
main-document headings are eligible; table/header/footer headings and ordinary
numbered lists are excluded. Candidates are excluded unless an immutable
snapshot explicitly sets `includeCandidates=true`. Chapter-specific rules
additionally require a confirmed S2-T04 `Chapter` section. An ambiguous chapter
classification returns `Unsupported` with a safe code rather than passing.

Number rules use `EffectiveParagraphNumbering` and `ResolvedNumberingLabel`.
They never parse a number from heading body text. Chapter numbers require the
`UpperRoman` format and no trailing period. Level-2 and level-3 validators
classify an in-memory parsed label as an exact ASCII decimal dotted hierarchy;
the label itself is never retained. Unsupported/unresolved numbering produces a
finding category, not a guessed number.

Alignment uses effective paragraph formatting. Bold/regular and underline use
visible effective run formatting, including style/theme resolution and
property-level provenance. Deleted, hidden, effective-hidden, and empty runs do
not control the result. Mixed bold state is the safe category `mixed`. One
finding is emitted per property and heading location.

Uppercase and ending-period checks inspect at most 512 UTF-16 characters in
memory after Unicode NFKC normalization, invariant case conversion, and trim.
Only boolean or punctuation categories leave the validator. Heading text,
normalized text, and numbering labels never enter finding JSON, diagnostics,
logs, canonical projections, or audit trail events.
Uppercase comparison does not attempt to interpret scientific names or symbols;
such exceptions require manual confirmation and no linguistic auto-fix exists.

## Abstract and summary contracts

Applicability uses the immutable nullable `DocumentKindSnapshot` captured when
the audit job is created. It is never re-read from the live document/type
relation or inferred from a title or DOCX content. Exact
snapshot selectors `Skripsi` and `Tesis/Disertasi` are supported; an unknown
document kind is invalid configuration and a different known kind is
`NotApplicable`.

Presence checks use confirmed semantic kinds only:
`AbstractIndonesian`/`AbstractEnglish` for Skripsi and
`SummaryIndonesian`/`SummaryEnglish` for Tesis or Disertasi. They do not perform
language detection. A missing section uses compact location `maindocument`,
with no fake section, paragraph, or page index.

Paragraph and word counts use each `AbstractSectionDescriptor` range. Heading,
table paragraph, exact parser-recognized keyword paragraph, deleted text,
hidden text, and effective-hidden runs are excluded. Narrative text is read
only in memory. Tabs, line breaks, and repeated whitespace are boundaries.
Unicode letters/digits form tokens; apostrophe/right-apostrophe and hyphen are
internal only when surrounded by letters/digits. The tokenizer uses Unicode
NFKC and no OS locale. Actual JSON contains only an integer count.

Formatting validators inspect the effective narrative paragraph values:
line-spacing value 240 twips, rule `auto`, spacing before 0 twips, and spacing
after 0 twips. Abstract and normal-body layout selectors remain separate, so a
paragraph cannot enter the normal-body S2-T05 selector after it is a structural
heading; rule-specific findings remain distinct by snapshot rule code.

Duplicate abstract groups are observed and deterministic in parser schema 4.0,
but no ABS rule formally forbids duplicates. Wave 1 therefore does not invent a
duplicate-section validation key. Keyword value/count/sort quality is likewise
not claimed.

## Ordering, privacy, deduplication, and limits

The shared engine orders by snapshot ordinal, numeric body/section/paragraph/run
location, compact location, property order, and normalized safe category. It
then performs S2-T05 semantic deduplication and applies the global finding cap.
Runtime database IDs, timestamps, and random values are absent from canonical
results. Repeated and parallel validation yield the same projection and hash.

Structural limits are immutable positive constants:

| Limit | Value | Safe overflow behavior |
| --- | ---: | --- |
| heading characters inspected | 512 | `heading-text-limit-exceeded` / Unsupported |
| abstract narrative characters counted | 100,000 | `narrative-text-limit-exceeded` / Unsupported |
| ordering entries reserved for a future formally configured STR validator | 1,000 | no STR validator currently consumes it |
| findings | S2-T05 `LayoutValidatorOptions.MaximumFindings`, default 10,000 | stable truncation with `FindingsTruncated=true` |

Actual/expected/location may contain only controlled property names, enums,
booleans, counts, units, resolution/provenance, semantic kinds, safe diagnostic
codes, and stable text-free locations. They never contain heading, abstract,
summary, or keyword text; identity data; filename/path/URL; user data; raw XML;
exception messages; or stack traces. `AuditFindingMapper` remains the only
persistence boundary. Audit trail metadata receives no finding payload or
parsed semantic content.

## Audit integration and commands

`AuditRunner` reads the audit-owned document-kind snapshot, ensures/reuses
immutable resolved snapshots, parses the DOCX, and calls the shared validation
engine with only those persisted rule snapshots plus the immutable nullable
document-kind context. Unknown keys, ambiguous supported structure, or invalid
configuration cannot complete as compliant. Snapshot hashing, applicable rule
count, finding persistence/count, score calculation, lifecycle, queue contract,
and generic failure handling are unchanged.

```powershell
npm run test:wave1-validators
npm run test:layout-validators
npm run test:docx-parser
npm run verify
```

All tests use the eight existing synthetic fixtures or temporary in-memory
models/packages. S2-T06 adds no permanent fixture and does not modify fixture
checksums.

## Explicitly deferred beyond Wave 1

Not complete: formal document-systematics required presence/order/zone rules,
TOC-versus-heading reconciliation, rendered page numbers/parity, visual cover
or logo checks, identity/NIM/supervisor validation, abstract language/content
quality, keyword quality, title/sentence-case linguistic rules, table/figure/
caption validation, citation/reference validation, scoring UI, findings UI,
export, and auto-fix. These are not reported as compliant and are not silently
executed.
