# Deterministic DOCX parser contract

S2-T01 defines the read-only Open XML intermediate model. S2-T02 adds
deterministic effective formatting with property-level provenance. Neither task
implements PPKI validation, heading semantics, findings, rendered-page
estimation, or auto-fix.

## Scope and package safety

`OpenXmlDocxParser` opens a local materialized DOCX with
`WordprocessingDocument.Open(path, false)` and `AutoSave=false`. It never writes
the package, resolves an external relationship, downloads a hyperlink, loads
image bytes into the model, searches installed fonts, or requires database,
Storage, Supabase, or network access. Missing/corrupt/unsupported packages raise
`DocxParserException` with a stable code and generic message.

The worker contract remains `IDocxParser.ParseAsync(path, cancellationToken)`.
`AuditRunner` removes its unique temporary file in `finally` and maps parser
failure to `Document parsing failed.` No parsed model or document text is
written to the database or audit trail.

## Intermediate model and versions

`ParsedDocument` uses parser schema `2.0` and canonical projection schema
`2.0`. It contains package type, sections, ordered body elements, safe counts,
paragraphs/runs, structural inventories, document defaults, style and numbering
catalogs, theme-font slots, effective formatting, and bounded diagnostics.
Schema `2.0` is required because effective values and provenance materially
change the canonical projection.

The model contains no timestamp, random ID, absolute path, user/owner/audit ID,
database entity, credential, Storage path, or signed URL. Collections preserve
package/declaration order or use an explicitly documented ordinal sort.

## Location contract

`DocumentElementLocation` uses zero-based section, body, paragraph, run, table,
row, and cell indexes. Header/footer locations carry their controlled type.
`PartUri` is a normalized internal OPC URI; external targets are never stored.
`ToCompactString()` returns a stable representation such as:

```text
maindocument/s:0/b:0/p:0/r:0/kind:run
```

Locations contain no paragraph text, XPath, memory address, time, GUID, or
claimed rendered page number.

## Raw Open XML units and normalization

- Page, margin, indentation, and spacing values remain integer twips.
- Font sizes remain integer half-points; drawing dimensions remain EMUs.
- Line spacing retains its integer value and rule.
- Alignment and orientation use controlled enums.
- Boolean values remain nullable, so explicit `false` differs from missing.
- Numeric zero remains distinct from missing.

Legacy cm/pt/string properties retain their S2-T01 behavior for existing
validators; they are not redefined as effective values. Display conversion uses
invariant decimal arithmetic and `MidpointRounding.AwayFromZero`. Canonical
equality uses raw integers. Invalid numeric attributes produce safe diagnostics.

## Formatting layers and provenance

Raw values, direct formatting, style references, document defaults, and
effective formatting are separate contracts. Direct/catalog values use
`ParagraphFormattingProperties` and `RunFormattingProperties`. Effective
paragraph, run, and section models contain `ResolvedFormattingValue<T>` for
each property.

Each value records `Resolved`, `Unspecified`, `Unresolved`, or `Invalid` state,
source kind/property, normalized source style ID when relevant, inherited versus
explicit status, and an optional safe diagnostic code. Provenance excludes
document text, raw XML, paths, filenames, infrastructure IDs, current time, and
random values.

## Document defaults and style catalog

Paragraph/run defaults are read from `w:docDefaults`. The first declaration of
each style records ID, controlled type, name, default/custom flags, `basedOn`,
`next`, `link`, direct paragraph/run properties, and stable declaration order.
Paragraph and character styles participate in resolution. Table/numbering
styles may be inventoried but do not receive a full cascade.

Duplicate IDs use the first declaration and emit `style-id-duplicate`. Missing
targets, direct/indirect cycles, type mismatches, and excessive chain depth emit
bounded safe diagnostics. A valid partial chain may resolve a property, with the
chain diagnostic retained in its provenance.

## Paragraph cascade

Each paragraph property independently uses:

1. direct paragraph property;
2. referenced paragraph style (or declared default paragraph style);
3. nearest then ancestor `basedOn` styles;
4. document paragraph defaults;
5. unspecified.

The contract covers alignment, indentation, spacing/line rule, keep flags,
page-break-before, widow/contextual spacing, outline level, and numbering
ID/level references. It does not render numbering labels or infer compliance.

## Run cascade, toggle semantics, and theme fonts

Non-toggle properties use direct run formatting, character-style chain,
paragraph-style run properties, document run defaults, then package theme
resolution. ASCII, High ANSI, East Asia, and complex-script font slots remain
separate.

For Open XML toggle properties, style-layer `true` toggles the inherited state;
style-layer `false` leaves it unchanged. Direct `true` or `false` is an absolute
override. This avoids incorrect logical-OR inheritance. Theme mappings include
major/minor Latin, East Asia, and complex script. Missing theme parts/slots
produce `theme-font-unresolved`; the resolver never queries OS fonts.

## Section semantics and effective page contract

Paragraph `w:sectPr` terminates a section and body-level `w:sectPr` defines the
final section. Every present raw section property resolves with provenance
`SectionProperties`; every missing value stays `Unspecified`. The parser does
not assume A4, margins, orientation, prior-section values, printer/locale
defaults, page count, or rendered position.

## Paragraph, run, structural, and privacy contract

Paragraph/run order follows XML order. Semantic whitespace, tabs, line/page
breaks, hidden/deleted/inserted state, field references, and drawing references
remain distinct. Text may exist in memory for later validation but is never put
in diagnostics, logs, audit metadata, or canonical projections.

Tables retain row/cell structure and width/grid metadata. Drawings retain only
relationship/content-type/dimension metadata. Fields retain normalized kinds
and begin/separate/end structure and are never executed. Header/footer parts and
footnote/endnote/comment references are inventoried without validation.

## Diagnostics and resource limits

Diagnostics contain controlled code/severity/message key, optional location,
and sorted allowlisted metadata. They exclude document text, XML, filenames,
paths, external targets, stack traces, and dependency messages.

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

Hard-limit failure uses `resource-limit-exceeded`. Cancellation is checked
during package, relationship, body, section, and style traversal.

## Determinism and golden tests

`ParsedDocumentCanonicalProjection` schema `2.0` includes effective formatting
and provenance, omits document text/cache state, serializes in fixed order, and
calculates SHA-256. Repeated and parallel parses must yield the same projection
and hash. The six fixtures are synthetic and parsed from temporary copies whose
source checksums are rechecked.

```powershell
npm run fixtures:generate
npm run fixtures:check
npm run test:docx-parser
```

## Explicitly deferred

S2-T03 may add heading semantics. Still deferred are full table-style cascade,
numbering label/text rendering, rendered-page layout, PPKI validators, findings,
and auto-fix. Raw/direct formatting, style references, and effective formatting
must remain distinct.
