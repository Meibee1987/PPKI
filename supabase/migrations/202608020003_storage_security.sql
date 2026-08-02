-- S1-T03: private Storage buckets with server-only object access.
-- Authenticated browser clients use API-owned signed URLs; no direct object policy is granted.

begin;

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types) values
  ('documents-original', 'documents-original', false, 52428800, array['application/vnd.openxmlformats-officedocument.wordprocessingml.document']),
  ('documents-versions', 'documents-versions', false, 52428800, array['application/vnd.openxmlformats-officedocument.wordprocessingml.document']),
  ('audit-reports', 'audit-reports', false, 52428800, array['application/pdf', 'application/json'])
on conflict (id) do update set
  public = false,
  file_size_limit = excluded.file_size_limit,
  allowed_mime_types = excluded.allowed_mime_types;

-- No anon/authenticated Storage access is required: API and worker use a
-- server credential after database ownership authorization.
revoke all on table storage.objects from anon, authenticated;
revoke all on table storage.buckets from anon, authenticated;

commit;
