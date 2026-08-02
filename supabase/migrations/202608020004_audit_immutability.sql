-- S1-T04: immutable document versions and reproducible audit snapshots.
-- migration-hygiene: allow-destructive replace-history-cascade-fks-with-restrict

begin;

-- A historical non-queued audit cannot be given a trustworthy resolved-rule
-- snapshot after the fact. Stop safely instead of inventing a backfill.
do $$
begin
  if exists (select 1 from public.audit_jobs where status <> 'Queued') then
    raise exception 'S1-T04 precondition failed: existing non-queued audits require offline remediation';
  end if;
end;
$$;

alter table public.audit_jobs
  add column if not exists applicable_rule_count integer not null default 0;

alter table public.document_versions
  drop constraint if exists document_versions_document_id_fkey;
alter table public.document_versions
  add constraint document_versions_document_id_fkey
  foreign key (document_id) references public.documents(id) on delete restrict;

alter table public.audit_findings
  drop constraint if exists audit_findings_audit_job_id_fkey;
alter table public.audit_findings
  add constraint audit_findings_audit_job_id_fkey
  foreign key (audit_job_id) references public.audit_jobs(id) on delete restrict;

create table public.audit_rule_snapshots (
  id uuid primary key default gen_random_uuid(),
  audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  rule_id uuid not null references public.rules(id) on delete restrict,
  rule_code text not null,
  domain text not null,
  subdomain text,
  applies_to text not null,
  element text not null,
  requirement_json jsonb not null,
  validation_key text not null,
  validation_json jsonb not null,
  severity text not null,
  fix_mode text not null,
  source_reference_json jsonb not null,
  layer text not null,
  precedence integer not null,
  ordinal integer not null,
  snapshot_schema_version integer not null,
  created_at timestamptz not null default now(),
  constraint uq_audit_rule_snapshots_job_rule_code unique (audit_job_id, rule_code),
  constraint uq_audit_rule_snapshots_job_ordinal unique (audit_job_id, ordinal),
  constraint ck_audit_rule_snapshots_rule_code check (btrim(rule_code) <> ''),
  constraint ck_audit_rule_snapshots_validation_key check (btrim(validation_key) <> ''),
  constraint ck_audit_rule_snapshots_layer check (btrim(layer) <> ''),
  constraint ck_audit_rule_snapshots_severity check (severity in ('Error', 'Warning', 'Info')),
  constraint ck_audit_rule_snapshots_fix_mode check (fix_mode in ('Auto', 'Confirm', 'Manual', 'Report')),
  constraint ck_audit_rule_snapshots_order check (ordinal > 0 and precedence >= 0),
  constraint ck_audit_rule_snapshots_schema_version check (snapshot_schema_version > 0),
  constraint ck_audit_rule_snapshots_json_shapes check (
    jsonb_typeof(requirement_json) = 'object'
    and jsonb_typeof(validation_json) = 'object'
    and jsonb_typeof(source_reference_json) = 'object'
  )
);

create index ix_audit_rule_snapshots_audit_job
  on public.audit_rule_snapshots(audit_job_id);

alter table public.audit_jobs
  add constraint ck_audit_jobs_applicable_rule_count
  check (applicable_rule_count >= 0) not valid;

create or replace function public.reject_document_version_mutation()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  relation_owner name;
begin
  select role.rolname into relation_owner
  from pg_catalog.pg_class as relation
  join pg_catalog.pg_roles as role on role.oid = relation.relowner
  where relation.oid = tg_relid;

  if current_user = relation_owner then
    if tg_op = 'DELETE' then return old; end if;
    return new;
  end if;

  raise exception using
    errcode = '55000',
    message = tg_op || ' is not allowed for immutable document version';
end;
$$;

create trigger trg_document_versions_reject_update
  before update on public.document_versions
  for each row execute function public.reject_document_version_mutation();

create trigger trg_document_versions_reject_delete
  before delete on public.document_versions
  for each row execute function public.reject_document_version_mutation();

create or replace function public.enforce_audit_job_state()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  relation_owner name;
  snapshot_count integer;
begin
  select role.rolname into relation_owner
  from pg_catalog.pg_class as relation
  join pg_catalog.pg_roles as role on role.oid = relation.relowner
  where relation.oid = tg_relid;

  if current_user = relation_owner then
    if tg_op = 'DELETE' then return old; end if;
    return new;
  end if;

  if tg_op = 'DELETE' then
    raise exception using errcode = '55000', message = 'DELETE is not allowed for audit job';
  end if;

  if tg_op = 'INSERT' then
    if new.status <> 'Queued' then
      raise exception using errcode = '23514', message = 'Audit job must be inserted as queued';
    end if;
  else
    if old.id is distinct from new.id
      or old.document_version_id is distinct from new.document_version_id
      or old.profile_version_id is distinct from new.profile_version_id
      or old.requested_by_user_id is distinct from new.requested_by_user_id
      or old.created_at is distinct from new.created_at then
      raise exception using errcode = '55000', message = 'Audit job identity is immutable';
    end if;

    if old.status in ('Completed', 'Failed', 'Cancelled') then
      raise exception using errcode = '55000', message = 'Terminal audit job is immutable';
    end if;

    if new.started_at is distinct from old.started_at and old.status = 'Processing' then
      raise exception using errcode = '55000', message = 'Audit job start time is immutable after processing starts';
    end if;

    if new.status is distinct from old.status and not (
      (old.status = 'Queued' and new.status in ('Processing', 'Cancelled'))
      or (old.status = 'Processing' and new.status in ('Completed', 'Failed', 'Cancelled'))
    ) then
      raise exception using errcode = '23514', message = 'Invalid audit job state transition';
    end if;

    if new.status = old.status and old.status = 'Queued' and new is distinct from old then
      raise exception using errcode = '55000', message = 'Queued audit job is immutable until claimed';
    end if;

    if old.resolved_rule_set_hash is not null and (
      new.resolved_rule_set_hash is distinct from old.resolved_rule_set_hash
      or new.applicable_rule_count is distinct from old.applicable_rule_count
    ) then
      raise exception using errcode = '55000', message = 'Resolved rule set identity is immutable';
    end if;
  end if;

  if new.status = 'Queued' and (
    new.started_at is not null
    or new.completed_at is not null
    or new.resolved_rule_set_hash is not null
    or new.applicable_rule_count <> 0
  ) then
    raise exception using errcode = '23514', message = 'Queued audit job fields are invalid';
  end if;

  if new.status = 'Processing' and (new.started_at is null or new.completed_at is not null) then
    raise exception using errcode = '23514', message = 'Processing audit job timestamps are invalid';
  end if;

  if new.status in ('Completed', 'Failed') and (new.started_at is null or new.completed_at is null) then
    raise exception using errcode = '23514', message = 'Terminal audit job timestamps are invalid';
  end if;

  if new.status = 'Cancelled' and new.completed_at is null then
    raise exception using errcode = '23514', message = 'Cancelled audit job completion time is required';
  end if;

  if new.completed_at is not null and new.started_at is not null and new.completed_at < new.started_at then
    raise exception using errcode = '23514', message = 'Audit job timestamps are out of order';
  end if;

  if new.resolved_rule_set_hash is null and new.applicable_rule_count <> 0 then
    raise exception using errcode = '23514', message = 'Audit rule snapshot count requires a hash';
  end if;

  if new.resolved_rule_set_hash is not null then
    if new.resolved_rule_set_hash !~ '^[0-9a-f]{64}$' then
      raise exception using errcode = '23514', message = 'Audit rule snapshot hash is invalid';
    end if;

    select count(*) into snapshot_count
    from public.audit_rule_snapshots as snapshot
    where snapshot.audit_job_id = new.id;

    if snapshot_count <> new.applicable_rule_count then
      raise exception using errcode = '23514', message = 'Audit rule snapshot count is invalid';
    end if;
  end if;

  if new.status = 'Completed' and new.resolved_rule_set_hash is null then
    raise exception using errcode = '23514', message = 'Completed audit job requires a rule snapshot hash';
  end if;

  if new.error_message is not null and (
    char_length(new.error_message) > 256
    or position(chr(10) in new.error_message) > 0
    or position(chr(13) in new.error_message) > 0
  ) then
    raise exception using errcode = '23514', message = 'Audit job error message is invalid';
  end if;

  return new;
end;
$$;

create trigger trg_audit_jobs_enforce_state_insert
  before insert on public.audit_jobs
  for each row execute function public.enforce_audit_job_state();

create trigger trg_audit_jobs_enforce_state_update
  before update on public.audit_jobs
  for each row execute function public.enforce_audit_job_state();

create trigger trg_audit_jobs_reject_delete
  before delete on public.audit_jobs
  for each row execute function public.enforce_audit_job_state();

create or replace function public.enforce_audit_rule_snapshot_immutability()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  relation_owner name;
  parent_status text;
begin
  select role.rolname into relation_owner
  from pg_catalog.pg_class as relation
  join pg_catalog.pg_roles as role on role.oid = relation.relowner
  where relation.oid = tg_relid;

  if current_user = relation_owner then
    if tg_op = 'DELETE' then return old; end if;
    return new;
  end if;

  if tg_op in ('UPDATE', 'DELETE') then
    raise exception using
      errcode = '55000',
      message = tg_op || ' is not allowed for immutable audit rule snapshot';
  end if;

  select audit.status into parent_status
  from public.audit_jobs as audit
  where audit.id = new.audit_job_id;

  if parent_status is distinct from 'Processing' then
    raise exception using errcode = '23514', message = 'Audit rule snapshot requires a processing audit job';
  end if;

  return new;
end;
$$;

create trigger trg_audit_rule_snapshots_enforce_insert
  before insert on public.audit_rule_snapshots
  for each row execute function public.enforce_audit_rule_snapshot_immutability();

create trigger trg_audit_rule_snapshots_reject_update
  before update on public.audit_rule_snapshots
  for each row execute function public.enforce_audit_rule_snapshot_immutability();

create trigger trg_audit_rule_snapshots_reject_delete
  before delete on public.audit_rule_snapshots
  for each row execute function public.enforce_audit_rule_snapshot_immutability();

create or replace function public.enforce_audit_finding_lifecycle()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  relation_owner name;
  parent_status text;
begin
  select role.rolname into relation_owner
  from pg_catalog.pg_class as relation
  join pg_catalog.pg_roles as role on role.oid = relation.relowner
  where relation.oid = tg_relid;

  if current_user = relation_owner then
    if tg_op = 'DELETE' then return old; end if;
    return new;
  end if;

  select audit.status into parent_status
  from public.audit_jobs as audit
  where audit.id = case when tg_op = 'INSERT' then new.audit_job_id else old.audit_job_id end;

  if tg_op = 'INSERT' and parent_status is distinct from 'Processing' then
    raise exception using errcode = '23514', message = 'Audit finding requires a processing audit job';
  end if;

  if tg_op in ('UPDATE', 'DELETE') and parent_status in ('Completed', 'Failed', 'Cancelled') then
    raise exception using
      errcode = '55000',
      message = tg_op || ' is not allowed for terminal audit finding';
  end if;

  if tg_op = 'DELETE' then return old; end if;
  return new;
end;
$$;

create trigger trg_audit_findings_enforce_insert
  before insert on public.audit_findings
  for each row execute function public.enforce_audit_finding_lifecycle();

create trigger trg_audit_findings_enforce_update
  before update on public.audit_findings
  for each row execute function public.enforce_audit_finding_lifecycle();

create trigger trg_audit_findings_enforce_delete
  before delete on public.audit_findings
  for each row execute function public.enforce_audit_finding_lifecycle();

alter table public.audit_rule_snapshots enable row level security;
revoke all on table public.audit_rule_snapshots from anon, authenticated;
grant select on table public.audit_rule_snapshots to authenticated;
revoke update, delete on table public.audit_rule_snapshots from service_role;
grant select, insert on table public.audit_rule_snapshots to service_role;

create policy audit_rule_snapshots_select_owned_document
  on public.audit_rule_snapshots
  for select to authenticated
  using (
    (select auth.uid()) is not null
    and exists (
      select 1
      from public.audit_jobs as audit
      join public.document_versions as version on version.id = audit.document_version_id
      join public.documents as document on document.id = version.document_id
      where audit.id = audit_rule_snapshots.audit_job_id
        and document.owner_user_id = (select auth.uid())
    )
  );

commit;
