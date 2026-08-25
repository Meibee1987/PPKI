-- S7-T03: retry-safe draft plan creation without changing S7-T01 membership semantics.
alter table public.fix_plans
  add column idempotency_key uuid,
  add column request_hash text;

update public.fix_plans
set idempotency_key = gen_random_uuid(),
    request_hash = repeat('0', 64)
where idempotency_key is null or request_hash is null;

alter table public.fix_plans
  alter column idempotency_key set not null,
  alter column request_hash set not null,
  add constraint ck_fix_plans_request_hash
    check (request_hash ~ '^[0-9a-f]{64}$'),
  add constraint uq_fix_plans_owner_idempotency
    unique (owner_user_id, idempotency_key);

create or replace function private.enforce_fix_plan_idempotency()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if old.idempotency_key is distinct from new.idempotency_key
    or old.request_hash is distinct from new.request_hash then
    raise exception 'Fix plan idempotency identity is immutable.' using errcode = '55000';
  end if;
  return new;
end;
$$;

revoke all on function private.enforce_fix_plan_idempotency()
  from public, anon, authenticated, service_role;

create trigger trg_fix_plans_enforce_idempotency
before update on public.fix_plans
for each row execute function private.enforce_fix_plan_idempotency();
