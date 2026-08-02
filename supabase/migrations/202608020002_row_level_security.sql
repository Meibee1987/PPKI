-- S1-T02: least-privilege Data API access. Business writes stay server-side.
-- No storage.objects policy is created here; storage policy is S1-T03 scope.

begin;

alter table public.user_profiles enable row level security;
alter table public.document_types enable row level security;
alter table public.formatting_profiles enable row level security;
alter table public.profile_versions enable row level security;
alter table public.profile_rules enable row level security;
alter table public.rules enable row level security;
alter table public.documents enable row level security;
alter table public.document_versions enable row level security;
alter table public.audit_jobs enable row level security;
alter table public.audit_findings enable row level security;

-- FORCE RLS is intentionally not used. The Auth trigger, migration/seeding
-- path, and trusted API/worker need their server-side path; they must still
-- enforce application authorization and never rely on their RLS bypass.

revoke all on table public.user_profiles from anon, authenticated;
revoke all on table public.document_types from anon, authenticated;
revoke all on table public.formatting_profiles from anon, authenticated;
revoke all on table public.profile_versions from anon, authenticated;
revoke all on table public.profile_rules from anon, authenticated;
revoke all on table public.rules from anon, authenticated;
revoke all on table public.documents from anon, authenticated;
revoke all on table public.document_versions from anon, authenticated;
revoke all on table public.audit_jobs from anon, authenticated;
revoke all on table public.audit_findings from anon, authenticated;

grant select on table public.user_profiles to authenticated;
grant select on table public.document_types to authenticated;
grant select on table public.formatting_profiles to authenticated;
grant select on table public.profile_versions to authenticated;
grant select on table public.documents to authenticated;
grant select on table public.document_versions to authenticated;
grant select on table public.audit_jobs to authenticated;
grant select on table public.audit_findings to authenticated;

drop policy if exists "profile read own" on public.user_profiles;
drop policy if exists "profile update own" on public.user_profiles;
drop policy if exists "documents read own" on public.documents;
drop policy if exists "versions read own" on public.document_versions;
drop policy if exists "audits read own" on public.audit_jobs;
drop policy if exists "findings read own" on public.audit_findings;
drop policy if exists "document types read" on public.document_types;
drop policy if exists "profiles read" on public.formatting_profiles;
drop policy if exists "profile versions read" on public.profile_versions;
drop policy if exists "rules read" on public.rules;

create policy user_profiles_select_own on public.user_profiles
  for select to authenticated
  using ((select auth.uid()) is not null and id = (select auth.uid()));

create policy documents_select_own on public.documents
  for select to authenticated
  using ((select auth.uid()) is not null and owner_user_id = (select auth.uid()));

create policy document_versions_select_owned_document on public.document_versions
  for select to authenticated
  using (
    (select auth.uid()) is not null
    and exists (
      select 1
      from public.documents as document
      where document.id = document_versions.document_id
        and document.owner_user_id = (select auth.uid())
    )
  );

create policy audit_jobs_select_owned_document on public.audit_jobs
  for select to authenticated
  using (
    (select auth.uid()) is not null
    and exists (
      select 1
      from public.document_versions as version
      join public.documents as document on document.id = version.document_id
      where version.id = audit_jobs.document_version_id
        and document.owner_user_id = (select auth.uid())
    )
  );

create policy audit_findings_select_owned_document on public.audit_findings
  for select to authenticated
  using (
    (select auth.uid()) is not null
    and exists (
      select 1
      from public.audit_jobs as audit
      join public.document_versions as version on version.id = audit.document_version_id
      join public.documents as document on document.id = version.document_id
      where audit.id = audit_findings.audit_job_id
        and document.owner_user_id = (select auth.uid())
    )
  );

-- The frontend currently supplies document type codes to the API, but this
-- compact public reference is safe for authenticated product clients to read.
create policy document_types_select_authenticated on public.document_types
  for select to authenticated
  using ((select auth.uid()) is not null);

-- Only active, effective PPKI configuration can be viewed directly. Rules and
-- assignments remain API-only because they expose validator implementation data.
create policy profile_versions_select_active on public.profile_versions
  for select to authenticated
  using (
    (select auth.uid()) is not null
    and status = 'Active'
    and (effective_at is null or effective_at <= now())
  );

create policy formatting_profiles_select_active_version on public.formatting_profiles
  for select to authenticated
  using (
    (select auth.uid()) is not null
    and exists (
      select 1
      from public.profile_versions as version
      where version.profile_id = formatting_profiles.id
        and version.status = 'Active'
        and (version.effective_at is null or version.effective_at <= now())
    )
  );

comment on policy document_versions_select_owned_document on public.document_versions is
  'Authenticated users may read versions only through the owning document.';
comment on policy audit_jobs_select_owned_document on public.audit_jobs is
  'requested_by_user_id is not an access grant; document ownership controls read access.';
comment on policy audit_findings_select_owned_document on public.audit_findings is
  'Findings inherit access from audit job to document version to document owner.';

commit;
