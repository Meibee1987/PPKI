-- S4-T03: persisted, append-only finding remediation evidence.
-- Historical findings remain valid and are not backfilled.

begin;

create table public.finding_resolution_cases (
  id uuid primary key default gen_random_uuid(),
  source_audit_finding_id uuid not null references public.audit_findings(id) on delete restrict,
  source_audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  source_document_version_id uuid not null references public.document_versions(id) on delete restrict,
  created_at timestamptz not null default now(),
  constraint uq_finding_resolution_cases_finding unique (source_audit_finding_id)
);

create table public.finding_resolution_events (
  id uuid primary key default gen_random_uuid(),
  resolution_case_id uuid not null references public.finding_resolution_cases(id) on delete restrict,
  sequence integer not null,
  event_type text not null,
  source_fix_execution_id uuid references public.fix_execution_jobs(id) on delete restrict,
  source_reaudit_job_id uuid references public.audit_jobs(id) on delete restrict,
  result_document_version_id uuid references public.document_versions(id) on delete restrict,
  result_audit_finding_id uuid references public.audit_findings(id) on delete restrict,
  comparison_status text,
  source_occurred_at timestamptz not null,
  created_at timestamptz not null default now(),
  source_event_key text not null,
  constraint uq_finding_resolution_events_sequence unique (resolution_case_id, sequence),
  constraint uq_finding_resolution_events_source_event unique (source_event_key),
  constraint ck_finding_resolution_events_sequence check (sequence > 0),
  constraint ck_finding_resolution_events_source_key check (
    char_length(source_event_key) between 16 and 256
    and source_event_key ~ '^[a-z-]+:[0-9a-f-]{36}:[0-9a-f-]{36}$'),
  constraint ck_finding_resolution_events_type check (event_type in (
    'FixAppliedObserved', 'ReauditPendingObserved',
    'VerificationResolvedObserved', 'VerificationStillDetectedObserved')),
  constraint ck_finding_resolution_events_payload check (
    (event_type = 'FixAppliedObserved'
      and source_fix_execution_id is not null and source_reaudit_job_id is null
      and result_document_version_id is not null and result_audit_finding_id is null
      and comparison_status is null)
    or (event_type = 'ReauditPendingObserved'
      and source_fix_execution_id is not null and source_reaudit_job_id is not null
      and result_document_version_id is not null and result_audit_finding_id is null
      and comparison_status is null)
    or (event_type = 'VerificationResolvedObserved'
      and source_fix_execution_id is not null and source_reaudit_job_id is not null
      and result_document_version_id is not null and result_audit_finding_id is null
      and comparison_status = 'NoLongerDetected')
    or (event_type = 'VerificationStillDetectedObserved'
      and source_fix_execution_id is not null and source_reaudit_job_id is not null
      and result_document_version_id is not null and result_audit_finding_id is not null
      and comparison_status in ('StillDetected', 'Changed'))
  )
);

create index ix_finding_resolution_cases_audit on public.finding_resolution_cases(source_audit_job_id);
create index ix_finding_resolution_cases_version on public.finding_resolution_cases(source_document_version_id);
create index ix_finding_resolution_events_case_sequence on public.finding_resolution_events(resolution_case_id, sequence);
create index ix_finding_resolution_events_fix_execution on public.finding_resolution_events(source_fix_execution_id);
create index ix_finding_resolution_events_reaudit on public.finding_resolution_events(source_reaudit_job_id);

create or replace function private.enforce_finding_resolution_case()
returns trigger language plpgsql set search_path = '' as $$
declare finding_audit uuid; audit_version uuid;
begin
  if tg_op <> 'INSERT' then
    raise exception using errcode = '55000', message = 'Finding resolution case identity is immutable';
  end if;
  select finding.audit_job_id, audit.document_version_id into finding_audit, audit_version
  from public.audit_findings finding join public.audit_jobs audit on audit.id = finding.audit_job_id
  where finding.id = new.source_audit_finding_id;
  if finding_audit is null or finding_audit <> new.source_audit_job_id
    or audit_version <> new.source_document_version_id then
    raise exception using errcode = '23514', message = 'Finding resolution case source identity is invalid';
  end if;
  return new;
end; $$;

create trigger trg_finding_resolution_cases_enforce_insert before insert on public.finding_resolution_cases
  for each row execute function private.enforce_finding_resolution_case();
create trigger trg_finding_resolution_cases_reject_update before update on public.finding_resolution_cases
  for each row execute function private.enforce_finding_resolution_case();
create trigger trg_finding_resolution_cases_reject_delete before delete on public.finding_resolution_cases
  for each row execute function private.enforce_finding_resolution_case();

create or replace function private.enforce_finding_resolution_event()
returns trigger language plpgsql set search_path = '' as $$
declare
  case_finding uuid; case_audit uuid; execution_audit uuid; execution_result uuid;
  execution_state text; reaudit_source uuid; reaudit_execution uuid; reaudit_version uuid;
  reaudit_status text; result_finding_audit uuid; expected_sequence integer;
  expected_event_key text;
begin
  if tg_op <> 'INSERT' then
    raise exception using errcode = '55000', message = 'Finding resolution events are append-only';
  end if;
  perform 1 from public.finding_resolution_cases where id = new.resolution_case_id for update;
  select source_audit_finding_id, source_audit_job_id into case_finding, case_audit
  from public.finding_resolution_cases where id = new.resolution_case_id;
  select coalesce(max(sequence), 0) + 1 into expected_sequence
  from public.finding_resolution_events where resolution_case_id = new.resolution_case_id;
  if case_finding is null or new.sequence <> expected_sequence then
    raise exception using errcode = '23514', message = 'Finding resolution event sequence is invalid';
  end if;
  select state, audit_job_id, result_document_version_id into execution_state, execution_audit, execution_result
  from public.fix_execution_jobs where id = new.source_fix_execution_id;
  if execution_state is distinct from 'Completed' or execution_audit is distinct from case_audit
    or execution_result is distinct from new.result_document_version_id
    or not exists (
      select 1 from public.fix_execution_jobs execution
      where execution.id = new.source_fix_execution_id
        and execution.selected_finding_ids ? case_finding::text) then
    raise exception using errcode = '23514', message = 'Finding resolution fix evidence is invalid';
  end if;
  if new.source_reaudit_job_id is not null then
    select source_audit_job_id, source_fix_execution_id, document_version_id, status
      into reaudit_source, reaudit_execution, reaudit_version, reaudit_status
    from public.audit_jobs where id = new.source_reaudit_job_id;
    if reaudit_source is distinct from case_audit or reaudit_execution is distinct from new.source_fix_execution_id
      or reaudit_version is distinct from new.result_document_version_id then
      raise exception using errcode = '23514', message = 'Finding resolution re-audit evidence is invalid';
    end if;
    if new.event_type = 'ReauditPendingObserved' and reaudit_status not in ('Queued', 'Processing') then
      raise exception using errcode = '23514', message = 'Finding resolution pending evidence is invalid';
    end if;
    if new.event_type like 'Verification%' and reaudit_status <> 'Completed' then
      raise exception using errcode = '23514', message = 'Finding resolution verification evidence is invalid';
    end if;
  end if;
  expected_event_key := case new.event_type
    when 'FixAppliedObserved' then 'fix-applied:' || new.source_fix_execution_id::text || ':' || case_finding::text
    when 'ReauditPendingObserved' then 'reaudit-pending:' || new.source_reaudit_job_id::text || ':' || case_finding::text
    else 'verification:' || new.source_reaudit_job_id::text || ':' || case_finding::text
  end;
  if new.source_event_key is distinct from expected_event_key then
    raise exception using errcode = '23514', message = 'Finding resolution source event identity is invalid';
  end if;
  if new.result_audit_finding_id is not null then
    select audit_job_id into result_finding_audit from public.audit_findings where id = new.result_audit_finding_id;
    if result_finding_audit is distinct from new.source_reaudit_job_id then
      raise exception using errcode = '23514', message = 'Finding resolution result finding evidence is invalid';
    end if;
  end if;
  return new;
end; $$;

create trigger trg_finding_resolution_events_enforce_insert before insert on public.finding_resolution_events
  for each row execute function private.enforce_finding_resolution_event();
create trigger trg_finding_resolution_events_reject_update before update on public.finding_resolution_events
  for each row execute function private.enforce_finding_resolution_event();
create trigger trg_finding_resolution_events_reject_delete before delete on public.finding_resolution_events
  for each row execute function private.enforce_finding_resolution_event();

alter table public.finding_resolution_cases enable row level security;
alter table public.finding_resolution_events enable row level security;
revoke all on table public.finding_resolution_cases, public.finding_resolution_events from anon, authenticated;
grant select on table public.finding_resolution_cases, public.finding_resolution_events to authenticated;
grant select, insert on table public.finding_resolution_cases, public.finding_resolution_events to service_role;
revoke update, delete on table public.finding_resolution_cases, public.finding_resolution_events from service_role;

create policy finding_resolution_cases_select_owned on public.finding_resolution_cases
  for select to authenticated using ((select auth.uid()) is not null and exists (
    select 1 from public.audit_findings finding
    join public.audit_jobs audit on audit.id = finding.audit_job_id
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where finding.id = finding_resolution_cases.source_audit_finding_id
      and document.owner_user_id = (select auth.uid())));

create policy finding_resolution_events_select_owned on public.finding_resolution_events
  for select to authenticated using ((select auth.uid()) is not null and exists (
    select 1 from public.finding_resolution_cases resolution_case
    join public.audit_findings finding on finding.id = resolution_case.source_audit_finding_id
    join public.audit_jobs audit on audit.id = finding.audit_job_id
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    where resolution_case.id = finding_resolution_events.resolution_case_id
      and document.owner_user_id = (select auth.uid())));

comment on table public.finding_resolution_cases is 'Canonical immutable case identity for one historical audit finding.';
comment on table public.finding_resolution_events is 'Append-only remediation evidence; current state is projected from sequence.';

commit;
