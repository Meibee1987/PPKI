-- S5-T04: immutable PDF preview artifacts and version-specific structural page maps.
begin;

create table public.document_render_jobs (
  id uuid primary key default gen_random_uuid(),
  document_version_id uuid not null references public.document_versions(id) on delete restrict,
  source_sha256 text not null,
  renderer_id text not null,
  renderer_version text not null,
  renderer_contract_version text not null,
  font_profile_version text not null,
  page_map_schema_version text not null,
  render_identity text not null,
  state text not null default 'Pending',
  claim_token uuid,
  attempt_count integer not null default 0,
  max_attempts integer not null default 3,
  next_attempt_at timestamptz,
  started_at timestamptz,
  lease_expires_at timestamptz,
  completed_at timestamptz,
  safe_failure_code text,
  created_at timestamptz not null default now(),
  constraint uq_document_render_jobs_identity unique (render_identity),
  constraint ck_document_render_jobs_hashes check (
    source_sha256 ~ '^[0-9a-f]{64}$' and render_identity ~ '^[0-9a-f]{64}$'),
  constraint ck_document_render_jobs_contract check (
    char_length(renderer_id) between 1 and 64
    and char_length(renderer_version) between 1 and 128
    and char_length(renderer_contract_version) between 1 and 64
    and char_length(font_profile_version) between 1 and 64
    and char_length(page_map_schema_version) between 1 and 64),
  constraint ck_document_render_jobs_state check (state in ('Pending','Processing','Completed','Failed')),
  constraint ck_document_render_jobs_attempts check (
    max_attempts between 1 and 10 and attempt_count between 0 and max_attempts),
  constraint ck_document_render_jobs_failure check (
    safe_failure_code is null or
    (char_length(safe_failure_code) between 1 and 128 and safe_failure_code ~ '^[a-z0-9][a-z0-9.-]*$')),
  constraint ck_document_render_jobs_runtime check (
    (state = 'Pending' and claim_token is null and lease_expires_at is null and completed_at is null and safe_failure_code is null)
    or (state = 'Processing' and claim_token is not null and started_at is not null and lease_expires_at is not null and completed_at is null and safe_failure_code is null)
    or (state = 'Completed' and claim_token is null and lease_expires_at is null and completed_at is not null and safe_failure_code is null)
    or (state = 'Failed' and claim_token is null and lease_expires_at is null and completed_at is not null and safe_failure_code is not null))
);

create index ix_document_render_jobs_queue on public.document_render_jobs(state, next_attempt_at, created_at)
  where state in ('Pending','Processing');
create index ix_document_render_jobs_version on public.document_render_jobs(document_version_id, created_at);

create table public.document_render_artifacts (
  id uuid primary key default gen_random_uuid(),
  render_job_id uuid not null unique references public.document_render_jobs(id) on delete restrict,
  document_version_id uuid not null references public.document_versions(id) on delete restrict,
  storage_bucket text not null,
  storage_key text not null,
  pdf_sha256 text not null,
  size_bytes bigint not null,
  page_count integer not null,
  renderer_id text not null,
  renderer_version text not null,
  renderer_contract_version text not null,
  font_profile_version text not null,
  page_map_schema_version text not null,
  source_sha256 text not null,
  source_text_fingerprint text not null,
  created_at timestamptz not null default now(),
  constraint uq_document_render_artifacts_object unique (storage_bucket, storage_key),
  constraint ck_document_render_artifacts_hashes check (
    pdf_sha256 ~ '^[0-9a-f]{64}$' and source_sha256 ~ '^[0-9a-f]{64}$'
    and source_text_fingerprint ~ '^[0-9a-f]{64}$'),
  constraint ck_document_render_artifacts_values check (
    size_bytes between 1 and 52428800 and page_count between 1 and 10000)
);
create index ix_document_render_artifacts_version on public.document_render_artifacts(document_version_id);

create table public.document_page_map_entries (
  id uuid primary key default gen_random_uuid(),
  render_artifact_id uuid not null references public.document_render_artifacts(id) on delete restrict,
  structural_location text not null,
  section_index integer,
  body_element_index integer,
  paragraph_index integer,
  run_index integer,
  table_index integer,
  row_index integer,
  cell_index integer,
  confidence text not null,
  page_number integer,
  safe_reason text,
  created_at timestamptz not null default now(),
  constraint uq_document_page_map_location unique (render_artifact_id, structural_location),
  constraint ck_document_page_map_indexes check (
    (section_index is null or section_index >= 0)
    and (body_element_index is null or body_element_index >= 0)
    and (paragraph_index is null or paragraph_index >= 0)
    and (run_index is null or run_index >= 0)
    and (table_index is null or table_index >= 0)
    and (row_index is null or row_index >= 0)
    and (cell_index is null or cell_index >= 0)),
  constraint ck_document_page_map_confidence check (
    (confidence in ('Exact','Estimated') and page_number >= 1 and safe_reason is null)
    or (confidence = 'Unavailable' and page_number is null and safe_reason is not null
      and char_length(safe_reason) between 1 and 128 and safe_reason ~ '^[a-z0-9][a-z0-9.-]*$'))
);
create index ix_document_page_map_lookup
  on public.document_page_map_entries(render_artifact_id, paragraph_index, run_index);

create or replace function private.enforce_document_render_job()
returns trigger language plpgsql set search_path = '' as $$
declare version_sha text;
begin
  if tg_op = 'DELETE' then
    raise exception using errcode = '55000', message = 'Document render job cannot be deleted';
  end if;
  select sha256 into version_sha from public.document_versions where id = new.document_version_id;
  if version_sha is distinct from new.source_sha256 then
    raise exception using errcode = '23514', message = 'Document render source hash mismatch';
  end if;
  if tg_op = 'INSERT' then
    if new.state <> 'Pending' or new.claim_token is not null or new.attempt_count <> 0
      or new.started_at is not null or new.lease_expires_at is not null
      or new.completed_at is not null or new.safe_failure_code is not null then
      raise exception using errcode = '23514', message = 'Document render job must start pending';
    end if;
  else
    if old.id is distinct from new.id or old.document_version_id is distinct from new.document_version_id
      or old.source_sha256 is distinct from new.source_sha256 or old.renderer_id is distinct from new.renderer_id
      or old.renderer_version is distinct from new.renderer_version
      or old.renderer_contract_version is distinct from new.renderer_contract_version
      or old.font_profile_version is distinct from new.font_profile_version
      or old.page_map_schema_version is distinct from new.page_map_schema_version
      or old.render_identity is distinct from new.render_identity or old.created_at is distinct from new.created_at then
      raise exception using errcode = '55000', message = 'Document render identity is immutable';
    end if;
    if old.state in ('Completed','Failed') then
      raise exception using errcode = '55000', message = 'Terminal document render job is immutable';
    end if;
    if new.state is distinct from old.state and not (
      old.state = 'Pending' and new.state = 'Processing'
      or old.state = 'Processing' and new.state in ('Pending','Completed','Failed')) then
      raise exception using errcode = '23514', message = 'Invalid document render state transition';
    end if;
  end if;
  return new;
end $$;

create trigger trg_document_render_job_insert before insert on public.document_render_jobs
  for each row execute function private.enforce_document_render_job();
create trigger trg_document_render_job_update before update on public.document_render_jobs
  for each row execute function private.enforce_document_render_job();
create trigger trg_document_render_job_delete before delete on public.document_render_jobs
  for each row execute function private.enforce_document_render_job();

create or replace function private.enforce_document_render_artifact()
returns trigger language plpgsql set search_path = '' as $$
declare job public.document_render_jobs%rowtype;
begin
  if tg_op <> 'INSERT' then
    raise exception using errcode = '55000', message = 'Document render artifact is immutable';
  end if;
  select * into job from public.document_render_jobs where id = new.render_job_id;
  if job.document_version_id is distinct from new.document_version_id
    or job.source_sha256 is distinct from new.source_sha256
    or job.renderer_id is distinct from new.renderer_id
    or job.renderer_version is distinct from new.renderer_version
    or job.renderer_contract_version is distinct from new.renderer_contract_version
    or job.font_profile_version is distinct from new.font_profile_version
    or job.page_map_schema_version is distinct from new.page_map_schema_version then
    raise exception using errcode = '23514', message = 'Document render artifact lineage mismatch';
  end if;
  return new;
end $$;

create trigger trg_document_render_artifact_insert before insert on public.document_render_artifacts
  for each row execute function private.enforce_document_render_artifact();
create trigger trg_document_render_artifact_update before update on public.document_render_artifacts
  for each row execute function private.enforce_document_render_artifact();
create trigger trg_document_render_artifact_delete before delete on public.document_render_artifacts
  for each row execute function private.enforce_document_render_artifact();

create or replace function private.reject_page_map_mutation()
returns trigger language plpgsql set search_path = '' as $$
begin
  raise exception using errcode = '55000', message = 'Document page map entry is immutable';
end $$;
create trigger trg_document_page_map_update before update on public.document_page_map_entries
  for each row execute function private.reject_page_map_mutation();
create trigger trg_document_page_map_delete before delete on public.document_page_map_entries
  for each row execute function private.reject_page_map_mutation();

alter table public.document_render_jobs enable row level security;
alter table public.document_render_artifacts enable row level security;
alter table public.document_page_map_entries enable row level security;
revoke all on public.document_render_jobs, public.document_render_artifacts, public.document_page_map_entries from anon, authenticated;
grant select on public.document_render_jobs, public.document_render_artifacts, public.document_page_map_entries to authenticated;
grant select, insert, update on public.document_render_jobs to service_role;
grant select, insert on public.document_render_artifacts, public.document_page_map_entries to service_role;
revoke delete on public.document_render_jobs, public.document_render_artifacts, public.document_page_map_entries from service_role;

create policy document_render_jobs_select_internal_admin on public.document_render_jobs for select to authenticated
  using (exists(select 1 from public.user_profiles p where p.id=(select auth.uid()) and p.role='PPKIAdmin'));
create policy document_render_artifacts_select_internal_admin on public.document_render_artifacts for select to authenticated
  using (exists(select 1 from public.user_profiles p where p.id=(select auth.uid()) and p.role='PPKIAdmin'));
create policy document_page_map_entries_select_internal_admin on public.document_page_map_entries for select to authenticated
  using (exists(select 1 from public.user_profiles p where p.id=(select auth.uid()) and p.role='PPKIAdmin'));

insert into public.document_render_jobs (
  document_version_id, source_sha256, renderer_id, renderer_version,
  renderer_contract_version, font_profile_version, page_map_schema_version, render_identity)
select version.id, version.sha256, 'gotenberg-libreoffice', '8.34.0+libreoffice-26.2.4.2',
  'docx-pdf/1.0', 'ppki-liberation-noto/1.0', 'page-map/1.0',
  encode(digest(concat_ws(E'\n', version.id::text, version.sha256, 'gotenberg-libreoffice',
    '8.34.0+libreoffice-26.2.4.2', 'docx-pdf/1.0', 'ppki-liberation-noto/1.0'), 'sha256'), 'hex')
from public.document_versions version
on conflict (render_identity) do nothing;

comment on table public.document_render_artifacts is
  'Immutable canonical PPKI PDF previews; Exact page means exact page in this renderer environment.';
comment on table public.document_page_map_entries is
  'Version-specific structural locations resolved from non-visible DOCX bookmarks to PDF named destinations.';

commit;
