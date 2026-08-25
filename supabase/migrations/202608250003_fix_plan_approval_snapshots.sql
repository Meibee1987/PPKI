-- S7-T06: explicit approval with one immutable, execution-authoritative snapshot per fix plan.
create table public.fix_plan_approval_snapshots (
  id uuid primary key default gen_random_uuid(),
  fix_plan_id uuid not null references public.fix_plans(id) on delete restrict,
  schema_version text not null,
  plan_hash text not null,
  approval_request_hash text not null,
  source_version_sha256 text not null,
  snapshot jsonb not null,
  approved_by_user_id uuid not null references auth.users(id) on delete restrict,
  approved_at timestamptz not null,
  created_at timestamptz not null default now(),
  constraint uq_fix_plan_approval_snapshots_plan unique (fix_plan_id),
  constraint ck_fix_plan_approval_schema_version check (char_length(schema_version) between 1 and 64),
  constraint ck_fix_plan_approval_plan_hash check (plan_hash ~ '^[0-9a-f]{64}$'),
  constraint ck_fix_plan_approval_request_hash check (approval_request_hash ~ '^[0-9a-f]{64}$'),
  constraint ck_fix_plan_approval_source_sha check (source_version_sha256 ~ '^[0-9a-f]{64}$'),
  constraint ck_fix_plan_approval_snapshot_object check (jsonb_typeof(snapshot) = 'object'),
  constraint ck_fix_plan_approval_timestamp check (created_at = approved_at)
);

create or replace function private.enforce_fix_plan_approval_snapshot()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  plan_owner uuid;
  plan_state text;
  plan_source_sha text;
  plan_approver uuid;
  plan_approved_at timestamptz;
begin
  if tg_op <> 'INSERT' then
    raise exception 'Approved fix plan snapshots are append-only.' using errcode = '55000';
  end if;
  select plan.owner_user_id, plan.state, version.sha256, plan.approver_user_id, plan.approved_at
    into plan_owner, plan_state, plan_source_sha, plan_approver, plan_approved_at
  from public.fix_plans plan
  join public.document_versions version on version.id = plan.source_document_version_id
  where plan.id = new.fix_plan_id
  for update of plan;
  if plan_owner is null or plan_state not in ('Draft', 'Approved')
    or plan_owner <> new.approved_by_user_id
    or plan_source_sha <> new.source_version_sha256
    or (plan_state = 'Approved' and (plan_approver <> new.approved_by_user_id
      or plan_approved_at <> new.approved_at)) then
    raise exception 'Fix plan approval snapshot lineage is invalid.' using errcode = '23514';
  end if;
  return new;
end;
$$;

revoke all on function private.enforce_fix_plan_approval_snapshot()
  from public, anon, authenticated, service_role;
create trigger trg_fix_plan_approval_snapshots_enforce_insert
before insert on public.fix_plan_approval_snapshots
for each row execute function private.enforce_fix_plan_approval_snapshot();
create trigger trg_fix_plan_approval_snapshots_enforce_update
before update on public.fix_plan_approval_snapshots
for each row execute function private.enforce_fix_plan_approval_snapshot();
create trigger trg_fix_plan_approval_snapshots_enforce_delete
before delete on public.fix_plan_approval_snapshots
for each row execute function private.enforce_fix_plan_approval_snapshot();

alter table public.fix_plan_approval_snapshots enable row level security;
revoke all on table public.fix_plan_approval_snapshots from anon, authenticated, service_role;
grant select on table public.fix_plan_approval_snapshots to authenticated;
grant select, insert on table public.fix_plan_approval_snapshots to service_role;
create policy fix_plan_approval_snapshots_select_owned
on public.fix_plan_approval_snapshots for select to authenticated using (
  exists(select 1 from public.fix_plans plan
    where plan.id = fix_plan_id and plan.owner_user_id = (select auth.uid()))
);

comment on table public.fix_plan_approval_snapshots is
  'One immutable, schema-versioned approval snapshot that freezes executable fix-plan intent.';
