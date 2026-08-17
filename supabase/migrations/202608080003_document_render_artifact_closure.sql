-- Close immutable artifact entry sets when their render job completes.
begin;

create or replace function private.enforce_document_render_artifact()
returns trigger language plpgsql set search_path = '' as $$
declare job public.document_render_jobs%rowtype;
begin
  if tg_op <> 'INSERT' then
    raise exception using errcode = '55000', message = 'Document render artifact is immutable';
  end if;
  select * into job from public.document_render_jobs where id = new.render_job_id;
  if job.state <> 'Processing' then
    raise exception using errcode = '23514', message = 'Document render artifact requires active processing job';
  end if;
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

create or replace function private.reject_page_map_mutation()
returns trigger language plpgsql set search_path = '' as $$
declare job_state text;
begin
  if tg_op = 'INSERT' then
    select job.state into job_state
    from public.document_render_artifacts artifact
    join public.document_render_jobs job on job.id = artifact.render_job_id
    where artifact.id = new.render_artifact_id;
    if job_state <> 'Processing' then
      raise exception using errcode = '55000', message = 'Document page map entry set is closed';
    end if;
    return new;
  end if;
  raise exception using errcode = '55000', message = 'Document page map entry is immutable';
end $$;

create trigger trg_document_page_map_insert before insert on public.document_page_map_entries
  for each row execute function private.reject_page_map_mutation();

commit;
