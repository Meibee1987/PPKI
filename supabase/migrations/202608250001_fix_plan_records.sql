-- S7-T01: persisted fix-plan aggregate and per-finding membership.
create table public.fix_plans (
  id uuid primary key default gen_random_uuid(),
  source_audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  source_document_version_id uuid not null references public.document_versions(id) on delete restrict,
  owner_user_id uuid not null references auth.users(id) on delete restrict,
  approver_user_id uuid null references auth.users(id) on delete restrict,
  state text not null default 'Draft',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  approved_at timestamptz null,
  applying_at timestamptz null,
  completed_at timestamptz null,
  failed_at timestamptz null,
  constraint ck_fix_plans_state check (state in ('Draft','Approved','Applying','Completed','Failed')),
  constraint ck_fix_plans_lifecycle_metadata check (
    (state = 'Draft' and approver_user_id is null and approved_at is null
      and applying_at is null and completed_at is null and failed_at is null)
    or (state = 'Approved' and approver_user_id is not null and approved_at is not null
      and applying_at is null and completed_at is null and failed_at is null)
    or (state = 'Applying' and approver_user_id is not null and approved_at is not null
      and applying_at is not null and completed_at is null and failed_at is null)
    or (state = 'Completed' and approver_user_id is not null and approved_at is not null
      and applying_at is not null and completed_at is not null and failed_at is null)
    or (state = 'Failed' and approver_user_id is not null and approved_at is not null
      and applying_at is not null and completed_at is null and failed_at is not null)
  ),
  constraint ck_fix_plans_timestamp_order check (
    updated_at >= created_at
    and (approved_at is null or approved_at >= created_at)
    and (applying_at is null or applying_at >= approved_at)
    and (completed_at is null or completed_at >= applying_at)
    and (failed_at is null or failed_at >= applying_at)
  )
);

create index ix_fix_plans_source_audit on public.fix_plans(source_audit_job_id);
create index ix_fix_plans_source_version on public.fix_plans(source_document_version_id);
create index ix_fix_plans_owner_state_created on public.fix_plans(owner_user_id, state, created_at);

create table public.fix_plan_items (
  id uuid primary key default gen_random_uuid(),
  fix_plan_id uuid not null references public.fix_plans(id) on delete cascade,
  finding_id uuid not null references public.audit_findings(id) on delete restrict,
  created_at timestamptz not null default now(),
  constraint uq_fix_plan_items_plan_finding unique (fix_plan_id, finding_id)
);

create index ix_fix_plan_items_finding on public.fix_plan_items(finding_id);

create or replace function private.enforce_fix_plan_record()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  audit_version_id uuid;
begin
  if tg_op = 'DELETE' then
    if old.state <> 'Draft' then
      raise exception 'Approved or historical fix plans cannot be deleted.' using errcode = '55000';
    end if;
    return old;
  end if;

  select audit.document_version_id into audit_version_id
  from public.audit_jobs audit
  where audit.id = new.source_audit_job_id;
  if audit_version_id is null or audit_version_id <> new.source_document_version_id then
    raise exception 'Fix plan source audit/version lineage is invalid.' using errcode = '23514';
  end if;

  if tg_op = 'INSERT' then
    if new.state <> 'Draft' then
      raise exception 'A new fix plan must be a draft.' using errcode = '55000';
    end if;
    return new;
  end if;

  if old.id is distinct from new.id
    or old.source_audit_job_id is distinct from new.source_audit_job_id
    or old.source_document_version_id is distinct from new.source_document_version_id
    or old.owner_user_id is distinct from new.owner_user_id
    or old.created_at is distinct from new.created_at then
    raise exception 'Fix plan source identity is immutable.' using errcode = '55000';
  end if;

  if old.state = new.state then
    if old.state <> 'Draft'
      or old.approver_user_id is distinct from new.approver_user_id
      or old.approved_at is distinct from new.approved_at
      or old.applying_at is distinct from new.applying_at
      or old.completed_at is distinct from new.completed_at
      or old.failed_at is distinct from new.failed_at then
      raise exception 'Approved or historical fix plans cannot be edited.' using errcode = '55000';
    end if;
    return new;
  end if;

  if not (
    (old.state = 'Draft' and new.state = 'Approved')
    or (old.state = 'Approved' and new.state = 'Applying')
    or (old.state = 'Applying' and new.state in ('Completed','Failed'))
  ) then
    raise exception 'Invalid fix plan lifecycle transition.' using errcode = '55000';
  end if;

  if new.updated_at <= old.updated_at then
    raise exception 'Fix plan transition must advance updated_at.' using errcode = '23514';
  end if;
  if old.state <> 'Draft' and (
    old.approver_user_id is distinct from new.approver_user_id
    or old.approved_at is distinct from new.approved_at) then
    raise exception 'Fix plan approval metadata is immutable.' using errcode = '55000';
  end if;
  return new;
end;
$$;

create or replace function private.enforce_fix_plan_item()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  plan_state text;
  plan_audit_id uuid;
  plan_version_id uuid;
  finding_audit_id uuid;
  finding_version_id uuid;
begin
  if tg_op = 'UPDATE' then
    raise exception 'Fix plan item identity is immutable.' using errcode = '55000';
  end if;

  select plan.state, plan.source_audit_job_id, plan.source_document_version_id
    into plan_state, plan_audit_id, plan_version_id
  from public.fix_plans plan
  where plan.id = case when tg_op = 'DELETE' then old.fix_plan_id else new.fix_plan_id end
  for update;
  if plan_state is null then
    -- A parent-trigger-approved draft delete removes the parent before its
    -- cascading child triggers run. Non-draft parent deletion is rejected by
    -- enforce_fix_plan_record before reaching this path.
    if tg_op = 'DELETE' then return old; end if;
    raise exception 'Fix plan is invalid.' using errcode = '23503';
  end if;
  if plan_state <> 'Draft' then
    raise exception 'Approved or executing fix plan items are immutable.' using errcode = '55000';
  end if;
  if tg_op = 'DELETE' then return old; end if;

  select finding.audit_job_id, audit.document_version_id
    into finding_audit_id, finding_version_id
  from public.audit_findings finding
  join public.audit_jobs audit on audit.id = finding.audit_job_id
  where finding.id = new.finding_id;
  if finding_audit_id is null or finding_audit_id <> plan_audit_id
    or finding_version_id <> plan_version_id then
    raise exception 'Fix plan item lineage is invalid.' using errcode = '23514';
  end if;
  return new;
end;
$$;

revoke all on function private.enforce_fix_plan_record() from public, anon, authenticated, service_role;
revoke all on function private.enforce_fix_plan_item() from public, anon, authenticated, service_role;

create trigger trg_fix_plans_enforce_insert before insert on public.fix_plans
for each row execute function private.enforce_fix_plan_record();
create trigger trg_fix_plans_enforce_update before update on public.fix_plans
for each row execute function private.enforce_fix_plan_record();
create trigger trg_fix_plans_enforce_delete before delete on public.fix_plans
for each row execute function private.enforce_fix_plan_record();
create trigger trg_fix_plan_items_enforce_insert before insert on public.fix_plan_items
for each row execute function private.enforce_fix_plan_item();
create trigger trg_fix_plan_items_enforce_update before update on public.fix_plan_items
for each row execute function private.enforce_fix_plan_item();
create trigger trg_fix_plan_items_enforce_delete before delete on public.fix_plan_items
for each row execute function private.enforce_fix_plan_item();

alter table public.fix_plans enable row level security;
alter table public.fix_plan_items enable row level security;
revoke all on table public.fix_plans from anon, authenticated, service_role;
revoke all on table public.fix_plan_items from anon, authenticated, service_role;
grant select on table public.fix_plans to authenticated;
grant select on table public.fix_plan_items to authenticated;
grant select, insert, update, delete on table public.fix_plans to service_role;
grant select, insert, update, delete on table public.fix_plan_items to service_role;

create policy fix_plans_select_owned on public.fix_plans
for select to authenticated using (owner_user_id = (select auth.uid()));

create policy fix_plan_items_select_owned on public.fix_plan_items
for select to authenticated using (
  exists(select 1 from public.fix_plans plan
    where plan.id = fix_plan_id and plan.owner_user_id = (select auth.uid()))
);

comment on table public.fix_plans is
  'Persisted owner-selected fix plan lifecycle bound to one immutable audit and source document version.';
comment on table public.fix_plan_items is
  'Stable per-finding membership for a persisted fix plan; finding evidence remains authoritative in audit_findings.';
