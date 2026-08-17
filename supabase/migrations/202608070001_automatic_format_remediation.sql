-- S5-T03: durable, one-pass automatic formatting remediation orchestration.
-- migration-hygiene: allow-destructive replace-fix-selection-bound-for-one-batch-auto-policy

begin;

alter table public.fix_execution_jobs drop constraint ck_fix_execution_json;
alter table public.fix_execution_jobs add constraint ck_fix_execution_json check (
  jsonb_typeof(selected_finding_ids) = 'array'
  and jsonb_array_length(selected_finding_ids) between 1 and 10000
  and jsonb_typeof(approved_plan_snapshot) = 'object'
);

create table public.automatic_remediation_orchestrations (
  id uuid primary key default gen_random_uuid(),
  source_audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  orchestration_type text not null,
  policy_version text not null,
  state text not null default 'Pending',
  eligible_finding_count integer not null default 0,
  operation_count integer not null default 0,
  fix_execution_id uuid references public.fix_execution_jobs(id) on delete restrict,
  result_document_version_id uuid references public.document_versions(id) on delete restrict,
  reaudit_job_id uuid references public.audit_jobs(id) on delete restrict,
  safe_failure_code text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint uq_automatic_remediation_identity
    unique (source_audit_job_id, orchestration_type, policy_version),
  constraint uq_automatic_remediation_fix_execution unique (fix_execution_id),
  constraint uq_automatic_remediation_reaudit unique (reaudit_job_id),
  constraint ck_automatic_remediation_identity check (
    orchestration_type = 'AutoFormat' and policy_version = 'auto-format/1.0'),
  constraint ck_automatic_remediation_state check (
    state in ('Pending','NoAction','Queued','Processing','ReauditPending','Completed','Failed','Conflict')),
  constraint ck_automatic_remediation_counts check (
    eligible_finding_count between 0 and 10000
    and operation_count between 0 and 10000),
  constraint ck_automatic_remediation_safe_failure check (
    safe_failure_code is null or
    (char_length(safe_failure_code) between 1 and 128 and safe_failure_code ~ '^[a-z0-9][a-z0-9.-]*$')),
  constraint ck_automatic_remediation_timestamps check (updated_at >= created_at)
);

create index ix_automatic_remediation_worker
  on public.automatic_remediation_orchestrations(state, updated_at)
  where state in ('Pending','Queued','Processing','ReauditPending');

create or replace function private.enforce_automatic_remediation_orchestration()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  source_fix uuid;
  execution_audit uuid;
  execution_result uuid;
  reaudit_source_fix uuid;
begin
  if tg_op = 'DELETE' then
    raise exception using errcode = '55000', message = 'Automatic remediation orchestration cannot be deleted';
  end if;

  select source_fix_execution_id into source_fix
  from public.audit_jobs where id = new.source_audit_job_id;
  if source_fix is not null then
    raise exception using errcode = '23514', message = 'Automatic remediation can only target an initial audit';
  end if;

  if tg_op = 'INSERT' then
    if new.state <> 'Pending' or new.eligible_finding_count <> 0 or new.operation_count <> 0
      or new.fix_execution_id is not null or new.result_document_version_id is not null
      or new.reaudit_job_id is not null or new.safe_failure_code is not null then
      raise exception using errcode = '23514', message = 'Automatic remediation must start pending';
    end if;
  else
    if old.id is distinct from new.id
      or old.source_audit_job_id is distinct from new.source_audit_job_id
      or old.orchestration_type is distinct from new.orchestration_type
      or old.policy_version is distinct from new.policy_version
      or old.created_at is distinct from new.created_at then
      raise exception using errcode = '55000', message = 'Automatic remediation identity is immutable';
    end if;
    if old.state in ('NoAction','Completed','Failed','Conflict') then
      raise exception using errcode = '55000', message = 'Terminal automatic remediation is immutable';
    end if;
    if new.state is distinct from old.state and not (
      (old.state = 'Pending' and new.state in ('NoAction','Queued','Failed','Conflict'))
      or (old.state = 'Queued' and new.state in ('Processing','ReauditPending','Completed','Failed','Conflict'))
      or (old.state = 'Processing' and new.state in ('ReauditPending','Completed','Failed','Conflict'))
      or (old.state = 'ReauditPending' and new.state in ('Completed','Failed'))
    ) then
      raise exception using errcode = '23514', message = 'Invalid automatic remediation transition';
    end if;
    if old.fix_execution_id is not null and old.fix_execution_id is distinct from new.fix_execution_id
      or old.result_document_version_id is not null and old.result_document_version_id is distinct from new.result_document_version_id
      or old.reaudit_job_id is not null and old.reaudit_job_id is distinct from new.reaudit_job_id then
      raise exception using errcode = '55000', message = 'Automatic remediation lineage is immutable';
    end if;
  end if;

  if new.fix_execution_id is not null then
    select audit_job_id, result_document_version_id into execution_audit, execution_result
    from public.fix_execution_jobs where id = new.fix_execution_id;
    if execution_audit is distinct from new.source_audit_job_id
      or new.result_document_version_id is not null
         and execution_result is distinct from new.result_document_version_id then
      raise exception using errcode = '23514', message = 'Automatic remediation execution lineage is invalid';
    end if;
  end if;
  if new.reaudit_job_id is not null then
    select source_fix_execution_id into reaudit_source_fix
    from public.audit_jobs where id = new.reaudit_job_id;
    if reaudit_source_fix is distinct from new.fix_execution_id then
      raise exception using errcode = '23514', message = 'Automatic remediation re-audit lineage is invalid';
    end if;
  end if;
  if new.state = 'NoAction' and (new.eligible_finding_count <> 0 or new.operation_count <> 0
      or new.fix_execution_id is not null or new.result_document_version_id is not null or new.reaudit_job_id is not null) then
    raise exception using errcode = '23514', message = 'No-action automatic remediation has invalid evidence';
  end if;
  if new.state = 'ReauditPending' and (new.fix_execution_id is null
      or new.result_document_version_id is null or new.reaudit_job_id is null) then
    raise exception using errcode = '23514', message = 'Pending re-audit lineage is incomplete';
  end if;
  if new.state in ('Failed','Conflict') and new.safe_failure_code is null then
    raise exception using errcode = '23514', message = 'Automatic remediation terminal failure must be safe';
  end if;
  return new;
end;
$$;

create trigger trg_automatic_remediation_enforce_insert
  before insert on public.automatic_remediation_orchestrations
  for each row execute function private.enforce_automatic_remediation_orchestration();
create trigger trg_automatic_remediation_enforce_update
  before update on public.automatic_remediation_orchestrations
  for each row execute function private.enforce_automatic_remediation_orchestration();
create trigger trg_automatic_remediation_reject_delete
  before delete on public.automatic_remediation_orchestrations
  for each row execute function private.enforce_automatic_remediation_orchestration();

alter table public.automatic_remediation_orchestrations enable row level security;
revoke all on table public.automatic_remediation_orchestrations from anon, authenticated;
grant select on table public.automatic_remediation_orchestrations to authenticated;
grant select, insert, update on table public.automatic_remediation_orchestrations to service_role;
revoke delete on table public.automatic_remediation_orchestrations from service_role;

create policy automatic_remediation_select_internal_admin
  on public.automatic_remediation_orchestrations
  for select to authenticated
  using (
    exists (
      select 1 from public.user_profiles as profile
      where profile.id = (select auth.uid()) and profile.role = 'PPKIAdmin'
    )
  );

comment on table public.automatic_remediation_orchestrations is
  'Canonical durable one-pass orchestration evidence for exact versioned automatic formatting policy.';

commit;
