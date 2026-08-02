-- S1-T05: append-only operational audit trail. Historical activity before this
-- migration is intentionally not reconstructed with guessed actors or times.

begin;

create schema if not exists private;
revoke all on schema private from public, anon, authenticated, service_role;

create table public.audit_trail_events (
  id uuid primary key default gen_random_uuid(),
  occurred_at timestamptz not null default now(),
  actor_type text not null,
  actor_user_id uuid references auth.users(id) on delete restrict,
  actor_service text,
  action text not null,
  resource_type text not null,
  resource_id uuid,
  owner_user_id uuid references auth.users(id) on delete restrict,
  correlation_id uuid not null,
  causation_id uuid,
  request_id text,
  metadata jsonb not null default '{}'::jsonb,
  event_schema_version integer not null default 1,
  event_source text not null,
  constraint ck_audit_trail_actor_type
    check (actor_type in ('user', 'service', 'system')),
  constraint ck_audit_trail_actor_identity
    check (
      (actor_type = 'user' and actor_user_id is not null and actor_service is null)
      or (actor_type = 'service' and actor_user_id is null and actor_service in ('api', 'worker', 'database', 'maintenance'))
      or (actor_type = 'system' and actor_user_id is null and actor_service is null)
    ),
  constraint ck_audit_trail_action
    check (char_length(action) between 3 and 128 and action ~ '^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$'),
  constraint ck_audit_trail_resource_type
    check (char_length(resource_type) between 1 and 64 and resource_type ~ '^[a-z][a-z0-9_]*$'),
  constraint ck_audit_trail_resource_identity
    check (resource_id is not null or actor_type = 'system'),
  constraint ck_audit_trail_correlation
    check (correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
  constraint ck_audit_trail_causation
    check (causation_id is null or causation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
  constraint ck_audit_trail_request_id
    check (request_id is null or (char_length(request_id) between 1 and 128 and request_id ~ '^[A-Za-z0-9._:-]+$')),
  constraint ck_audit_trail_metadata_object
    check (jsonb_typeof(metadata) = 'object'),
  constraint ck_audit_trail_metadata_allowlist
    check (
      metadata - array[
        'version_number', 'previous_status', 'new_status', 'audit_status',
        'applicable_rule_count', 'finding_count', 'file_size_bytes',
        'mime_type', 'failure_category', 'cleanup_reason', 'download_kind'
      ]::text[] = '{}'::jsonb
    ),
  constraint ck_audit_trail_schema_version
    check (event_schema_version > 0),
  constraint ck_audit_trail_event_source
    check (event_source in ('application', 'database_trigger'))
);

create index ix_audit_trail_occurred_at
  on public.audit_trail_events(occurred_at);
create index ix_audit_trail_correlation_id
  on public.audit_trail_events(correlation_id);
create index ix_audit_trail_resource
  on public.audit_trail_events(resource_type, resource_id);
create index ix_audit_trail_owner_occurred
  on public.audit_trail_events(owner_user_id, occurred_at desc);
create index ix_audit_trail_actor_occurred
  on public.audit_trail_events(actor_user_id, occurred_at desc);
create unique index uq_audit_trail_semantic_event
  on public.audit_trail_events(action, resource_type, resource_id, correlation_id)
  where resource_id is not null;

create or replace function private.reject_audit_trail_mutation()
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
    message = 'Audit trail events are append-only.';
end;
$$;

create trigger trg_audit_trail_events_reject_update
  before update on public.audit_trail_events
  for each row execute function private.reject_audit_trail_mutation();

create trigger trg_audit_trail_events_reject_delete
  before delete on public.audit_trail_events
  for each row execute function private.reject_audit_trail_mutation();

create or replace function private.append_audit_trail_event(
  p_action text,
  p_resource_type text,
  p_resource_id uuid,
  p_owner_user_id uuid,
  p_metadata jsonb,
  p_default_correlation_id uuid
)
returns void
language plpgsql
security definer
set search_path = ''
as $$
declare
  actor_user_text text := nullif(pg_catalog.current_setting('app.actor_user_id', true), '');
  actor_service_text text := nullif(pg_catalog.current_setting('app.actor_service', true), '');
  correlation_text text := nullif(pg_catalog.current_setting('app.correlation_id', true), '');
  causation_text text := nullif(pg_catalog.current_setting('app.causation_id', true), '');
  request_text text := nullif(pg_catalog.current_setting('app.request_id', true), '');
  resolved_actor_type text := 'system';
  resolved_actor_user_id uuid;
  resolved_actor_service text;
  resolved_correlation_id uuid := coalesce(p_default_correlation_id, gen_random_uuid());
  resolved_causation_id uuid;
  resolved_request_id text;
begin
  if actor_user_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$' then
    resolved_actor_type := 'user';
    resolved_actor_user_id := actor_user_text::uuid;
  elsif actor_service_text in ('api', 'worker', 'database', 'maintenance') then
    resolved_actor_type := 'service';
    resolved_actor_service := actor_service_text;
  end if;

  if correlation_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$' then
    resolved_correlation_id := correlation_text::uuid;
  end if;
  if causation_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$' then
    resolved_causation_id := causation_text::uuid;
  end if;
  if request_text ~ '^[A-Za-z0-9._:-]{1,128}$' then
    resolved_request_id := request_text;
  end if;

  insert into public.audit_trail_events (
    actor_type, actor_user_id, actor_service, action, resource_type,
    resource_id, owner_user_id, correlation_id, causation_id, request_id,
    metadata, event_schema_version, event_source
  ) values (
    resolved_actor_type, resolved_actor_user_id, resolved_actor_service,
    p_action, p_resource_type, p_resource_id, p_owner_user_id,
    resolved_correlation_id, resolved_causation_id, resolved_request_id,
    coalesce(p_metadata, '{}'::jsonb), 1, 'database_trigger'
  )
  on conflict (action, resource_type, resource_id, correlation_id)
    where resource_id is not null
    do nothing;
end;
$$;

create or replace function private.capture_document_created_event()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  perform private.append_audit_trail_event(
    'document.created', 'document', new.id, new.owner_user_id,
    '{}'::jsonb, new.id);
  return new;
end;
$$;

create or replace function private.capture_document_status_event()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  perform private.append_audit_trail_event(
    'document.status_changed', 'document', new.id, new.owner_user_id,
    pg_catalog.jsonb_build_object('previous_status', old.status, 'new_status', new.status),
    new.id);
  return new;
end;
$$;

create or replace function private.capture_document_version_event()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
  resource_owner uuid;
begin
  select document.owner_user_id into resource_owner
  from public.documents as document
  where document.id = new.document_id;

  perform private.append_audit_trail_event(
    'document.version_created', 'document_version', new.id, resource_owner,
    pg_catalog.jsonb_build_object(
      'version_number', new.version_no,
      'file_size_bytes', new.size_bytes,
      'mime_type', new.mime_type
    ),
    new.id);
  return new;
end;
$$;

create or replace function private.capture_audit_status_event()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
  resource_owner uuid;
  event_action text;
  event_metadata jsonb;
  finding_total integer;
begin
  select document.owner_user_id into resource_owner
  from public.document_versions as version
  join public.documents as document on document.id = version.document_id
  where version.id = new.document_version_id;

  if new.status = 'Processing' then
    event_action := 'audit.processing_started';
    event_metadata := pg_catalog.jsonb_build_object('previous_status', old.status, 'new_status', new.status);
  elsif new.status = 'Completed' then
    select count(*) into finding_total
    from public.audit_findings as finding
    where finding.audit_job_id = new.id;
    event_action := 'audit.completed';
    event_metadata := pg_catalog.jsonb_build_object(
      'audit_status', new.status,
      'applicable_rule_count', new.applicable_rule_count,
      'finding_count', finding_total
    );
  elsif new.status = 'Failed' then
    event_action := 'audit.failed';
    event_metadata := pg_catalog.jsonb_build_object(
      'audit_status', new.status,
      'failure_category', 'processing_error'
    );
  elsif new.status = 'Cancelled' then
    event_action := 'audit.cancelled';
    event_metadata := pg_catalog.jsonb_build_object('previous_status', old.status, 'new_status', new.status);
  else
    return new;
  end if;

  perform private.append_audit_trail_event(
    event_action, 'audit_job', new.id, resource_owner,
    event_metadata, new.id);
  return new;
end;
$$;

create trigger trg_documents_capture_created_event
  after insert on public.documents
  for each row execute function private.capture_document_created_event();

create trigger trg_documents_capture_status_event
  after update of status on public.documents
  for each row when (old.status is distinct from new.status)
  execute function private.capture_document_status_event();

create trigger trg_document_versions_capture_created_event
  after insert on public.document_versions
  for each row execute function private.capture_document_version_event();

create trigger trg_audit_jobs_capture_status_event
  after update of status on public.audit_jobs
  for each row when (old.status is distinct from new.status)
  execute function private.capture_audit_status_event();

revoke all on function private.reject_audit_trail_mutation() from public, anon, authenticated, service_role;
revoke all on function private.append_audit_trail_event(text, text, uuid, uuid, jsonb, uuid) from public, anon, authenticated, service_role;
revoke all on function private.capture_document_created_event() from public, anon, authenticated, service_role;
revoke all on function private.capture_document_status_event() from public, anon, authenticated, service_role;
revoke all on function private.capture_document_version_event() from public, anon, authenticated, service_role;
revoke all on function private.capture_audit_status_event() from public, anon, authenticated, service_role;

alter table public.audit_trail_events enable row level security;
revoke all on table public.audit_trail_events from anon, authenticated, service_role;
grant insert on table public.audit_trail_events to service_role;

commit;
