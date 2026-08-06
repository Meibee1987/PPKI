-- S4-T04 correction: one internal, database-authoritative PPKIAdmin access model.
begin;

create or replace function public.is_ppki_admin()
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(exists(
    select 1 from public.user_profiles profile
    where profile.id = auth.uid() and profile.role = 'PPKIAdmin'
  ), false);
$$;

revoke all on function public.is_ppki_admin() from public, anon, service_role;
grant execute on function public.is_ppki_admin() to authenticated;

create or replace function public.protect_user_profile_delete_from_browser()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if auth.uid() is not null then
    raise exception 'User profiles cannot be deleted by an authenticated browser.' using errcode = '55000';
  end if;
  return old;
end;
$$;

revoke all on function public.protect_user_profile_delete_from_browser()
  from public, anon, authenticated, service_role;
drop trigger if exists trg_user_profiles_protect_delete on public.user_profiles;
create trigger trg_user_profiles_protect_delete before delete on public.user_profiles
for each row execute function public.protect_user_profile_delete_from_browser();

-- The applied S4-T04 migration required a distinct owner/admin. The internal
-- application deliberately permits operational self-approval, while retaining
-- exact role lookup, actor attribution, transition checks, and append-only data.
create or replace function public.validate_finding_review_case()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  expected_audit uuid;
  expected_version uuid;
  requester_role text;
begin
  select finding.audit_job_id, audit.document_version_id
    into expected_audit, expected_version
  from public.audit_findings finding
  join public.audit_jobs audit on audit.id = finding.audit_job_id
  where finding.id = new.audit_finding_id;
  select role into requester_role from public.user_profiles where id = new.requested_by_user_id;
  if expected_audit is null or new.audit_job_id <> expected_audit
    or new.source_document_version_id <> expected_version or requester_role is distinct from 'PPKIAdmin' then
    raise exception 'Finding review case lineage or requester is invalid.' using errcode = '23514';
  end if;
  return new;
end;
$$;

create or replace function public.validate_finding_review_event()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  review_case public.finding_review_cases%rowtype;
  previous_event public.finding_review_events%rowtype;
  actor_role text;
  expected_sequence integer;
  verified_resolved boolean;
begin
  select * into review_case from public.finding_review_cases where id = new.review_case_id for update;
  if review_case.id is null then raise exception 'Finding review case is invalid.' using errcode = '23503'; end if;
  select role into actor_role from public.user_profiles where id = new.actor_user_id;
  if actor_role is distinct from 'PPKIAdmin' then
    raise exception 'Finding review actor is not an internal administrator.' using errcode = '42501';
  end if;
  select * into previous_event from public.finding_review_events
    where review_case_id = new.review_case_id order by sequence desc limit 1;
  expected_sequence := coalesce(previous_event.sequence, 0) + 1;
  if new.sequence <> expected_sequence then raise exception 'Finding review sequence is invalid.' using errcode = '23514'; end if;

  if new.event_type = 'ReviewRequested' then
    select coalesce((select event_type = 'VerificationResolvedObserved'
      from public.finding_resolution_events resolution_event
      join public.finding_resolution_cases resolution_case on resolution_case.id = resolution_event.resolution_case_id
      where resolution_case.source_audit_finding_id = review_case.audit_finding_id
      order by resolution_event.sequence desc limit 1), false) into verified_resolved;
    if verified_resolved then raise exception 'Finding is already verified resolved.' using errcode = '55000'; end if;
    if previous_event.id is not null and previous_event.event_type <> 'NeedsRevision' then
      raise exception 'Finding review transition is invalid.' using errcode = '55000';
    end if;
  elsif new.event_type = 'ManualRemediationReported' then
    if previous_event.event_type is distinct from 'ManualRemediationApproved' then
      raise exception 'Finding review transition is invalid.' using errcode = '55000';
    end if;
  else
    if previous_event.event_type is distinct from 'ReviewRequested' then
      raise exception 'Finding review transition is invalid.' using errcode = '55000';
    end if;
    if previous_event.requested_disposition = 'ManualRemediation'
       and new.event_type not in ('ManualRemediationApproved','NeedsRevision','Rejected') then
      raise exception 'Finding review decision does not match request.' using errcode = '55000';
    elsif previous_event.requested_disposition = 'Ignore'
       and new.event_type not in ('Ignored','NeedsRevision','Rejected') then
      raise exception 'Finding review decision does not match request.' using errcode = '55000';
    elsif previous_event.requested_disposition = 'AcceptedRisk'
       and new.event_type not in ('AcceptedRisk','NeedsRevision','Rejected') then
      raise exception 'Finding review decision does not match request.' using errcode = '55000';
    end if;
  end if;
  return new;
end;
$$;

-- Replace every authenticated read predicate with the same exact admin gate.
drop policy if exists user_profiles_select_own on public.user_profiles;
drop policy if exists documents_select_own on public.documents;
drop policy if exists document_versions_select_owned_document on public.document_versions;
drop policy if exists audit_jobs_select_owned_document on public.audit_jobs;
drop policy if exists audit_findings_select_owned_document on public.audit_findings;
drop policy if exists document_types_select_authenticated on public.document_types;
drop policy if exists profile_versions_select_active on public.profile_versions;
drop policy if exists formatting_profiles_select_active_version on public.formatting_profiles;
drop policy if exists audit_rule_snapshots_select_owned_document on public.audit_rule_snapshots;
drop policy if exists fix_execution_jobs_select_owned_document on public.fix_execution_jobs;
drop policy if exists finding_resolution_cases_select_owned on public.finding_resolution_cases;
drop policy if exists finding_resolution_events_select_owned on public.finding_resolution_events;
drop policy if exists finding_review_cases_select_authorized on public.finding_review_cases;
drop policy if exists finding_review_events_select_authorized on public.finding_review_events;

create policy user_profiles_select_internal_admin on public.user_profiles for select to authenticated
  using (public.is_ppki_admin() and id = (select auth.uid()));
create policy documents_select_internal_admin on public.documents for select to authenticated
  using (public.is_ppki_admin() and owner_user_id = (select auth.uid()));
create policy document_versions_select_internal_admin on public.document_versions for select to authenticated
  using (public.is_ppki_admin() and exists(select 1 from public.documents document
    where document.id = document_versions.document_id and document.owner_user_id = (select auth.uid())));
create policy audit_jobs_select_internal_admin on public.audit_jobs for select to authenticated
  using (public.is_ppki_admin() and exists(select 1 from public.document_versions version
    join public.documents document on document.id = version.document_id
    where version.id = audit_jobs.document_version_id and document.owner_user_id = (select auth.uid())));
create policy audit_findings_select_internal_admin on public.audit_findings for select to authenticated
  using (public.is_ppki_admin() and exists(select 1 from public.audit_jobs audit
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where audit.id = audit_findings.audit_job_id and document.owner_user_id = (select auth.uid())));
create policy document_types_select_internal_admin on public.document_types for select to authenticated
  using (public.is_ppki_admin());
create policy profile_versions_select_internal_admin on public.profile_versions for select to authenticated
  using (public.is_ppki_admin() and status = 'Active' and (effective_at is null or effective_at <= now()));
create policy formatting_profiles_select_internal_admin on public.formatting_profiles for select to authenticated
  using (public.is_ppki_admin() and exists(select 1 from public.profile_versions version
    where version.profile_id = formatting_profiles.id and version.status = 'Active'
      and (version.effective_at is null or version.effective_at <= now())));
create policy audit_rule_snapshots_select_internal_admin on public.audit_rule_snapshots for select to authenticated
  using (public.is_ppki_admin() and exists(select 1 from public.audit_jobs audit
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where audit.id = audit_rule_snapshots.audit_job_id and document.owner_user_id = (select auth.uid())));
create policy fix_execution_jobs_select_internal_admin on public.fix_execution_jobs for select to authenticated
  using (public.is_ppki_admin() and exists(select 1 from public.audit_jobs audit
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where audit.id = fix_execution_jobs.audit_job_id and document.owner_user_id = (select auth.uid())));
create policy finding_resolution_cases_select_internal_admin on public.finding_resolution_cases for select to authenticated
  using (public.is_ppki_admin() and exists(select 1 from public.audit_jobs audit
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where audit.id = finding_resolution_cases.source_audit_job_id and document.owner_user_id = (select auth.uid())));
create policy finding_resolution_events_select_internal_admin on public.finding_resolution_events for select to authenticated
  using (public.is_ppki_admin() and exists(select 1 from public.finding_resolution_cases resolution_case
    join public.audit_jobs audit on audit.id = resolution_case.source_audit_job_id
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where resolution_case.id = finding_resolution_events.resolution_case_id
      and document.owner_user_id = (select auth.uid())));
create policy finding_review_cases_select_internal_admin on public.finding_review_cases for select to authenticated
  using (public.is_ppki_admin());
create policy finding_review_events_select_internal_admin on public.finding_review_events for select to authenticated
  using (public.is_ppki_admin());

comment on function public.is_ppki_admin() is
  'Fail-closed authenticated role predicate; exact role is read from public.user_profiles.';
comment on table public.finding_review_events is
  'Append-only internal PPKIAdmin operational self-approval history; never resolution evidence.';

commit;
