-- S8-T08: append-only, privacy-bounded outcomes for exact approved fix-plan items.
begin;

alter table public.fix_execution_jobs
  add column fix_plan_id uuid null references public.fix_plans(id) on delete restrict;

create unique index uq_fix_execution_jobs_fix_plan
  on public.fix_execution_jobs(fix_plan_id) where fix_plan_id is not null;
alter table public.fix_execution_jobs
  add constraint uq_fix_execution_job_plan_pair unique (id, fix_plan_id);
alter table public.fix_plan_items
  add constraint uq_fix_plan_item_plan_pair unique (id, fix_plan_id);

create or replace function private.enforce_fix_execution_plan_link()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  plan_audit uuid;
  plan_source uuid;
  plan_owner uuid;
  plan_state text;
  approved_hash text;
begin
  if tg_op = 'UPDATE' and old.fix_plan_id is distinct from new.fix_plan_id then
    raise exception 'Fix execution plan linkage is immutable.' using errcode = '55000';
  end if;
  if new.fix_plan_id is null then return new; end if;
  select plan.source_audit_job_id, plan.source_document_version_id, plan.owner_user_id,
         plan.state, snapshot.plan_hash
    into plan_audit, plan_source, plan_owner, plan_state, approved_hash
  from public.fix_plans plan
  join public.fix_plan_approval_snapshots snapshot on snapshot.fix_plan_id = plan.id
  where plan.id = new.fix_plan_id;
  if plan_audit is null or plan_state not in ('Approved','Applying','Completed','Failed')
    or plan_audit <> new.audit_job_id or plan_source <> new.source_document_version_id
    or plan_owner <> new.requested_by_user_id or approved_hash <> new.plan_hash
    or new.idempotency_key <> new.fix_plan_id then
    raise exception 'Fix execution plan linkage is invalid.' using errcode = '23514';
  end if;
  return new;
end;
$$;

revoke all on function private.enforce_fix_execution_plan_link()
  from public, anon, authenticated, service_role;
create trigger trg_fix_execution_jobs_plan_link
before insert or update on public.fix_execution_jobs
for each row execute function private.enforce_fix_execution_plan_link();

create table public.fix_item_results (
  id uuid primary key,
  fix_execution_job_id uuid not null,
  fix_plan_id uuid not null references public.fix_plans(id) on delete restrict,
  fix_plan_item_id uuid not null,
  source_document_version_id uuid not null references public.document_versions(id) on delete restrict,
  result_document_version_id uuid null references public.document_versions(id) on delete restrict,
  attempt_number integer not null,
  claim_token uuid not null,
  operation_ordinal integer not null,
  outcome text not null,
  validation_key text not null,
  fix_key text not null,
  fixer_version text not null,
  property_identifier text not null,
  structural_anchor jsonb not null,
  before_payload jsonb null,
  after_payload jsonb null,
  safe_failure_code text null,
  created_at timestamptz not null default now(),
  constraint fk_fix_item_results_job_plan foreign key (fix_execution_job_id, fix_plan_id)
    references public.fix_execution_jobs(id, fix_plan_id) on delete restrict,
  constraint fk_fix_item_results_item_plan foreign key (fix_plan_item_id, fix_plan_id)
    references public.fix_plan_items(id, fix_plan_id) on delete restrict,
  constraint uq_fix_item_results_attempt_item
    unique (fix_execution_job_id, attempt_number, fix_plan_item_id),
  constraint ck_fix_item_results_attempt check (attempt_number between 1 and 10 and operation_ordinal > 0),
  constraint ck_fix_item_results_outcome check (outcome in ('Applied','Skipped','Failed')),
  constraint ck_fix_item_results_identity check (
    char_length(validation_key) between 1 and 128 and validation_key ~ '^[a-z0-9][a-z0-9.-]*$'
    and char_length(fix_key) between 1 and 128 and fix_key ~ '^[a-z0-9][a-z0-9.-]*$'
    and char_length(fixer_version) between 1 and 128 and fixer_version ~ '^[a-z0-9][a-z0-9.-]*$'
    and char_length(property_identifier) between 1 and 128 and property_identifier ~ '^[a-z0-9][a-z0-9.-]*$'),
  constraint ck_fix_item_results_anchor check (
    jsonb_typeof(structural_anchor) = 'object'
    and structural_anchor ->> 'schemaVersion' = 'fix-structural-anchor/1.0'
    and structural_anchor ->> 'scope' in ('main-document-section','main-document-paragraph','main-document-run')
    and pg_column_size(structural_anchor) <= 512
    and structural_anchor - array['schemaVersion','scope','bodyElementIndex','sectionIndex','paragraphIndex','runIndex']::text[] = '{}'::jsonb),
  constraint ck_fix_item_results_payloads check (
    (before_payload is null or (jsonb_typeof(before_payload) = 'object'
      and before_payload ->> 'schemaVersion' = 'fix-item-value/1.0'
      and before_payload ->> 'property' = property_identifier
      and before_payload ->> 'valueType' in ('twips','twips-pair','half-points','enum-code',
        'boolean-state','font-family-token','font-family-sha256')
      and jsonb_typeof(before_payload -> 'value') = 'string'
      and char_length(before_payload ->> 'value') between 1 and 128
      and before_payload - array['schemaVersion','property','valueType','value']::text[] = '{}'::jsonb
      and pg_column_size(before_payload) <= 1024))
    and (after_payload is null or (jsonb_typeof(after_payload) = 'object'
      and after_payload ->> 'schemaVersion' = 'fix-item-value/1.0'
      and after_payload ->> 'property' = property_identifier
      and after_payload ->> 'valueType' in ('twips','twips-pair','half-points','enum-code',
        'boolean-state','font-family-token','font-family-sha256')
      and jsonb_typeof(after_payload -> 'value') = 'string'
      and char_length(after_payload ->> 'value') between 1 and 128
      and after_payload - array['schemaVersion','property','valueType','value']::text[] = '{}'::jsonb
      and pg_column_size(after_payload) <= 1024))),
  constraint ck_fix_item_results_failure check (
    safe_failure_code is null or (char_length(safe_failure_code) between 1 and 128
      and safe_failure_code ~ '^[a-z0-9][a-z0-9.-]*$')),
  constraint ck_fix_item_results_semantics check (
    (outcome = 'Applied' and result_document_version_id is not null
      and before_payload is not null and after_payload is not null
      and before_payload <> after_payload and safe_failure_code is null)
    or (outcome = 'Skipped' and before_payload is not null and after_payload = before_payload
      and safe_failure_code is null)
    or (outcome = 'Failed' and result_document_version_id is null
      and after_payload is null and safe_failure_code is not null))
);

create index ix_fix_item_results_plan_item on public.fix_item_results(fix_plan_id, fix_plan_item_id);
create index ix_fix_item_results_result_version on public.fix_item_results(result_document_version_id)
  where result_document_version_id is not null;

create or replace function private.enforce_fix_item_result()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  job_attempt integer;
  job_claim uuid;
  job_state text;
  job_lease timestamptz;
  job_source uuid;
  plan_state text;
  approved_item jsonb;
  persisted_finding uuid;
  expected_anchor jsonb;
begin
  if tg_op <> 'INSERT' then
    raise exception 'Fix item results are append-only.' using errcode = '55000';
  end if;
  select job.attempt_count, job.claim_token, job.state, job.lease_expires_at,
         job.source_document_version_id, plan.state
    into job_attempt, job_claim, job_state, job_lease, job_source, plan_state
  from public.fix_execution_jobs job
  join public.fix_plans plan on plan.id = job.fix_plan_id
  where job.id = new.fix_execution_job_id and job.fix_plan_id = new.fix_plan_id
  for update of job;
  if job_attempt is null or job_state <> 'Processing' or job_claim <> new.claim_token
    or job_attempt <> new.attempt_number or job_lease <= statement_timestamp()
    or job_source <> new.source_document_version_id or plan_state <> 'Applying' then
    raise exception 'Fix item result requires the active fenced attempt.' using errcode = '23514';
  end if;
  select item
    into approved_item
  from public.fix_plan_approval_snapshots snapshot,
       jsonb_array_elements(snapshot.snapshot -> 'items') item
  where snapshot.fix_plan_id = new.fix_plan_id
    and item ->> 'itemId' = new.fix_plan_item_id::text;
  select finding_id into persisted_finding from public.fix_plan_items
    where id = new.fix_plan_item_id and fix_plan_id = new.fix_plan_id;
  if approved_item is null
    or approved_item ->> 'findingId' <> persisted_finding::text
    or approved_item ->> 'validationKey' <> new.validation_key
    or approved_item ->> 'capabilityId' <> new.fix_key
    or approved_item ->> 'capabilityVersion' <> new.fixer_version
    or approved_item #>> '{operation,propertyIdentifier}' <> new.property_identifier
    or (approved_item #>> '{operation,ordinal}')::integer <> new.operation_ordinal then
    raise exception 'Fix item result does not match the immutable approved item.' using errcode = '23514';
  end if;
  expected_anchor := jsonb_strip_nulls(jsonb_build_object(
    'schemaVersion', 'fix-structural-anchor/1.0',
    'scope', approved_item #>> '{operation,target,scope}',
    'bodyElementIndex', approved_item #> '{operation,target,bodyElementIndex}',
    'sectionIndex', approved_item #> '{operation,target,sectionIndex}',
    'paragraphIndex', approved_item #> '{operation,target,paragraphIndex}',
    'runIndex', approved_item #> '{operation,target,runIndex}'));
  if new.structural_anchor <> expected_anchor then
    raise exception 'Fix item result anchor does not match the approved item.' using errcode = '23514';
  end if;
  if new.result_document_version_id is not null
    and new.result_document_version_id <> new.fix_execution_job_id then
    raise exception 'Fix item result version lineage is invalid.' using errcode = '23514';
  end if;
  return new;
end;
$$;

revoke all on function private.enforce_fix_item_result()
  from public, anon, authenticated, service_role;
create trigger trg_fix_item_results_enforce_insert before insert on public.fix_item_results
for each row execute function private.enforce_fix_item_result();
create trigger trg_fix_item_results_reject_update before update on public.fix_item_results
for each row execute function private.enforce_fix_item_result();
create trigger trg_fix_item_results_reject_delete before delete on public.fix_item_results
for each row execute function private.enforce_fix_item_result();

create or replace function private.enforce_fix_item_result_aggregate()
returns trigger
language plpgsql
set search_path = ''
as $$
declare
  item_count integer;
  result_count integer;
  applied_count integer;
  skipped_count integer;
  failed_count integer;
  plan_state text;
begin
  if new.fix_plan_id is null or new.state not in ('Completed','NoChange','Failed') then return null; end if;
  select count(*) into item_count from public.fix_plan_items where fix_plan_id = new.fix_plan_id;
  select count(*), count(*) filter(where outcome = 'Applied'),
         count(*) filter(where outcome = 'Skipped'), count(*) filter(where outcome = 'Failed')
    into result_count, applied_count, skipped_count, failed_count
  from public.fix_item_results
  where fix_execution_job_id = new.id and attempt_number = new.attempt_count;
  select state into plan_state from public.fix_plans where id = new.fix_plan_id;
  if result_count <> item_count
    or (new.state = 'Completed' and (applied_count = 0 or failed_count <> 0
      or exists(select 1 from public.fix_item_results where fix_execution_job_id = new.id
        and attempt_number = new.attempt_count and result_document_version_id <> new.result_document_version_id)))
    or (new.state = 'NoChange' and (skipped_count <> item_count or applied_count <> 0 or failed_count <> 0
      or exists(select 1 from public.fix_item_results where fix_execution_job_id = new.id
        and attempt_number = new.attempt_count and result_document_version_id is not null)))
    or (new.state = 'Failed' and (failed_count <> item_count or applied_count <> 0 or skipped_count <> 0))
    or (new.state in ('Completed','NoChange') and plan_state <> 'Completed')
    or (new.state = 'Failed' and plan_state <> 'Failed') then
    raise exception 'Fix execution terminal aggregate is inconsistent with item outcomes.' using errcode = '23514';
  end if;
  return null;
end;
$$;

revoke all on function private.enforce_fix_item_result_aggregate()
  from public, anon, authenticated, service_role;
create constraint trigger trg_fix_execution_jobs_item_result_aggregate
after insert or update on public.fix_execution_jobs deferrable initially deferred
for each row execute function private.enforce_fix_item_result_aggregate();

alter table public.fix_item_results enable row level security;
revoke all on table public.fix_item_results from anon, authenticated, service_role;
grant select on table public.fix_item_results to authenticated;
grant select, insert on table public.fix_item_results to service_role;
create policy fix_item_results_select_owned on public.fix_item_results
for select to authenticated using (exists (
  select 1 from public.fix_plans plan
  where plan.id = fix_item_results.fix_plan_id and plan.owner_user_id = (select auth.uid())
));

comment on table public.fix_item_results is
  'Append-only, per-attempt outcomes for exact approved fix-plan items; payloads contain bounded structural formatting values only.';

commit;
