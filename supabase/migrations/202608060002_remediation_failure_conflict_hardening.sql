-- S4-T05: typed failures, bounded retry, lease fencing, and safe publish evidence.
begin;

alter table public.fix_execution_jobs
  add column claim_token uuid,
  add column attempt_count integer not null default 0,
  add column max_attempts integer not null default 3,
  add column next_attempt_at timestamptz,
  add column failure_category text,
  add column result_object_size bigint,
  add column object_created_by_attempt integer;

drop trigger trg_fix_execution_jobs_enforce_insert on public.fix_execution_jobs;
drop trigger trg_fix_execution_jobs_enforce_update on public.fix_execution_jobs;
drop trigger trg_fix_execution_jobs_reject_delete on public.fix_execution_jobs;

update public.fix_execution_jobs
set attempt_count = case when state = 'Queued' then 0 else 1 end,
    claim_token = case when state = 'Processing' then gen_random_uuid() else null end,
    failure_category = case when state = 'Failed' then 'TerminalInfrastructure' else null end,
    result_object_size = case when state = 'Completed' then
      (select size_bytes from public.document_versions where id = result_document_version_id) else null end;

alter table public.fix_execution_jobs
  add constraint ck_fix_execution_attempts check
    (max_attempts = 3 and attempt_count between 0 and max_attempts),
  add constraint ck_fix_execution_failure_category check
    (failure_category is null or failure_category in
      ('Conflict','InvalidInput','InvalidSource','InvalidPlan','CapabilityUnavailable',
       'TransientInfrastructure','TerminalInfrastructure')),
  add constraint ck_fix_execution_claim_state check
    ((state = 'Processing' and claim_token is not null)
      or (state <> 'Processing' and claim_token is null)),
  add constraint ck_fix_execution_result_object check
    ((state = 'Completed' and result_object_size > 0)
      or (state <> 'Completed' and result_object_size is null and object_created_by_attempt is null));

create or replace function private.enforce_fix_execution_job()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  audit_source uuid;
  requester_role text;
  source_document uuid;
  source_version_no integer;
  document_current integer;
  result_document uuid;
  result_parent uuid;
  result_creator uuid;
  result_hash text;
  result_size bigint;
begin
  if tg_op = 'DELETE' then
    raise exception using errcode = '55000', message = 'Fix execution jobs cannot be deleted';
  end if;

  if tg_op = 'INSERT' then
    if new.state <> 'Queued' or new.result_document_version_id is not null
      or new.started_at is not null or new.lease_expires_at is not null or new.completed_at is not null
      or new.claim_token is not null or new.attempt_count <> 0 or new.max_attempts <> 3
      or new.next_attempt_at is not null or new.failure_category is not null
      or new.completed_operation_count <> 0 or new.failed_operation_count <> 0
      or new.result_sha256 is not null or new.result_object_size is not null
      or new.object_created_by_attempt is not null or new.safe_failure_code is not null then
      raise exception using errcode = '23514', message = 'Fix execution must be inserted as a clean queued job';
    end if;
    select audit.document_version_id, version.document_id, version.version_no,
           document.current_version_no, profile.role
      into audit_source, source_document, source_version_no, document_current, requester_role
    from public.audit_jobs audit
    join public.document_versions version on version.id = audit.document_version_id
    join public.documents document on document.id = version.document_id
    join public.user_profiles profile on profile.id = new.requested_by_user_id
    where audit.id = new.audit_job_id and audit.status = 'Completed'
    for update of document;
    if audit_source is null or audit_source <> new.source_document_version_id
      or requester_role <> 'PPKIAdmin' or source_version_no <> document_current then
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
      or old.max_attempts is distinct from new.max_attempts
      or old.created_at is distinct from new.created_at then
      raise exception using errcode = '55000', message = 'Fix execution request and approved plan are immutable';
    end if;
    if old.state in ('Completed','Failed','NoChange') then
      raise exception using errcode = '55000', message = 'Terminal fix execution is immutable';
    end if;
    if old.attempt_count > new.attempt_count or new.attempt_count > old.attempt_count + 1 then
      raise exception using errcode = '23514', message = 'Fix execution attempt is invalid';
    end if;
    if old.state = 'Queued' and new.state = 'Processing' then
      if new.attempt_count <> old.attempt_count + 1 or new.claim_token is null
        or new.claim_token is not distinct from old.claim_token then
        raise exception using errcode = '23514', message = 'Fix execution claim is invalid';
      end if;
    elsif old.state = 'Processing' and new.state = 'Processing' then
      if old.lease_expires_at < statement_timestamp() then
        if new.attempt_count <> old.attempt_count + 1 or new.claim_token is null
          or new.claim_token is not distinct from old.claim_token then
          raise exception using errcode = '23514', message = 'Fix execution reclaim is invalid';
        end if;
      elsif new.claim_token is distinct from old.claim_token or new.attempt_count <> old.attempt_count
        or old.started_at is distinct from new.started_at
        or old.result_document_version_id is distinct from new.result_document_version_id
        or old.completed_operation_count is distinct from new.completed_operation_count
        or old.failed_operation_count is distinct from new.failed_operation_count
        or old.result_sha256 is distinct from new.result_sha256
        or old.failure_category is distinct from new.failure_category
        or old.safe_failure_code is distinct from new.safe_failure_code
        or old.completed_at is distinct from new.completed_at then
        raise exception using errcode = '55000', message = 'Only the active claim lease may be renewed';
      end if;
    elsif old.state = 'Processing' and new.state = 'Queued' then
      if new.claim_token is not null or new.lease_expires_at is not null
        or new.failure_category <> 'TransientInfrastructure' or new.safe_failure_code is null
        or new.next_attempt_at is null or new.attempt_count >= new.max_attempts then
        raise exception using errcode = '23514', message = 'Fix execution retry is invalid';
      end if;
    elsif not (old.state = 'Processing' and new.state in ('Completed','Failed','NoChange')) then
      raise exception using errcode = '23514', message = 'Invalid fix execution state transition';
    end if;
  end if;

  if new.state = 'Queued' and (new.lease_expires_at is not null or new.completed_at is not null or new.claim_token is not null) then
    raise exception using errcode = '23514', message = 'Queued fix execution fields are invalid';
  end if;
  if new.state = 'Processing' and (new.started_at is null or new.lease_expires_at is null
    or new.completed_at is not null or new.next_attempt_at is not null or new.claim_token is null
    or new.failure_category is not null or new.safe_failure_code is not null
    or new.result_document_version_id is not null or new.result_sha256 is not null
    or new.result_object_size is not null or new.object_created_by_attempt is not null
    or new.completed_operation_count <> 0 or new.failed_operation_count <> 0) then
    raise exception using errcode = '23514', message = 'Processing fix execution fields are invalid';
  end if;
  if new.state in ('Completed','Failed','NoChange') and (new.started_at is null or new.completed_at is null
    or new.lease_expires_at is not null or new.claim_token is not null or new.next_attempt_at is not null) then
    raise exception using errcode = '23514', message = 'Terminal fix execution fields are invalid';
  end if;
  if new.completed_at is not null and new.completed_at < new.started_at then
    raise exception using errcode = '23514', message = 'Fix execution timestamps are out of order';
  end if;

  if new.state = 'Completed' then
    if new.result_document_version_id is null or new.result_sha256 is null or new.result_object_size is null
      or new.completed_operation_count <> new.planned_operation_count or new.failed_operation_count <> 0
      or new.failure_category is not null or new.safe_failure_code is not null then
      raise exception using errcode = '23514', message = 'Completed fix execution result is invalid';
    end if;
    select document_id into source_document from public.document_versions where id = new.source_document_version_id;
    select document_id, parent_version_id, created_by_user_id, sha256, size_bytes
      into result_document, result_parent, result_creator, result_hash, result_size
    from public.document_versions where id = new.result_document_version_id;
    if result_document is null or result_document <> source_document or result_parent <> new.source_document_version_id
      or result_creator <> new.requested_by_user_id or result_hash <> new.result_sha256
      or result_size <> new.result_object_size then
      raise exception using errcode = '23514', message = 'Fix execution result lineage is invalid';
    end if;
  elsif new.state = 'NoChange' then
    if new.result_document_version_id is not null or new.result_sha256 is not null or new.result_object_size is not null
      or new.object_created_by_attempt is not null or new.completed_operation_count <> new.planned_operation_count
      or new.failed_operation_count <> 0 or new.failure_category is not null or new.safe_failure_code is not null then
      raise exception using errcode = '23514', message = 'No-change fix execution result is invalid';
    end if;
  elsif new.state = 'Failed' then
    if new.result_document_version_id is not null or new.result_sha256 is not null or new.result_object_size is not null
      or new.object_created_by_attempt is not null or new.safe_failure_code is null or new.failure_category is null
      or new.failed_operation_count = 0 or new.completed_operation_count <> 0 then
      raise exception using errcode = '23514', message = 'Failed fix execution result is invalid';
    end if;
  end if;
  return new;
end;
$$;

create trigger trg_fix_execution_jobs_enforce_insert before insert on public.fix_execution_jobs
  for each row execute function private.enforce_fix_execution_job();
create trigger trg_fix_execution_jobs_enforce_update before update on public.fix_execution_jobs
  for each row execute function private.enforce_fix_execution_job();
create trigger trg_fix_execution_jobs_reject_delete before delete on public.fix_execution_jobs
  for each row execute function private.enforce_fix_execution_job();

comment on column public.fix_execution_jobs.claim_token is
  'Opaque per-attempt fencing identity. Only the active exact token may renew or finalize.';
comment on column public.fix_execution_jobs.next_attempt_at is
  'Deterministic fix-retry/1.0 fixed-backoff eligibility timestamp; no semantic input changes.';

commit;
