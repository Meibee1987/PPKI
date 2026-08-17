# Exact text anchor contract

S5-T05 introduces a read-only, derived targeting contract in `Ppki.DocxEngine`.
It does not alter parser schema `4.0`, persist source text, expose an API, or
perform replacement.

## Canonical text coordinates

The text model is `wordprocessingml-visible-text/scalar-none/1.0`.

- Offsets and lengths count Unicode scalar values, not UTF-16 code units,
  grapheme clusters, bytes, or the incidental indexing behavior of a .NET
  `string`. A non-BMP character therefore has length one. Combining marks are
  separate scalar values.
- No Unicode normalization is performed. Source code points are preserved.
  NFC `é` and decomposed `e` plus U+0301 remain distinct and hash differently.
- `w:t` content is preserved exactly, including U+00A0 NBSP and U+00AD soft
  hyphen. `w:softHyphen` maps to U+00AD and `w:noBreakHyphen` to U+2011.
- `w:tab` maps to U+0009. A line/text-wrapping break and carriage return map to
  U+000A, a page break to U+000C, and a column break to U+000B. No
  platform-specific newline conversion occurs.
- Ordinary run boundaries and hyperlink wrappers add no characters. Hyperlink
  text is targetable and its source spans retain hyperlink membership.
- Field instructions are excluded. Field result text is represented in the
  coordinate stream but any overlapping target is `Unsupported`; text directly
  adjacent to a completed field remains targetable.
- Bookmarks and proofing/noProof markup add no characters and do not change the
  coordinates. Hidden text and inserted/deleted/moved revision text are
  represented only to preserve structural ordering and fail `Unsupported` when
  targeted. Drawings, text boxes, symbols, and unmodelled containers create an
  unsafe boundary; a range crossing one fails closed.

## `text-anchor/1.0`

An exact anchor contains the immutable `DocumentVersionId`, lowercase source
SHA-256, canonical structural paragraph location, text-model version, scalar
start/length, target and paragraph fingerprints, 16-scalar prefix/suffix
fingerprints, and the exact intersecting XML source spans. A span identifies
the run ordinal, child-node ordinal/type, canonical and source-local scalar
range, hyperlink membership, and bold/italic boundaries. It never contains the
paragraph, sentence, target, or replacement text.

Fingerprints are lowercase SHA-256 over UTF-8 domain-separated input:
`domain + LF + exact canonical text`. Canonical anchor serialization uses fixed
field order, invariant decimal formatting, lowercase SHA values, a `D`-format
GUID, LF separators, and source-order spans. Its hash is the domain-separated
SHA-256 fingerprint of that serialization.

## Validation and materialization

Resolution never searches. It reopens the package read-only, checks exact
contract/model, document version, package SHA, structural paragraph location,
paragraph fingerprint, fixed range target fingerprint, context evidence, and
source spans. Any mismatch is `Stale`; unsupported semantic overlap is
`Unsupported`; only a complete match is `Exact`. Context hashes are validation
evidence and are never used to relocate a range.

The materializer returns only structural source segments and offsets. It does
not flatten a paragraph, merge runs, rewrite XML, or mutate the package. Page
maps can join through the same `DocumentElementLocation.ToCompactString()`.
Page number is deliberately absent from anchor identity because pagination may
change between versions.

No database migration or `AuditFinding` JSON change is included. Persisting
this hash-and-coordinate-only anchor in a future task is compatible with the
current privacy posture, but S5-T06 must explicitly govern any source or
replacement text evidence before such text is stored.
