# Canonical document preview contract

S5-T04 renders each immutable `DocumentVersion` with the container image
`gotenberg/gotenberg:8.34.0-libreoffice` pinned to digest
`sha256:3c23aeb3a027a63d7c71745fc9d83724bd58cf9dfa470396ac82c0896028db2a`.
The application contract records renderer version
`8.34.0+libreoffice-26.2.4.2`, `docx-pdf/1.0`, font profile
`ppki-liberation-noto/1.0`, and page-map schema `page-map/1.0`. Local and CI
must use this same image digest; an unpinned host LibreOffice is not canonical.

The image's bundled Liberation and Noto open-font families form the documented
layout profile. `Exact` therefore means the exact page in the canonical PPKI
preview PDF. It does not claim page parity with arbitrary Microsoft Word
installations or proprietary fonts that are not part of this profile.

The worker copies the source DOCX to a unique temporary directory, injects
zero-width structural bookmarks into that render-only copy, requests bookmark
destinations from Gotenberg, and resolves the resulting PDF named destinations
to one-based pages. Paragraph and run anchors are independent. Missing anchors
are `Unavailable`; S5-T04 does not emit `Estimated` because no bounded estimate
algorithm is defined. Duplicate text cannot collide because no document text is
used to resolve a destination.

Render identity is the SHA-256 of document-version ID, source SHA-256, renderer
ID/version/contract, and font-profile version. Jobs are database-backed,
lease-claimed, bounded to three attempts, and persisted as `Pending`,
`Processing`, `Completed`, or `Failed`. A completed immutable PDF and its page
map are published create-only under a server-owned key. A renderer contract
change creates a different identity and never mutates historical artifacts.

Input and output size are limited to 50 MiB and execution is time-bounded.
Original filenames never become local paths, temporary data is cleaned up, and
the worker never logs document text. The renderer route disables index updates
and form-field export; the pinned Gotenberg runtime disables macros and blocks
linked external content. Preview reads remain authorized API GET requests and
never expose storage object paths.
