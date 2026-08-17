-- Keep historical backfill durable without delaying newly-created document versions.
begin;

alter table public.document_render_jobs
  add column priority integer not null default 0,
  add constraint ck_document_render_jobs_priority check (priority between 0 and 100);

-- Existing rows retain 0 from the add-column default. Lifecycle writers
-- created after this migration use the normal queue priority.
alter table public.document_render_jobs alter column priority set default 100;

create index ix_document_render_jobs_priority_queue
  on public.document_render_jobs(state, priority desc, next_attempt_at, created_at)
  where state in ('Pending','Processing');

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
      or old.render_identity is distinct from new.render_identity or old.priority is distinct from new.priority
      or old.created_at is distinct from new.created_at then
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

commit;
