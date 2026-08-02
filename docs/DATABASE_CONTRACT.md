# Database contract

`202608010001_initial_schema.sql` is treated as applied, immutable migration
history. S1-T01 adds `202608020001_ownership_integrity.sql`; it is additive and
does not invent values for historical data.

## Ownership

```text
auth.users
  -> user_profiles
  -> documents
  -> document_versions
  -> audit_jobs
  -> audit_findings
```

`user_profiles.id` is the `auth.users.id` UUID and has one profile per auth
user. Its FK uses `CASCADE`; however, deletion of an Auth user with owned
documents, created versions, or requested jobs is blocked by their `NO ACTION`/
`RESTRICT` relationships until an explicit retention/disposal workflow exists.
`documents.owner_user_id`,
`document_versions.created_by_user_id`, and (for all new jobs)
`audit_jobs.requested_by_user_id` reference `auth.users(id)`. An audit worker
is an executor, never the requester. No email is a foreign key and the API
derives these IDs from the authenticated principal rather than request input.

Findings have no separate owner. Their ownership is derived through audit job,
document version, and document. S1-T02 enables RLS for all application tables:
authenticated users can only read their ownership chain and cannot write
business data directly. Storage policies remain deferred to S1-T03.

## Tables and relationships

| Table | Responsibility | Delete behavior |
| --- | --- | --- |
| `user_profiles` | One profile per Auth user | Auth user cascade, subject to other Auth references blocking user deletion |
| `document_types` | Reference type and academic work kind | referenced by documents with no action |
| `formatting_profiles` | Named PPKI source edition | referenced by profile versions with no action |
| `profile_versions` | Versioned configuration status | profile no action; assignments cascade |
| `rules` | Stable, source-data rule definition | referenced by assignments/findings with no action/restrict |
| `documents` | One user-owned thesis document and its type | document type is no action; auth owner is no action |
| `document_versions` | Immutable-in-concept uploaded version metadata | deleting a document cascades to its versions; a parent version is restricted |
| `audit_jobs` | One requested audit of one document version against one profile version | document/profile versions are restricted; requester auth user is restricted |
| `audit_findings` | Result rows belonging to one audit | deleting an audit job cascades to its findings; rule is restricted |
| `profile_rules` | Rule assignment for one profile version | profile version cascade; rule restrict |

`formatting_profiles -> profile_versions` is restricted and version numbers are
unique per profile. `profile_versions -> profile_rules -> rules` prevents a
duplicate rule assignment. Rule codes remain unique and stable.

## Integrity rules

- Documents require an owner, valid document type, non-blank title of at most
  512 characters, `Active` or `Archived` status, and `updated_at >= created_at`.
- Versions use the repository names `version_no`, `storage_key`, and
  `size_bytes`. Their version number is positive and unique per document;
  versions after version 1 require a parent; the parent cannot be itself and a
  trigger requires it to belong to the same document.
- A checksum is lowercase hexadecimal SHA-256, exactly 64 characters.
  `size_bytes` is positive. A storage key is a non-empty private logical key:
  it cannot be a URL, root path, backslash path, or contain `..`.
- Audit job statuses are `Queued`, `Processing`, `Completed`, `Failed`, and
  `Cancelled`. Terminal jobs require `completed_at`; completion cannot precede
  start; completed jobs require a lowercase 64-character resolved rule-set
  hash. Counts cannot be negative. Persisted failure text is generic, while
  detailed diagnostics remain worker-only.
- Findings retain rule-code, severity, fix-mode, and supported source-reference
  snapshots (source section, PDF page, and printed page) alongside JSON
  actual/expected/location values. JSON is an object,
  array, or JSON `null`; JSON null is permitted when a validator has no actual
  or expected value. Findings never contain complete DOCX or paragraph text.
  Severity is `Error`, `Warning`, or `Info`; fix mode is `Auto`, `Confirm`,
  `Manual`, or `Report`.
- Profile version statuses are `Draft`, `Active`, or `Retired`.

The new checks are `NOT VALID` to avoid a data-lossful or arbitrary backfill.
PostgreSQL enforces them for every new or changed row. In particular,
`requested_by_user_id` and finding snapshots are required for new rows; legacy
rows may remain incomplete until an auditable remediation migration is chosen.

## Immutability and future work

Document versions and audit results are immutable by product contract. Full
append-only/immutability triggers are deferred to S1-T04/S1-T05. RLS policy and
least-privilege grants are defined by S1-T02; see
[DATABASE_SECURITY.md](DATABASE_SECURITY.md) for the access matrix.
