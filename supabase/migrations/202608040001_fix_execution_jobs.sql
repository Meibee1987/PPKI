-- S3-T04: immutable approved fix plans and asynchronous, idempotent execution.
-- This migration is additive; historical audits and versions need no backfill.

begin;

create table public.fix_execution_jobs (
  id uuid primary key default gen_random_uuid(),
  audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  source_document_version_id uuid not null references public.document_versions(id) on delete restrict,
  result_document_version_id uuid references public.document_versions(id) on delete restrict,
  requested_by_user_id uuid not null references auth.users(id) on delete restrict,
  idempotency_key uuid not null,
  plan_hash text not null,
  planner_version text not null,
  selected_finding_ids jsonb not null,
  approved_plan_snapshot jsonb not null,
  state text not null default 'Queued',
  planned_operation_count integer not null,
  completed_operation_count integer not null default 0,
  failed_operation_count integer not null default 0,
  result_sha256 text,
  safe_failure_code text,
  started_at timestamptz,
  lease_expires_at timestamptz,
  completed_at timestamptz,
  created_at timestamptz not null default now(),
  constraint uq_fix_execution_audit_idempotency unique (audit_job_id, idempotency_key),
  constraint uq_fix_execution_source_plan unique (source_document_version_id, plan_hash),
  constraint uq_fix_execution_result_version unique (result_document_version_id),
  constraint ck_fix_execution_hashes check (
    plan_hash ~ '^[0-9a-f]{64}$'
    and (result_sha256 is null or result_sha256 ~ '^[0-9a-f]{64}$')
  ),
  constraint ck_fix_execution_json check (
    jsonb_typeof(selected_finding_ids) = 'array'
    and jsonb_array_length(selected_finding_ids) between 1 and 100
    and jsonb_typeof(approved_plan_snapshot) = 'object'
  ),
  constraint ck_fix_execution_counts check (
    planned_operation_count > 0
    and completed_operation_count between 0 and planned_operation_count
    and failed_operation_count between 0 and planned_operation_count
  ),
  constraint ck_fix_execution_state check (state in ('Queued','Processing','Completed','Failed','NoChange')),
  constraint ck_fix_execution_safe_failure check (
    safe_failure_code is null
    or (char_length(safe_failure_code) between 1 and 128 and safe_failure_code ~ '^[a-z0-9][a-z0-9.-]*$')
  )
);

create index ix_fix_execution_jobs_worker_queue
  on public.fix_execution_jobs(state, created_at)
  where state in ('Queued', 'Processing');
create index ix_fix_execution_jobs_audit
  on public.fix_execution_jobs(audit_job_id, created_at desc);

create or replace function private.enforce_fix_execution_job()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  audit_source uuid;
  document_owner uuid;
  source_document uuid;
  result_document uuid;
  result_parent uuid;
  result_creator uuid;
  result_hash text;
begin
  if tg_op = 'DELETE' then
    raise exception using errcode = '55000', message = 'Fix execution jobs cannot be deleted';
  end if;

  if tg_op = 'INSERT' then
    if new.state <> 'Queued' or new.result_document_version_id is not null
      or new.started_at is not null or new.lease_expires_at is not null or new.completed_at is not null
      or new.completed_operation_count <> 0 or new.failed_operation_count <> 0
      or new.result_sha256 is not null or new.safe_failure_code is not null then
      raise exception using errcode = '23514', message = 'Fix execution must be inserted as a clean queued job';
    end if;

    select audit.document_version_id, document.owner_user_id
      into audit_source, document_owner
    from public.audit_jobs as audit
    join public.document_versions as version on version.id = audit.document_version_id
    join public.documents as document on document.id = version.document_id
    where audit.id = new.audit_job_id and audit.status = 'Completed';
    if audit_source is null or audit_source <> new.source_document_version_id
      or document_owner <> new.requested_by_user_id then
      raise exception using errcode = '23514', message = 'Fix execution source identity is invalid';
    end if;
  else
    if old.id is distinct from new.id or old.audit_job_id is distinct from new.audit_job_id
      or old.source_document_version_id is distinct from new.source_document_version_id
      or old.requested_by_user_id is distinct from new.requested_by_user_id
      or old.idempotency_key is distinct from new.idempotency_key
      or old.plan_hash is distinct from new.plan_hash
      or old.planner_version is distinct from new.planner_version
      or old.selected_finding_ids is distinct from new.selected_finding_ids
      or old.approved_plan_snapshot is distinct from new.approved_plan_snapshot
      or old.planned_operation_count is distinct from new.planned_operation_count
      or old.created_at is distinct from new.created_at then
      raise exception using errcode = '55000', message = 'Fix execution request and approved plan are immutable';
    end if;
    if old.state in ('Completed','Failed','NoChange') then
      raise exception using errcode = '55000', message = 'Terminal fix execution is immutable';
    end if;
    if new.state is distinct from old.state and not (
      (old.state = 'Queued' and new.state = 'Processing')
      or (old.state = 'Processing' and new.state in ('Completed','Failed','NoChange'))
    ) then
      raise exception using errcode = '23514', message = 'Invalid fix execution state transition';
    end if;
    if old.state = 'Processing' and new.state = 'Processing' and (
      old.started_at is distinct from new.started_at
      or old.result_document_version_id is distinct from new.result_document_version_id
      or old.completed_operation_count is distinct from new.completed_operation_count
      or old.failed_operation_count is distinct from new.failed_operation_count
      or old.result_sha256 is distinct from new.result_sha256
      or old.safe_failure_code is distinct from new.safe_failure_code
      or old.completed_at is distinct from new.completed_at) then
      raise exception using errcode = '55000', message = 'Only the processing lease may be renewed';
    end if;
    if old.result_document_version_id is not null
      and old.result_document_version_id is distinct from new.result_document_version_id then
      raise exception using errcode = '55000', message = 'Fix execution result lineage is immutable';
    end if;
  end if;

  if new.state = 'Queued' and (new.started_at is not null or new.lease_expires_at is not null or new.completed_at is not null) then
    raise exception using errcode = '23514', message = 'Queued fix execution timestamps are invalid';
  end if;
  if new.state = 'Processing' and (new.started_at is null or new.lease_expires_at is null or new.completed_at is not null) then
    raise exception using errcode = '23514', message = 'Processing fix execution timestamps are invalid';
  end if;
  if new.state = 'Processing' and (
    new.result_document_version_id is not null or new.result_sha256 is not null
    or new.safe_failure_code is not null or new.completed_operation_count <> 0
    or new.failed_operation_count <> 0) then
    raise exception using errcode = '23514', message = 'Processing fix execution result fields are invalid';
  end if;
  if new.state in ('Completed','Failed','NoChange') and (new.started_at is null or new.completed_at is null or new.lease_expires_at is not null) then
    raise exception using errcode = '23514', message = 'Terminal fix execution timestamps are invalid';
  end if;
  if new.completed_at is not null and new.completed_at < new.started_at then
    raise exception using errcode = '23514', message = 'Fix execution timestamps are out of order';
  end if;
  if new.state = 'Completed' and (
    new.result_document_version_id is null or new.result_sha256 is null
    or new.completed_operation_count <> new.planned_operation_count or new.failed_operation_count <> 0
    or new.safe_failure_code is not null) then
    raise exception using errcode = '23514', message = 'Completed fix execution result is invalid';
  end if;
  if new.state = 'Completed' then
    select source.document_id into source_document
    from public.document_versions as source
    where source.id = new.source_document_version_id;
    select result.document_id, result.parent_version_id, result.created_by_user_id, result.sha256
      into result_document, result_parent, result_creator, result_hash
    from public.document_versions as result
    where result.id = new.result_document_version_id;
    if result_document is null or result_document <> source_document
      or result_parent <> new.source_document_version_id
      or result_creator <> new.requested_by_user_id
      or result_hash <> new.result_sha256 then
      raise exception using errcode = '23514', message = 'Fix execution result ownership chain is invalid';
    end if;
  end if;
  if new.state = 'NoChange' and (
    new.result_document_version_id is not null or new.result_sha256 is not null
    or new.completed_operation_count <> new.planned_operation_count or new.failed_operation_count <> 0
    or new.safe_failure_code is not null) then
    raise exception using errcode = '23514', message = 'No-change fix execution result is invalid';
  end if;
  if new.state = 'Failed' and (
    new.result_document_version_id is not null or new.result_sha256 is not null
    or new.safe_failure_code is null or new.failed_operation_count = 0
    or new.completed_operation_count <> 0) then
    raise exception using errcode = '23514', message = 'Failed fix execution result is invalid';
  end if;
  return new;
end;
$$;

create trigger trg_fix_execution_jobs_enforce_insert
  before insert on public.fix_execution_jobs
  for each row execute function private.enforce_fix_execution_job();
create trigger trg_fix_execution_jobs_enforce_update
  before update on public.fix_execution_jobs
  for each row execute function private.enforce_fix_execution_job();
create trigger trg_fix_execution_jobs_reject_delete
  before delete on public.fix_execution_jobs
  for each row execute function private.enforce_fix_execution_job();

alter table public.fix_execution_jobs enable row level security;
revoke all on table public.fix_execution_jobs from anon, authenticated;
grant select on table public.fix_execution_jobs to authenticated;
grant select, insert, update on table public.fix_execution_jobs to service_role;
revoke delete on table public.fix_execution_jobs from service_role;

create policy fix_execution_jobs_select_owned_document on public.fix_execution_jobs
  for select to authenticated
  using (
    (select auth.uid()) is not null
    and exists (
      select 1
      from public.audit_jobs as audit
      join public.document_versions as version on version.id = audit.document_version_id
      join public.documents as document on document.id = version.document_id
      where audit.id = fix_execution_jobs.audit_job_id
        and document.owner_user_id = (select auth.uid())
    )
  );

comment on table public.fix_execution_jobs is
  'Immutable approved fix-plan snapshots plus append-only execution lifecycle and source/result lineage.';

commit;
