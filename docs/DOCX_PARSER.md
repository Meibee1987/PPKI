# Deterministic DOCX parser contract

S2-T01 defines the read-only Open XML model, S2-T02 adds effective formatting
and provenance, and S2-T03 adds numbering semantics, structural heading
evidence, and a document outline. S2-T04 adds deterministic semantic-section
observation and document systematics. S2-T05 and S2-T06 consume this read-only
model for the supported Wave 1 layout and structural validators without
changing parser schema `4.0`.
The parser itself does not implement PPKI compliance, findings, rendered
layout, or auto-fix.

## Package safety and compatibility

`OpenXmlDocxParser` opens a materialized DOCX with
`WordprocessingDocument.Open(path, false)` and `AutoSave=false`. It never writes
the package, resolves external relationships, executes fields, loads image
bytes into the model, searches installed fonts, or uses database, Supabase,
Storage, HTTP, Word, or LibreOffice.

The worker contract remains `IDocxParser.ParseAsync(path, cancellationToken)`.
`AuditRunner` removes its temporary file in `finally` and exposes only the
generic `Document parsing failed.` category. Parsed content, numbering labels,
headings, outlines, semantic sections, and observed systematics are not
persisted or written to the audit trail.

Legacy constructors and paragraph properties retain their existing meaning.
In particular, raw/direct formatting, effective formatting, provenance,
effective numbering, and heading classification remain separate models.

## Schema and intermediate model

Parser and canonical projection schema are `4.0`. Version `4.0` is required
because the canonical semantics now include text-safe semantic sections,
abstract descriptors, ranges, classification evidence, and observed document
systematics in addition to the `3.0` numbering and outline contract.

`ParsedDocument` contains ordered body structure, sections, paragraphs/runs,
structural inventories, document defaults, style/theme catalogs, numbering
definitions, flat headings, the outline tree, semantic structure, observed
systematics, safe counts, and bounded diagnostics. It contains no timestamp,
random ID, absolute path, user/storage/database/audit identifier, credential,
or signed URL.

## Location contract

`DocumentElementLocation` uses zero-based section, body, paragraph, run, table,
row, and cell indexes. Part URIs are normalized internal OPC URIs. Compact
locations are stable, text-free values such as:

```text
maindocument/s:0/b:0/p:0/r:0/kind:run
```

Locations never claim a rendered page number.

`BodyElementIndex` is a zero-based global sequence across the main document
body; it does not restart at an Open XML section boundary. Finding ordering may
therefore place document-level locations first, then sort by `BodyElementIndex`,
followed by section, paragraph, and run indexes as deterministic tie-breakers.

## Raw units and formatting layers

- Page, margin, indentation, and spacing values use integer twips.
- Font sizes use integer half-points; drawing dimensions use EMUs.
- Numeric zero differs from missing.
- Boolean `false` differs from unspecified.
- Alignment/orientation use controlled enums.
- Invalid raw numbers produce safe diagnostics.

Direct formatting, style references, document defaults, and effective
formatting are resolved per property. `ResolvedFormattingValue<T>` records
state, source kind/property, source style ID, inherited/explicit status, and an
optional diagnostic code. Legacy cm/pt properties are not redefined as these
effective values.

Paragraph precedence is direct, referenced/default paragraph style, nearest
then ancestor `basedOn`, document defaults, then unspecified. Run precedence is
direct, character-style chain, paragraph-style run chain, document defaults,
theme, then unspecified. Style-layer toggle `true` reverses inherited state;
direct `true`/`false` is absolute. Theme resolution uses only package major/
minor Latin, East Asia, and complex-script slots.

## Numbering catalog

`ParsedNumberingCatalog` retains declaration-ordered abstract definitions and
instances. Abstract definitions include multi-level type, style links, and
level definitions. Instances include abstract references, level overrides,
start overrides, and stable declaration order.

Each level retains raw level index/start/format/text, suffix, justification,
restart value, legal-numbering flag, linked paragraph style, paragraph
indentation, and required run formatting. Raw XML is never retained. Duplicate
abstract IDs or instance IDs use the first declaration and emit
`numbering-definition-duplicate`.

Missing numbering parts are safe. Missing instances, abstract definitions, or
levels produce unresolved effective numbering and bounded diagnostics rather
than inferred values.

## Effective numbering precedence

`EffectiveParagraphNumbering` is separate from S2-T02 effective formatting.
Its source comes from the effective per-property cascade:

1. direct paragraph `numId`/`ilvl`;
2. referenced paragraph style;
3. nearest/ancestor `basedOn` style;
4. unspecified.

Direct values win independently. Zero remains valid. `numId=0` explicitly
disables numbering. A missing `numId` means no numbering; a present `numId`
without `ilvl` remains unresolved instead of inventing a level. Provenance
records direct/style source, style ID, inheritance, IDs, and a safe diagnostic.

## Counter and label semantics

Label state is new for every parse and keyed by numbering instance. Paragraphs
advance counters in deterministic main-document paragraph order. The current
level starts from `startOverride`, then level `start`, then the documented
parser fallback `1`. Repeated paragraphs increment the current level; advancing
a higher level resets deeper counters by default. Explicit restart `0` prevents
restart; a positive restart level resets only when its referenced higher level
advances. Other unsupported restart semantics are not guessed.

Level text supports `%1` through `%9`. Each placeholder uses its referenced
level format; legal-numbering levels render numeric placeholders as decimal.
Supported formats are:

- `decimal`;
- `upperRoman` and `lowerRoman` for positive values through 3999;
- `upperLetter` and `lowerLetter` using invariant alphabetic sequences;
- `bullet`, preserving the package glyph in-memory;
- `none`.

Suffixes `tab`, `space`, and `nothing` are represented separately and in the
in-memory value-with-suffix. Unsupported formats or invalid placeholders emit
diagnostics and do not receive guessed labels. Labels are never logged.

## Heading evidence and classification

`ParsedHeading` references a paragraph index/location rather than duplicating
its text. Confirmed headings require structural evidence:

1. valid direct outline level;
2. paragraph-style outline level;
3. inherited heading outline/style;
4. exact allowlisted built-in style IDs `Heading1` through `Heading9`;
5. a numbering level explicitly linked to a heading style.

Open XML outline levels `0..8` map explicitly to public heading levels `1..9`.
Direct outline wins. Conflicting lower-priority evidence produces
`heading-evidence-conflict`. Bold, uppercase, center alignment, font size, page
break, or text such as `BAB` never confirms a heading by itself. Formatting-only
paragraphs remain non-confirmed. Empty structural headings produce
`heading-empty`; fully hidden/deleted paragraphs are not silently promoted.

Built-in matching is case-normalized and limited to the fixed nine-style
allowlist. It does not depend on OS language. Custom styles require outline,
valid inheritance, or numbering-link evidence.

## Document outline tree

The document exposes a flat ordered heading list and a tree whose root is not a
paragraph. Level 1 headings are root children. A heading at level N becomes a
child of the nearest preceding heading with a smaller level; otherwise it is a
root child. Repeated levels become siblings. A skipped level emits
`heading-level-skipped` but still receives the deterministic nearest-smaller
parent.

Only confirmed main-document headings enter the tree. Header/footer headings
are never considered, and table paragraphs are excluded from the main outline.
Nodes retain deterministic locations, heading references, level, parent index,
and child order. No rendered page range or semantic PPKI section name is
inferred.

## Semantic section catalog and normalization

`SemanticDocumentStructureDetector` is a separate, stateless engine component.
Its fixed catalog version is `1.0`. The catalog recognizes controlled exact
aliases for title/approval/statement pages, Indonesian and English abstract or
summary sections, common front-matter lists, common main-matter titles,
references, appendices, and biography. It also recognizes a bounded `BAB` or
`CHAPTER` marker followed by a positive invariant decimal or Roman token.
Unknown headings remain observed as an `Other*` kind or unresolved structure;
they are never treated as violations.

Heading text is read only in memory and is capped at 512 characters by default.
Normalization uses Unicode NFKC, invariant upper case, trimmed/collapsed white
space, and controlled punctuation removal. A resolved numbering label may be
removed only when it is the exact parsed prefix. Matching is exact after
normalization: there is no substring, fuzzy-distance, language analysis, NLP,
AI, OS locale, or external service. Normalized text is not retained.

## Chapter, abstract, and summary evidence

Only S2-T03 structural headings are classified. A level-one heading with a
valid `BAB`/`CHAPTER` marker can be a confirmed chapter; a deeper marker remains
a candidate and receives safe ambiguity diagnostics. A numbered list, body
paragraph containing a semantic word, or formatting-only paragraph is never
promoted. Evidence preserves structural heading kind, direct/style/based-on or
numbering-linked outline origin, heading level, resolved numbering state,
Open XML section transition, body order, exact-alias/chapter-marker match, and
zone boundary without copying heading text.

Exact `ABSTRAK`, `ABSTRACT`, `RINGKASAN`, and `SUMMARY` headings determine the
controlled Indonesian/English label. Body text is not used for language
detection. `AbstractSectionDescriptor` references heading/content/end
locations, paragraph count, optional exact `KATA KUNCI:`/`KEYWORDS:` paragraph
location, evidence, and safe codes. It never contains abstract content. Word
count, keyword correctness, and required-presence checks are outside this
contract.

## Zones, ranges, and observed systematics

The first confirmed chapter or exact main-matter section establishes main
matter. Exact references or appendices establish back matter; structurally
bounded material before main matter is front matter. A known kind that would
move the observed zone backward becomes ambiguous/unknown and emits
`semantic-zone-regression`. Unknown headings inherit a zone only when those
boundaries are clear.

Ranges use main-body element order, never rendered pages. Start is the heading;
content starts at the next paragraph/table body element. Abstract/summary ends
before the next semantic heading. Other sections end before the next heading
at the same or higher level, and the last ends at body content end. This admits
proper parent/child containment while rejecting unresolved/crossing boundaries.
Tables remain part of containing ranges. Empty sections and malformed overlap
receive bounded diagnostics.

`DocumentSystematics` is an ordered observation: section kind/zone/range,
classification state, parent, duplicate group, evidence summary, zone starts,
chapter count, abstract inventory, ambiguity/duplicate inventories, unknown
structural headings, and diagnostic codes. It has no expected ordering,
pass/fail, severity finding, missing-section violation, or compliance score.

Duplicate recognized kinds receive stable integer groups and
`semantic-section-duplicate`; abstract/summary duplicates additionally receive
`abstract-section-duplicate`. Repeated chapters and `Other*` headings are not
duplicates. Ambiguous classification, empty content, unresolved boundaries,
overlap, zone regression, and overlong headings use safe codes without text.

Structural headings inside main-body tables are retained in the original
heading inventory but appear only as excluded semantic candidates. Header and
footer headings are not supplied to the detector. Footnote, endnote, comment,
and drawing/text-box content is not a section source.

## Privacy and diagnostics

Diagnostics contain only controlled code/severity/message key, deterministic
location, and allowlisted numeric/type/style metadata. They exclude paragraph
or heading text, labels, XML, paths, filenames, external targets, stack traces,
and dependency messages. The diagnostic cap prevents malformed-document floods.

Canonical projection schema `4.0` includes numbering definitions, effective
numbering, text-free heading evidence, outline structure, semantic sections,
abstract descriptors, and observed systematics. It excludes paragraph and
heading text, abstract content, normalized aliases, runtime counters/cache,
current time, random IDs, and
absolute paths. Repeated and parallel parses—including use of one parser
instance—must yield identical projections and hashes.

## Resource limits

| Resource | Default |
| --- | ---: |
| input bytes | 25 MiB |
| expanded package bytes | 200 MiB |
| package entries | 50,000 |
| paragraphs | 100,000 |
| runs | 500,000 |
| tables | 10,000 |
| relationships | 20,000 |
| diagnostics | 200 |
| styles | 10,000 |
| style inheritance depth | 64 |
| abstract numbering definitions | 10,000 |
| numbering instances | 10,000 |
| numbering levels and overrides | 100,000 |
| outline nodes | 100,000 |
| semantic sections | 10,000 |
| fixed section aliases | 1,000 |
| semantic heading characters | 512 |
| systematics entries | 10,000 |

Hard-limit violations raise safe `resource-limit-exceeded` errors. All options
must be positive.

## Tests

The eight fixtures are wholly synthetic and parsed through checksum-verified
temporary copies.

```powershell
npm run fixtures:generate
npm run fixtures:check
npm run test:docx-parser
```

## Explicitly deferred

Implemented outside the parser: the deterministic layout validators documented
in `docs/LAYOUT_VALIDATORS.md` and the supported heading and abstract/summary
validators documented in `docs/WAVE1_STRUCTURAL_VALIDATORS.md`.

Not implemented here: rule evaluation itself, formal document-systematics order
(the catalog has no sequence parameter), keyword-value validation,
table-of-contents consistency, every Word numbering format and special restart
mode, full table-style cascade, rendered-page layout, or auto-fix.
