-- S4-T04: internal PPKIAdmin-only manual finding review workflow.
create table public.finding_review_cases (
  id uuid primary key default gen_random_uuid(),
  audit_finding_id uuid not null references public.audit_findings(id) on delete restrict,
  audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  source_document_version_id uuid not null references public.document_versions(id) on delete restrict,
  requested_by_user_id uuid not null references auth.users(id) on delete restrict,
  created_at timestamptz not null default now(),
  constraint uq_finding_review_cases_finding unique (audit_finding_id)
);

create index ix_finding_review_cases_audit on public.finding_review_cases(audit_job_id);

create table public.finding_review_events (
  id uuid primary key default gen_random_uuid(),
  review_case_id uuid not null references public.finding_review_cases(id) on delete restrict,
  sequence integer not null check (sequence > 0),
  event_type text not null check (event_type in
    ('ReviewRequested','ManualRemediationApproved','ManualRemediationReported','NeedsRevision','Rejected','Ignored','AcceptedRisk')),
  requested_disposition text null check (requested_disposition is null or requested_disposition in
    ('ManualRemediation','Ignore','AcceptedRisk')),
  decision text null check (decision is null or decision in
    ('ApproveManualRemediation','Ignore','AcceptRisk','NeedsRevision','Reject')),
  actor_user_id uuid not null references auth.users(id) on delete restrict,
  note text null,
  idempotency_key uuid not null check (idempotency_key <> '00000000-0000-0000-0000-000000000000'),
  source_event_key text not null,
  created_at timestamptz not null default now(),
  constraint uq_finding_review_events_sequence unique (review_case_id, sequence),
  constraint uq_finding_review_events_idempotency unique (review_case_id, idempotency_key),
  constraint uq_finding_review_events_source_event unique (source_event_key),
  constraint ck_finding_review_events_note check
    (note is null or (char_length(note) between 1 and 1000 and note !~ '[[:cntrl:]]')),
  constraint ck_finding_review_events_source_key check
    (source_event_key = 'review-command:' || review_case_id::text || ':' || idempotency_key::text),
  constraint ck_finding_review_events_payload check (
    (event_type = 'ReviewRequested' and requested_disposition is not null and decision is null)
    or (event_type = 'ManualRemediationApproved' and requested_disposition is null and decision = 'ApproveManualRemediation')
    or (event_type = 'NeedsRevision' and requested_disposition is null and decision = 'NeedsRevision')
    or (event_type = 'Rejected' and requested_disposition is null and decision = 'Reject')
    or (event_type = 'Ignored' and requested_disposition is null and decision = 'Ignore')
    or (event_type = 'AcceptedRisk' and requested_disposition is null and decision = 'AcceptRisk')
    or (event_type = 'ManualRemediationReported' and requested_disposition is null and decision is null)
  )
);

create or replace function public.protect_user_profile_role_from_browser()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if old.role is distinct from new.role and auth.uid() is not null then
    raise exception 'User profile role cannot be changed by an authenticated browser.' using errcode = '55000';
  end if;
  return new;
end;
$$;

revoke all on function public.protect_user_profile_role_from_browser() from public, anon, authenticated, service_role;
drop trigger if exists trg_user_profiles_protect_role on public.user_profiles;
create trigger trg_user_profiles_protect_role before update of role on public.user_profiles
for each row execute function public.protect_user_profile_role_from_browser();

create or replace function public.can_ppki_admin_review_finding(p_finding_id uuid)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(exists(
    select 1
    from public.user_profiles actor
    join public.audit_findings finding on finding.id = p_finding_id
    join public.audit_jobs audit on audit.id = finding.audit_job_id
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where actor.id = auth.uid()
      and actor.role = 'PPKIAdmin'
      and document.owner_user_id <> auth.uid()
  ), false);
$$;

revoke all on function public.can_ppki_admin_review_finding(uuid) from public, anon, service_role;
grant execute on function public.can_ppki_admin_review_finding(uuid) to authenticated;

create or replace function public.validate_finding_review_case()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  expected_audit uuid;
  expected_version uuid;
  expected_owner uuid;
begin
  select finding.audit_job_id, audit.document_version_id, document.owner_user_id
    into expected_audit, expected_version, expected_owner
  from public.audit_findings finding
  join public.audit_jobs audit on audit.id = finding.audit_job_id
  join public.document_versions version on version.id = audit.document_version_id
  join public.documents document on document.id = version.document_id
  where finding.id = new.audit_finding_id;
  if expected_audit is null or new.audit_job_id <> expected_audit
    or new.source_document_version_id <> expected_version or new.requested_by_user_id <> expected_owner then
    raise exception 'Finding review case lineage is invalid.' using errcode = '23514';
  end if;
  return new;
end;
$$;

create or replace function public.reject_finding_review_case_mutation()
returns trigger language plpgsql set search_path = '' as $$
begin raise exception 'Finding review case identity is immutable.' using errcode = '55000'; end;
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
  if actor_role is null then raise exception 'Finding review actor is invalid.' using errcode = '23514'; end if;
  select * into previous_event from public.finding_review_events
    where review_case_id = new.review_case_id order by sequence desc limit 1;
  expected_sequence := coalesce(previous_event.sequence, 0) + 1;
  if new.sequence <> expected_sequence then raise exception 'Finding review sequence is invalid.' using errcode = '23514'; end if;

  if new.event_type = 'ReviewRequested' then
    if new.actor_user_id <> review_case.requested_by_user_id then
      raise exception 'Finding review requester is invalid.' using errcode = '42501';
    end if;
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
    if new.actor_user_id <> review_case.requested_by_user_id
      or previous_event.event_type is distinct from 'ManualRemediationApproved' then
      raise exception 'Finding review transition is invalid.' using errcode = '55000';
    end if;
  else
    if actor_role <> 'PPKIAdmin' or new.actor_user_id = review_case.requested_by_user_id then
      raise exception 'Finding review decision is not authorized.' using errcode = '42501';
    end if;
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

create or replace function public.reject_finding_review_event_mutation()
returns trigger language plpgsql set search_path = '' as $$
begin raise exception 'Finding review events are append-only.' using errcode = '55000'; end;
$$;

revoke all on function public.validate_finding_review_case() from public, anon, authenticated, service_role;
revoke all on function public.reject_finding_review_case_mutation() from public, anon, authenticated, service_role;
revoke all on function public.validate_finding_review_event() from public, anon, authenticated, service_role;
revoke all on function public.reject_finding_review_event_mutation() from public, anon, authenticated, service_role;

create trigger trg_finding_review_cases_validate before insert on public.finding_review_cases
for each row execute function public.validate_finding_review_case();
create trigger trg_finding_review_cases_immutable before update or delete on public.finding_review_cases
for each row execute function public.reject_finding_review_case_mutation();
create trigger trg_finding_review_events_validate before insert on public.finding_review_events
for each row execute function public.validate_finding_review_event();
create trigger trg_finding_review_events_append_only before update or delete on public.finding_review_events
for each row execute function public.reject_finding_review_event_mutation();

alter table public.finding_review_cases enable row level security;
alter table public.finding_review_events enable row level security;

revoke all on table public.finding_review_cases from anon, authenticated, service_role;
revoke all on table public.finding_review_events from anon, authenticated, service_role;
grant select on table public.finding_review_cases to authenticated;
grant select on table public.finding_review_events to authenticated;
grant insert on table public.finding_review_cases to service_role;
grant insert on table public.finding_review_events to service_role;

create policy finding_review_cases_select_authorized on public.finding_review_cases
for select to authenticated using (
  exists(select 1 from public.audit_findings finding
    join public.audit_jobs audit on audit.id = finding.audit_job_id
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where finding.id = audit_finding_id and document.owner_user_id = (select auth.uid()))
  or public.can_ppki_admin_review_finding(audit_finding_id)
);

create policy finding_review_events_select_authorized on public.finding_review_events
for select to authenticated using (
  exists(select 1 from public.finding_review_cases review_case
    join public.audit_findings finding on finding.id = review_case.audit_finding_id
    join public.audit_jobs audit on audit.id = finding.audit_job_id
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where review_case.id = review_case_id and
      (document.owner_user_id = (select auth.uid())
       or public.can_ppki_admin_review_finding(review_case.audit_finding_id))
  )
);

comment on table public.finding_review_cases is 'Canonical immutable manual-review identity for one historical finding.';
comment on table public.finding_review_events is 'Append-only owner request and PPKIAdmin decision history; never resolution evidence.';
