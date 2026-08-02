-- S1-T01: additive ownership and data-integrity contract.
-- Existing rows are deliberately not rewritten. NOT VALID checks protect every
-- new or changed row while allowing a separate, auditable legacy remediation.

alter table public.documents
  add column if not exists status text not null default 'Active';

alter table public.audit_jobs
  add column if not exists requested_by_user_id uuid;

alter table public.audit_findings
  add column if not exists rule_code_snapshot text,
  add column if not exists fix_mode_snapshot text,
  add column if not exists source_section_snapshot text,
  add column if not exists pdf_page_snapshot integer,
  add column if not exists printed_page_snapshot text;

create table if not exists public.profile_rules (
  id uuid primary key default gen_random_uuid(),
  profile_version_id uuid not null references public.profile_versions(id) on delete cascade,
  rule_id uuid not null references public.rules(id) on delete restrict,
  created_at timestamptz not null default now(),
  unique(profile_version_id, rule_id)
);

create index if not exists ix_document_versions_document on public.document_versions(document_id);
create index if not exists ix_audit_jobs_document_version on public.audit_jobs(document_version_id);
create index if not exists ix_audit_findings_audit_job on public.audit_findings(audit_job_id);

do $$
begin
  alter table public.audit_jobs
    add constraint fk_audit_jobs_requested_by_user
    foreign key (requested_by_user_id) references auth.users(id) on delete restrict not valid;
exception when duplicate_object then null;
end $$;

do $$
begin
  alter table public.documents add constraint ck_documents_title_not_blank
    check (btrim(title) <> '' and char_length(title) <= 512) not valid;
  alter table public.documents add constraint ck_documents_status
    check (status in ('Active', 'Archived')) not valid;
  alter table public.documents add constraint ck_documents_current_version_no_positive
    check (current_version_no > 0) not valid;
  alter table public.documents add constraint ck_documents_updated_at_order
    check (updated_at >= created_at) not valid;
exception when duplicate_object then null;
end $$;

do $$
begin
  alter table public.document_versions add constraint ck_document_versions_version_no_positive
    check (version_no > 0) not valid;
  -- Only the first version may omit a parent; non-first versions must retain lineage.
  alter table public.document_versions add constraint ck_document_versions_parent_required_after_first
    check (parent_version_id is not null or version_no = 1) not valid;
  alter table public.document_versions add constraint ck_document_versions_parent_not_self
    check (parent_version_id is null or parent_version_id <> id) not valid;
  alter table public.document_versions add constraint ck_document_versions_sha256_lowercase
    check (sha256 ~ '^[0-9a-f]{64}$') not valid;
  alter table public.document_versions add constraint ck_document_versions_size_bytes_positive
    check (size_bytes > 0) not valid;
  alter table public.document_versions add constraint ck_document_versions_storage_bucket_not_blank
    check (btrim(storage_bucket) <> '') not valid;
  -- Storage keys are logical private object keys, never URLs or host paths.
  alter table public.document_versions add constraint ck_document_versions_storage_key_safe
    check (
      btrim(storage_key) <> ''
      and storage_key !~ '^/'
      and storage_key !~ '(^|/)\.\.(/|$)'
      and position(E'\\' in storage_key) = 0
      and position('://' in storage_key) = 0
    ) not valid;
exception when duplicate_object then null;
end $$;

-- A CHECK cannot compare another row. This small trigger prevents a version
-- from naming a parent that belongs to a different document.
create or replace function public.enforce_document_version_parent_document()
returns trigger
language plpgsql
set search_path = public
as $$
declare
  parent_document_id uuid;
begin
  if new.parent_version_id is not null then
    select document_id into parent_document_id
    from public.document_versions
    where id = new.parent_version_id;

    if parent_document_id is distinct from new.document_id then
      raise exception 'parent_version_id must belong to the same document';
    end if;
  end if;

  return new;
end;
$$;

do $$
begin
  if not exists (
    select 1 from pg_trigger where tgname = 'trg_document_versions_parent_document'
  ) then
    create trigger trg_document_versions_parent_document
      before insert or update of document_id, parent_version_id on public.document_versions
      for each row execute function public.enforce_document_version_parent_document();
  end if;
end $$;

do $$
begin
  alter table public.audit_jobs add constraint ck_audit_jobs_requested_by_user
    check (requested_by_user_id is not null) not valid;
  alter table public.audit_jobs add constraint ck_audit_jobs_status
    check (status in ('Queued', 'Processing', 'Completed', 'Failed', 'Cancelled')) not valid;
  alter table public.audit_jobs add constraint ck_audit_jobs_counts_nonnegative
    check (total_rules >= 0 and error_count >= 0 and warning_count >= 0 and info_count >= 0) not valid;
  alter table public.audit_jobs add constraint ck_audit_jobs_terminal_timestamps
    check (
      (status in ('Queued', 'Processing') and completed_at is null)
      or (status in ('Completed', 'Failed', 'Cancelled') and completed_at is not null)
    ) not valid;
  alter table public.audit_jobs add constraint ck_audit_jobs_timestamp_order
    check (completed_at is null or (started_at is not null and completed_at >= started_at)) not valid;
  alter table public.audit_jobs add constraint ck_audit_jobs_completed_hash
    check (status <> 'Completed' or resolved_rule_set_hash ~ '^[0-9a-f]{64}$') not valid;
exception when duplicate_object then null;
end $$;

do $$
begin
  alter table public.audit_findings add constraint ck_audit_findings_severity
    check (severity in ('Error', 'Warning', 'Info')) not valid;
  alter table public.audit_findings add constraint ck_audit_findings_rule_snapshot
    check (btrim(rule_code_snapshot) <> '' and fix_mode_snapshot in ('Auto', 'Confirm', 'Manual', 'Report')) not valid;
  -- Validators may legitimately produce JSON null for actual or expected.
  alter table public.audit_findings add constraint ck_audit_findings_json_shape
    check (
      jsonb_typeof(actual_value) in ('object', 'array', 'null')
      and jsonb_typeof(expected_value) in ('object', 'array', 'null')
      and jsonb_typeof(location) in ('object', 'array', 'null')
    ) not valid;
  alter table public.audit_findings add constraint ck_audit_findings_message_not_blank
    check (btrim(message) <> '') not valid;
exception when duplicate_object then null;
end $$;

do $$
begin
  alter table public.profile_versions add constraint ck_profile_versions_status
    check (status in ('Draft', 'Active', 'Retired')) not valid;
  alter table public.rules add constraint ck_rules_severity
    check (severity in ('Error', 'Warning', 'Info')) not valid;
  alter table public.rules add constraint ck_rules_fix_mode
    check (fix_mode in ('Auto', 'Confirm', 'Manual', 'Report')) not valid;
exception when duplicate_object then null;
end $$;
