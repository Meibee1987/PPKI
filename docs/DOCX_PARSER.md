# Deterministic DOCX parser contract

S2-T01 defines a read-only Open XML intermediate model used by later compiled
validators. It does not implement PPKI validation, effective style resolution,
rendered page estimation, or auto-fix.

## Scope and package safety

`OpenXmlDocxParser` opens a local materialized DOCX with
`WordprocessingDocument.Open(path, false)` and `AutoSave=false`. It never writes
the package, resolves an external relationship, downloads a hyperlink, loads
image bytes into the parsed model, or requires database, Storage, Supabase, or
network access. A missing/corrupt/unsupported package raises
`DocxParserException` with a stable code and generic message; dependency
messages, XML, filename, and absolute path are not exposed.

The worker contract remains `IDocxParser.ParseAsync(path, cancellationToken)`.
`AuditRunner` deletes its unique temporary file in `finally` and converts a
parser failure to the generic `Document parsing failed.` category. No parsed
model or document text is written to the database or audit trail.

## Intermediate model

`ParsedDocument` uses parser schema `1.0` and contains package type, sections,
body element order, safe aggregate counts, paragraphs/runs, tables, drawing
metadata, normalized field kinds, header/footer inventories, style/numbering
references, and bounded diagnostics. Collections are read-only and constructed
in package order or explicit ordinal order.

The model contains no timestamp, random ID, absolute path, user/owner/audit ID,
database entity, credential, Storage path, or signed URL.

## Location contract

`DocumentElementLocation` uses zero-based indexes for section, body element,
paragraph, run, table, row, and cell. Header/footer locations also carry their
controlled type. `PartUri` is a normalized internal OPC part URI; external
targets are never stored. `ToCompactString()` produces a stable representation:

```text
maindocument/s:0/b:0/p:0/r:0/kind:run
```

Locations contain no paragraph text, XPath, memory address, current time, GUID,
or claimed rendered page number.

## Raw Open XML units and normalization

- Page size, margins, header/footer distance, gutter, indentation, and fixed
  spacing remain signed integer twips.
- Direct font size remains integer half-points.
- Drawing dimensions remain integer EMUs.
- Line spacing retains both integer value and rule.
- Alignment, orientation, break, field, drawing, and header/footer values use
  controlled enums.
- Boolean formatting is nullable so missing/inherited differs from `false`.

Legacy cm/pt properties remain for Sprint 00/01 validators. Twips-to-cm display
conversion uses invariant decimal arithmetic and rounds to two decimals with
`MidpointRounding.AwayFromZero`; raw values are never discarded. Equality and
canonical projections use raw integers, not floating-point comparisons. Invalid
numeric attributes become bounded diagnostics.

## Section semantics

A paragraph `w:sectPr` terminates that section; the body-level final `w:sectPr`
defines the final section. Missing values remain null rather than silently
receiving Word defaults. Extraction includes size/orientation, margins,
header/footer distances, gutter, section type, columns, start page number, and
internal header/footer references.

## Paragraph, run, and privacy contract

Paragraph/run order follows XML order. Direct paragraph properties include
style/numbering references, alignment, indentation, spacing/rule, keep flags,
page-break-before, and outline level. Direct run properties include fonts,
half-point size, tri-state bold/italic, underline, language, vertical alignment,
tabs, breaks, field/drawing references, and hidden/inserted/deleted state.

Text segments may exist in memory for later validators. Semantic whitespace,
tabs, line breaks, and page breaks stay distinct; deleted text is marked and
excluded from legacy normalized text. Text is never placed in diagnostics,
logs, audit metadata, or canonical projections.

## Structural inventory

- Tables retain table/row/cell locations, style, width/grid metadata, and body
  paragraph indexes. Nested tables currently produce a diagnostic.
- Drawings retain inline/anchor kind, relationship ID, content type, and EMU
  dimensions; image binary is not loaded into the model.
- Fields retain only a normalized instruction kind and begin/separate/end
  structure. Fields and macros are never executed.
- Headers/footers retain internal part URI, controlled type, and paragraphs.
- Footnote, endnote, and comment references are counted without validation.
- Style and numbering catalogs retain references; effective inheritance is not
  resolved in S2-T01.

## Diagnostics and resource limits

Diagnostics contain only code, controlled severity, safe message key, optional
location, and sorted allowlisted metadata. They exclude text, XML, filenames,
paths, external targets, stack traces, and dependency messages. The last entry
becomes `diagnostics-truncated` when the cap is reached.

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

Exceeding a hard limit raises `resource-limit-exceeded`. Cancellation is checked
before opening and during relationship/body/section/header traversal.

## Determinism and golden tests

`ParsedDocumentCanonicalProjection` serializes a text-free semantic projection
in fixed order and calculates SHA-256. Tests require repeated and parallel
parses to yield identical projection/hash results; ZIP byte equality is not
used. The five fixtures are wholly synthetic and parsed through temporary
copies whose original checksum is verified.

```powershell
npm run fixtures:generate
npm run fixtures:check
npm run test:docx-parser
```

## Explicitly deferred

S2-T02/S2-T03 may add effective style inheritance and heading semantics. Also
deferred are PPKI validators, rendered page estimation, complete nested/notes/
comments content models, and all auto-fix behavior. Raw direct formatting and
style references must not be described as effective formatting.
