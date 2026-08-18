-- S5-T07: purpose-specific language correction proposals, decisions, and batches.
begin;

create table public.text_correction_analyses (
  id uuid primary key default gen_random_uuid(),
  audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  document_version_id uuid not null references public.document_versions(id) on delete restrict,
  source_sha256 text not null check (source_sha256 ~ '^[0-9a-f]{64}$'),
  detector_id text not null check (detector_id ~ '^[a-z0-9][a-z0-9.-]{0,63}$'),
  detector_version text not null check (detector_version ~ '^[a-z0-9][a-z0-9./-]{0,63}$'),
  catalog_version text not null check (catalog_version ~ '^[a-z0-9][a-z0-9./-]{0,63}$'),
  state text not null default 'Pending' check (state in ('Pending','Processing','Completed','Failed','Skipped')),
  proposal_count integer not null default 0 check (proposal_count between 0 and 10000),
  safe_failure_code text check (safe_failure_code ~ '^[a-z0-9][a-z0-9.-]{0,127}$'),
  started_at timestamptz,
  completed_at timestamptz,
  created_at timestamptz not null default now(),
  constraint uq_text_correction_analysis_audit unique (audit_job_id)
);

create table public.text_correction_proposals (
  id uuid primary key default gen_random_uuid(),
  analysis_id uuid not null references public.text_correction_analyses(id) on delete restrict,
  audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  document_version_id uuid not null references public.document_versions(id) on delete restrict,
  source_sha256 text not null check (source_sha256 ~ '^[0-9a-f]{64}$'),
  detector_id text not null check (detector_id ~ '^[a-z0-9][a-z0-9.-]{0,63}$'),
  detector_version text not null check (detector_version ~ '^[a-z0-9][a-z0-9./-]{0,63}$'),
  catalog_version text not null check (catalog_version ~ '^[a-z0-9][a-z0-9./-]{0,63}$'),
  catalog_rule_id text not null check (catalog_rule_id ~ '^[a-z0-9][a-z0-9.-]{0,127}$'),
  category text not null check (category ~ '^[a-z0-9][a-z0-9.-]{0,63}$'),
  anchor_contract_version text not null check (anchor_contract_version = 'text-anchor/1.0'),
  anchor_evidence jsonb not null check (
    jsonb_typeof(anchor_evidence) = 'object'
    and anchor_evidence->>'contractVersion' = 'text-anchor/1.0'
    and not (anchor_evidence ?| array['targetText','context','sourceText','paragraphText','replacementText'])),
  anchor_hash text not null check (anchor_hash ~ '^[0-9a-f]{64}$'),
  suggested_replacement text not null check (char_length(suggested_replacement) between 1 and 256),
  suggestion_hash text not null check (suggestion_hash ~ '^[0-9a-f]{64}$'),
  proposal_identity text not null check (proposal_identity ~ '^[0-9a-f]{64}$'),
  created_at timestamptz not null default now(),
  constraint uq_text_correction_proposal_identity unique (proposal_identity)
);

create table public.text_correction_decision_events (
  id uuid primary key default gen_random_uuid(),
  proposal_id uuid not null references public.text_correction_proposals(id) on delete restrict,
  sequence integer not null check (sequence > 0),
  actor_user_id uuid not null references auth.users(id) on delete restrict,
  action text not null check (action in ('UseSuggestion','EditManual','Ignore')),
  source_document_version_id uuid not null references public.document_versions(id) on delete restrict,
  anchor_hash text not null check (anchor_hash ~ '^[0-9a-f]{64}$'),
  manual_replacement text check (manual_replacement is null or char_length(manual_replacement) between 1 and 256),
  replacement_hash text check (replacement_hash is null or replacement_hash ~ '^[0-9a-f]{64}$'),
  idempotency_key uuid not null,
  semantic_hash text not null check (semantic_hash ~ '^[0-9a-f]{64}$'),
  created_at timestamptz not null default now(),
  constraint uq_text_correction_decision_sequence unique (proposal_id, sequence),
  constraint uq_text_correction_decision_idempotency unique (proposal_id, idempotency_key),
  constraint ck_text_correction_decision_payload check (
    (action = 'UseSuggestion' and manual_replacement is null and replacement_hash is not null)
    or (action = 'EditManual' and manual_replacement is not null and replacement_hash is not null)
    or (action = 'Ignore' and manual_replacement is null and replacement_hash is null))
);

create table public.text_correction_batches (
  id uuid primary key default gen_random_uuid(),
  source_audit_job_id uuid not null references public.audit_jobs(id) on delete restrict,
  source_document_version_id uuid not null references public.document_versions(id) on delete restrict,
  actor_user_id uuid not null references auth.users(id) on delete restrict,
  idempotency_key uuid not null,
  decision_set_hash text not null check (decision_set_hash ~ '^[0-9a-f]{64}$'),
  decision_count integer not null check (decision_count between 1 and 100),
  state text not null default 'Pending' check (state in
    ('Pending','Queued','Processing','ReauditPending','VerificationPending','Completed','Failed','Conflict')),
  fix_execution_id uuid references public.fix_execution_jobs(id) on delete restrict,
  result_document_version_id uuid references public.document_versions(id) on delete restrict,
  reaudit_job_id uuid references public.audit_jobs(id) on delete restrict,
  safe_failure_code text check (safe_failure_code ~ '^[a-z0-9][a-z0-9.-]{0,127}$'),
  updated_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  constraint uq_text_correction_batch_idempotency unique (source_audit_job_id, actor_user_id, idempotency_key),
  constraint uq_text_correction_batch_decisions unique (source_document_version_id, decision_set_hash),
  constraint uq_text_correction_batch_execution unique (fix_execution_id),
  constraint uq_text_correction_batch_reaudit unique (reaudit_job_id)
);

create table public.text_correction_batch_items (
  id uuid primary key default gen_random_uuid(),
  batch_id uuid not null references public.text_correction_batches(id) on delete restrict,
  decision_event_id uuid not null references public.text_correction_decision_events(id) on delete restrict,
  ordinal integer not null check (ordinal between 1 and 100),
  verification_state text not null default 'Applied' check (verification_state in
    ('Applied','ReauditPending','VerifiedResolved','VerifiedStillDetected','VerificationUnavailable')),
  verified_at timestamptz,
  created_at timestamptz not null default now(),
  constraint uq_text_correction_batch_item_ordinal unique (batch_id, ordinal),
  constraint uq_text_correction_batch_item_decision unique (decision_event_id)
);

create index ix_text_correction_analyses_worker on public.text_correction_analyses(state, created_at);
create index ix_text_correction_proposals_page on public.text_correction_proposals(audit_job_id, created_at, id);
create index ix_text_correction_decisions_latest on public.text_correction_decision_events(proposal_id, sequence desc);
create index ix_text_correction_batches_worker on public.text_correction_batches(state, updated_at);

create or replace function private.enforce_text_correction_evidence()
returns trigger language plpgsql set search_path = '' as $$
declare
  audit_version uuid;
  version_sha text;
  analysis_audit uuid;
  analysis_version uuid;
  analysis_sha text;
  proposal_version uuid;
  proposal_anchor text;
  proposal_suggestion text;
  actor_role text;
  expected_sequence integer;
begin
  if tg_op = 'DELETE' then
    raise exception using errcode = '55000', message = 'Text correction evidence cannot be deleted';
  end if;
  if tg_table_name = 'text_correction_analyses' then
    if tg_op = 'INSERT' then
      select audit.document_version_id, version.sha256 into audit_version, version_sha
      from public.audit_jobs audit join public.document_versions version on version.id=audit.document_version_id
      where audit.id=new.audit_job_id and audit.status='Completed';
      if audit_version is null or audit_version<>new.document_version_id or version_sha<>new.source_sha256
        or new.state<>'Pending' or new.proposal_count<>0 or new.started_at is not null or new.completed_at is not null then
        raise exception using errcode='23514', message='Text correction analysis source is invalid';
      end if;
    elsif old.id is distinct from new.id or old.audit_job_id is distinct from new.audit_job_id
      or old.document_version_id is distinct from new.document_version_id or old.source_sha256 is distinct from new.source_sha256
      or old.detector_id is distinct from new.detector_id or old.detector_version is distinct from new.detector_version
      or old.catalog_version is distinct from new.catalog_version or old.created_at is distinct from new.created_at then
      raise exception using errcode='55000', message='Text correction analysis identity is immutable';
    end if;
    if tg_op='UPDATE' and not (
      (old.state='Pending' and new.state in ('Processing','Failed','Skipped'))
      or (old.state='Processing' and new.state in ('Completed','Failed'))
      or old.state=new.state) then
      raise exception using errcode='23514',message='Text correction analysis transition is invalid';
    end if;
    if new.state='Completed' and (new.completed_at is null or new.proposal_count<0) then
      raise exception using errcode='23514',message='Text correction analysis completion is invalid';
    end if;
  elsif tg_table_name = 'text_correction_proposals' then
    if tg_op = 'UPDATE' then raise exception using errcode='55000', message='Text correction proposals are immutable'; end if;
    select audit_job_id, document_version_id, source_sha256 into analysis_audit,analysis_version,analysis_sha
      from public.text_correction_analyses where id=new.analysis_id and state='Processing';
    if analysis_audit is null or analysis_audit<>new.audit_job_id or analysis_version<>new.document_version_id
      or analysis_sha<>new.source_sha256 then
      raise exception using errcode='23514', message='Text correction proposal lineage is invalid';
    end if;
  elsif tg_table_name = 'text_correction_decision_events' then
    if tg_op = 'UPDATE' then raise exception using errcode='55000', message='Text correction decisions are append-only'; end if;
    select document_version_id,anchor_hash,suggestion_hash into proposal_version,proposal_anchor,proposal_suggestion
      from public.text_correction_proposals where id=new.proposal_id;
    select role into actor_role from public.user_profiles where id=new.actor_user_id;
    if proposal_version is null or proposal_version<>new.source_document_version_id
      or proposal_anchor<>new.anchor_hash or actor_role<>'PPKIAdmin' then
      raise exception using errcode='23514', message='Text correction decision lineage is invalid';
    end if;
    select coalesce(max(sequence),0)+1 into expected_sequence from public.text_correction_decision_events
      where proposal_id=new.proposal_id;
    if new.sequence<>expected_sequence or (new.action='UseSuggestion' and new.replacement_hash<>proposal_suggestion) then
      raise exception using errcode='23514',message='Text correction decision evidence is invalid';
    end if;
  end if;
  return new;
end; $$;

create trigger trg_text_correction_analyses_guard before insert or update or delete on public.text_correction_analyses
  for each row execute function private.enforce_text_correction_evidence();
create trigger trg_text_correction_proposals_guard before insert or update or delete on public.text_correction_proposals
  for each row execute function private.enforce_text_correction_evidence();
create trigger trg_text_correction_decisions_guard before insert or update or delete on public.text_correction_decision_events
  for each row execute function private.enforce_text_correction_evidence();

create or replace function private.enforce_text_correction_batch()
returns trigger language plpgsql set search_path = '' as $$
declare actor_role text; audit_version uuid; current_version uuid;
begin
  if tg_op='DELETE' then raise exception using errcode='55000',message='Text correction batches cannot be deleted'; end if;
  if tg_op='INSERT' then
    select role into actor_role from public.user_profiles where id=new.actor_user_id;
    select audit.document_version_id into audit_version from public.audit_jobs audit where audit.id=new.source_audit_job_id and audit.status='Completed';
    select version.id into current_version from public.document_versions version join public.documents document
      on document.id=version.document_id and document.current_version_no=version.version_no where version.id=new.source_document_version_id;
    if actor_role<>'PPKIAdmin' or audit_version is null or audit_version<>new.source_document_version_id
      or current_version is null or new.state<>'Pending' or new.fix_execution_id is not null
      or new.result_document_version_id is not null or new.reaudit_job_id is not null or new.safe_failure_code is not null then
      raise exception using errcode='23514',message='Text correction batch source is invalid';
    end if;
  elsif old.id is distinct from new.id or old.source_audit_job_id is distinct from new.source_audit_job_id
    or old.source_document_version_id is distinct from new.source_document_version_id or old.actor_user_id is distinct from new.actor_user_id
    or old.idempotency_key is distinct from new.idempotency_key or old.decision_set_hash is distinct from new.decision_set_hash
    or old.decision_count is distinct from new.decision_count or old.created_at is distinct from new.created_at then
    raise exception using errcode='55000',message='Text correction batch request is immutable';
  end if;
  if tg_op='UPDATE' and not (
    (old.state='Pending' and new.state='Queued')
    or (old.state='Queued' and new.state in ('Queued','Processing','ReauditPending','Failed','Conflict'))
    or (old.state='Processing' and new.state in ('Processing','ReauditPending','Failed','Conflict'))
    or (old.state='ReauditPending' and new.state in ('ReauditPending','VerificationPending','Failed'))
    or (old.state='VerificationPending' and new.state in ('VerificationPending','Completed','Failed'))
    or old.state=new.state) then
    raise exception using errcode='23514',message='Text correction batch transition is invalid';
  end if;
  return new;
end; $$;
create trigger trg_text_correction_batches_guard before insert or update or delete on public.text_correction_batches
  for each row execute function private.enforce_text_correction_batch();

create or replace function private.enforce_text_correction_batch_item()
returns trigger language plpgsql set search_path = '' as $$
declare batch_version uuid; decision_version uuid; decision_action text; decision_sequence integer; latest_sequence integer;
begin
  if tg_op='DELETE' then raise exception using errcode='55000',message='Text correction batch items cannot be deleted'; end if;
  if tg_op='UPDATE' and (old.id is distinct from new.id or old.batch_id is distinct from new.batch_id
    or old.decision_event_id is distinct from new.decision_event_id or old.ordinal is distinct from new.ordinal
    or old.created_at is distinct from new.created_at) then
    raise exception using errcode='55000',message='Text correction batch item identity is immutable';
  end if;
  if tg_op='INSERT' then
    select source_document_version_id into batch_version from public.text_correction_batches where id=new.batch_id;
    select source_document_version_id,action,sequence into decision_version,decision_action,decision_sequence
      from public.text_correction_decision_events where id=new.decision_event_id;
    select max(sequence) into latest_sequence from public.text_correction_decision_events
      where proposal_id=(select proposal_id from public.text_correction_decision_events where id=new.decision_event_id);
    if batch_version is null or decision_version<>batch_version or decision_action='Ignore'
      or decision_sequence<>latest_sequence then
      raise exception using errcode='23514',message='Text correction batch item evidence is invalid';
    end if;
  end if;
  return new;
end; $$;
create trigger trg_text_correction_batch_items_guard before insert or update or delete on public.text_correction_batch_items
  for each row execute function private.enforce_text_correction_batch_item();

alter table public.text_correction_analyses enable row level security;
alter table public.text_correction_proposals enable row level security;
alter table public.text_correction_decision_events enable row level security;
alter table public.text_correction_batches enable row level security;
alter table public.text_correction_batch_items enable row level security;

revoke all on public.text_correction_analyses,public.text_correction_proposals,
  public.text_correction_decision_events,public.text_correction_batches,public.text_correction_batch_items from anon,authenticated;
grant select on public.text_correction_analyses,public.text_correction_proposals,
  public.text_correction_decision_events,public.text_correction_batches,public.text_correction_batch_items to authenticated;
grant select,insert,update on public.text_correction_analyses,public.text_correction_proposals,
  public.text_correction_decision_events,public.text_correction_batches,public.text_correction_batch_items to service_role;
revoke delete on public.text_correction_analyses,public.text_correction_proposals,
  public.text_correction_decision_events,public.text_correction_batches,public.text_correction_batch_items from service_role;

create policy text_correction_analyses_admin_read on public.text_correction_analyses for select to authenticated
  using (exists(select 1 from public.user_profiles p where p.id=(select auth.uid()) and p.role='PPKIAdmin'));
create policy text_correction_proposals_admin_read on public.text_correction_proposals for select to authenticated
  using (exists(select 1 from public.user_profiles p where p.id=(select auth.uid()) and p.role='PPKIAdmin'));
create policy text_correction_decisions_admin_read on public.text_correction_decision_events for select to authenticated
  using (exists(select 1 from public.user_profiles p where p.id=(select auth.uid()) and p.role='PPKIAdmin'));
create policy text_correction_batches_admin_read on public.text_correction_batches for select to authenticated
  using (exists(select 1 from public.user_profiles p where p.id=(select auth.uid()) and p.role='PPKIAdmin'));
create policy text_correction_batch_items_admin_read on public.text_correction_batch_items for select to authenticated
  using (exists(select 1 from public.user_profiles p where p.id=(select auth.uid()) and p.role='PPKIAdmin'));

comment on table public.text_correction_proposals is 'Immutable anchor-only proposal evidence; no source excerpt or paragraph text.';
comment on table public.text_correction_decision_events is 'Append-only explicit PPKIAdmin correction decisions.';
comment on table public.text_correction_batches is 'One bounded decision set producing at most one canonical result version.';
commit;
