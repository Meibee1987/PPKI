-- S4-T01: canonical re-audit lineage and exact historical snapshot reuse.
-- Additive only: ordinary and legacy audits keep nullable lineage columns.

begin;

alter table public.audit_jobs
  add column source_audit_job_id uuid,
  add column source_fix_execution_id uuid;

alter table public.audit_jobs
  add constraint fk_audit_jobs_source_audit
    foreign key (source_audit_job_id) references public.audit_jobs(id) on delete restrict,
  add constraint fk_audit_jobs_source_fix_execution
    foreign key (source_fix_execution_id) references public.fix_execution_jobs(id) on delete restrict,
  add constraint ck_audit_jobs_reaudit_lineage_pair
    check ((source_audit_job_id is null) = (source_fix_execution_id is null)),
  add constraint ck_audit_jobs_reaudit_not_self
    check (source_audit_job_id is null or source_audit_job_id <> id),
  add constraint uq_audit_jobs_source_fix_execution unique (source_fix_execution_id);

create index ix_audit_jobs_source_audit
  on public.audit_jobs(source_audit_job_id)
  where source_audit_job_id is not null;

create or replace function private.enforce_reaudit_lineage()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  source_status text;
  source_version uuid;
  source_document uuid;
  source_profile uuid;
  source_kind text;
  source_hash text;
  source_count integer;
  execution_state text;
  execution_audit uuid;
  execution_source_version uuid;
  execution_result_version uuid;
  execution_requester uuid;
  result_document uuid;
begin
  if tg_op = 'UPDATE' and (
    old.source_audit_job_id is distinct from new.source_audit_job_id
    or old.source_fix_execution_id is distinct from new.source_fix_execution_id) then
    raise exception using errcode = '55000', message = 'Re-audit lineage is immutable';
  end if;

  if new.source_audit_job_id is null and new.source_fix_execution_id is null then
    return new;
  end if;
  if new.source_audit_job_id is null or new.source_fix_execution_id is null
    or new.source_audit_job_id = new.id then
    raise exception using errcode = '23514', message = 'Re-audit lineage identity is invalid';
  end if;

  if tg_op = 'UPDATE' and (
    old.document_version_id is distinct from new.document_version_id
    or old.profile_version_id is distinct from new.profile_version_id
    or old.document_kind_snapshot is distinct from new.document_kind_snapshot
    or old.requested_by_user_id is distinct from new.requested_by_user_id
    or old.resolved_rule_set_hash is distinct from new.resolved_rule_set_hash
    or old.applicable_rule_count is distinct from new.applicable_rule_count
    or old.created_at is distinct from new.created_at) then
    raise exception using errcode = '55000', message = 'Re-audit historical context is immutable';
  end if;

  select audit.status, audit.document_version_id, version.document_id,
         audit.profile_version_id, audit.document_kind_snapshot,
         audit.resolved_rule_set_hash, audit.applicable_rule_count
    into source_status, source_version, source_document, source_profile,
         source_kind, source_hash, source_count
  from public.audit_jobs as audit
  join public.document_versions as version on version.id = audit.document_version_id
  where audit.id = new.source_audit_job_id;

  select execution.state, execution.audit_job_id,
         execution.source_document_version_id, execution.result_document_version_id,
         execution.requested_by_user_id, result.document_id
    into execution_state, execution_audit, execution_source_version,
         execution_result_version, execution_requester, result_document
  from public.fix_execution_jobs as execution
  left join public.document_versions as result on result.id = execution.result_document_version_id
  where execution.id = new.source_fix_execution_id;

  if source_status is distinct from 'Completed'
    or execution_state is distinct from 'Completed'
    or execution_audit is distinct from new.source_audit_job_id
    or execution_source_version is distinct from source_version
    or execution_result_version is null
    or execution_result_version is distinct from new.document_version_id
    or execution_requester is distinct from new.requested_by_user_id
    or result_document is distinct from source_document
    or new.profile_version_id is distinct from source_profile
    or new.document_kind_snapshot is distinct from source_kind
    or new.resolved_rule_set_hash is distinct from source_hash
    or new.applicable_rule_count is distinct from source_count
    or source_hash is null or source_count <= 0 then
    raise exception using errcode = '23514', message = 'Re-audit historical source chain is invalid';
  end if;

  if tg_op = 'INSERT' and (
    new.status <> 'Queued'
    or new.total_rules <> 0 or new.error_count <> 0
    or new.warning_count <> 0 or new.info_count <> 0
    or new.score is not null or new.started_at is not null
    or new.completed_at is not null or new.error_message is not null) then
    raise exception using errcode = '23514', message = 'Re-audit must start as a clean queued job';
  end if;

  return new;
end;
$$;

create trigger trg_audit_jobs_reaudit_lineage_insert
  before insert on public.audit_jobs
  for each row execute function private.enforce_reaudit_lineage();

create trigger trg_audit_jobs_reaudit_lineage_update
  before update on public.audit_jobs
  for each row execute function private.enforce_reaudit_lineage();

create or replace function private.enforce_reaudit_snapshot_clone()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  source_snapshot_count integer;
  target_snapshot_count integer;
  target_finding_count integer;
begin
  if new.source_fix_execution_id is null then
    return null;
  end if;

  select count(*) into source_snapshot_count
  from public.audit_rule_snapshots as snapshot
  where snapshot.audit_job_id = new.source_audit_job_id;

  select count(*) into target_snapshot_count
  from public.audit_rule_snapshots as snapshot
  where snapshot.audit_job_id = new.id;

  if source_snapshot_count <= 0
    or source_snapshot_count <> new.applicable_rule_count
    or target_snapshot_count <> source_snapshot_count then
    raise exception using errcode = '23514', message = 'Re-audit snapshot clone is incomplete';
  end if;

  if exists (
    select 1 from (
      (select rule_id, rule_code, domain, subdomain, applies_to, element,
              requirement_json, validation_key, validation_json, severity,
              fix_mode, source_reference_json, layer, precedence, ordinal,
              snapshot_schema_version
       from public.audit_rule_snapshots where audit_job_id = new.source_audit_job_id
       except
       select rule_id, rule_code, domain, subdomain, applies_to, element,
              requirement_json, validation_key, validation_json, severity,
              fix_mode, source_reference_json, layer, precedence, ordinal,
              snapshot_schema_version
       from public.audit_rule_snapshots where audit_job_id = new.id)
      union all
      (select rule_id, rule_code, domain, subdomain, applies_to, element,
              requirement_json, validation_key, validation_json, severity,
              fix_mode, source_reference_json, layer, precedence, ordinal,
              snapshot_schema_version
       from public.audit_rule_snapshots where audit_job_id = new.id
       except
       select rule_id, rule_code, domain, subdomain, applies_to, element,
              requirement_json, validation_key, validation_json, severity,
              fix_mode, source_reference_json, layer, precedence, ordinal,
              snapshot_schema_version
       from public.audit_rule_snapshots where audit_job_id = new.source_audit_job_id)
    ) as mismatch
  ) then
    raise exception using errcode = '23514', message = 'Re-audit snapshot clone differs from source';
  end if;

  if new.status = 'Queued' then
    select count(*) into target_finding_count
    from public.audit_findings as finding
    where finding.audit_job_id = new.id;
    if target_finding_count <> 0 then
      raise exception using errcode = '23514', message = 'Re-audit cannot copy source findings';
    end if;
  end if;

  return null;
end;
$$;

create constraint trigger trg_audit_jobs_reaudit_snapshot_clone
  after insert or update on public.audit_jobs
  deferrable initially deferred
  for each row
  execute function private.enforce_reaudit_snapshot_clone();

comment on column public.audit_jobs.source_audit_job_id is
  'Immutable source audit whose exact historical evaluation context is cloned for a re-audit.';
comment on column public.audit_jobs.source_fix_execution_id is
  'Immutable unique completed fix execution that canonically identifies one re-audit.';

commit;
