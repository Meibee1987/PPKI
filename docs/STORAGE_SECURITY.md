# Storage security contract

All three Supabase Storage buckets are private. Browser clients have no direct
`storage.objects` grant or policy; document business operations remain in the
ASP.NET Core API and worker.

| Bucket | Canonical key | MIME allowlist | Limit | Browser access |
| --- | --- | --- | --- | --- |
| `documents-original` | `{owner}/{document}/{version}/original.docx` | DOCX | 50 MB | none |
| `documents-versions` | `{owner}/{document}/{version}/document.docx` | DOCX | 50 MB | none |
| `audit-reports` | `{owner}/{document}/{audit}.{pdf|json}` | PDF, JSON | 50 MB | none |

All identifiers are lowercase canonical UUIDs. The server constructs keys from
verified entities; a user filename, email, URL, slash/backslash, empty segment,
query, fragment, and traversal text cannot influence a key. `DocumentVersion`
stores the trusted bucket/key only after upload succeeds. The original object
uses `x-upsert: false`; a database save failure triggers best-effort deletion
of the newly uploaded object.

The download endpoint resolves the version through document ownership, verifies
that its stored key equals the canonical original key, then returns a server
created signed URL. Its lifetime is configurable from 120 to 300 seconds and
defaults to 300 seconds. Signed URLs are never persisted or logged. MIME
allowlists are defense in depth: backend extension/MIME checks remain required
because MIME values can be spoofed.

Workers validate the persisted bucket/key before materializing a unique
temporary file and delete that file after parsing. They use server credentials,
not signed URLs, and never overwrite the original object. Full immutable
storage/version enforcement is deferred to S1-T04; append-only audit history is
deferred to S1-T05.

Run `npm run test:storage-local` after `npx supabase db reset` to execute the
local Storage smoke test. It uses only synthetic users and an object, checks
browser denial and server signed-URL access, and cleans all fixtures without
printing keys, tokens, URLs, or response bodies.
