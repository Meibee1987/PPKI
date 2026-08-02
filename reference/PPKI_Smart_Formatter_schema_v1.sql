-- PPKI Smart Formatter - PostgreSQL schema draft v1
-- Baseline: PPKI IPB Edisi Ke-4 + versioned rule profiles.

create extension if not exists pgcrypto;

create type user_status as enum ('active','invited','suspended');
create type profile_type as enum ('official','academic_unit','lecturer','custom');
create type version_status as enum ('draft','review','active','retired');
create type job_status as enum ('queued','processing','completed','failed','cancelled');
create type finding_status as enum ('open','ignored','fixed','manual_review');
create type review_status as enum ('requested','in_review','changes_requested','approved');

create table users (
  id uuid primary key default gen_random_uuid(),
  email text not null unique,
  full_name text not null,
  status user_status not null default 'active',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table roles (
  id uuid primary key default gen_random_uuid(),
  code text not null unique,
  name text not null
);

create table user_roles (
  user_id uuid not null references users(id) on delete cascade,
  role_id uuid not null references roles(id) on delete cascade,
  primary key (user_id, role_id)
);

create table academic_units (
  id uuid primary key default gen_random_uuid(),
  parent_id uuid references academic_units(id),
  name text not null,
  unit_type text not null check (unit_type in ('university','school','faculty','department','program')),
  is_active boolean not null default true
);

create table document_types (
  id uuid primary key default gen_random_uuid(),
  code text not null unique,
  name text not null,
  is_active boolean not null default true
);

create table formatting_profiles (
  id uuid primary key default gen_random_uuid(),
  base_profile_id uuid references formatting_profiles(id),
  academic_unit_id uuid references academic_units(id),
  owner_user_id uuid references users(id),
  name text not null,
  profile_type profile_type not null,
  description text,
  created_at timestamptz not null default now()
);

create table profile_versions (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references formatting_profiles(id) on delete cascade,
  version_no integer not null,
  status version_status not null default 'draft',
  effective_at timestamptz,
  approved_by uuid references users(id),
  approved_at timestamptz,
  change_summary text,
  created_at timestamptz not null default now(),
  unique(profile_id, version_no)
);

create table source_references (
  id uuid primary key default gen_random_uuid(),
  source_type text not null,
  title text not null,
  edition text,
  pdf_page integer,
  printed_page text,
  section text,
  evidence_url text,
  note text
);

create table rules (
  id uuid primary key default gen_random_uuid(),
  source_reference_id uuid references source_references(id),
  rule_code text not null unique,
  domain text not null,
  subdomain text,
  element text not null,
  severity text not null check (severity in ('Error','Warning','Info')),
  fix_mode text not null check (fix_mode in ('Auto','Confirm','Manual','Report')),
  validation_key text not null,
  is_active boolean not null default true
);

create table profile_rules (
  id uuid primary key default gen_random_uuid(),
  profile_version_id uuid not null references profile_versions(id) on delete cascade,
  rule_id uuid not null references rules(id),
  requirement_json jsonb not null,
  validation_json jsonb not null default '{}'::jsonb,
  is_override boolean not null default false,
  override_reason text,
  evidence_source_id uuid references source_references(id),
  unique(profile_version_id, rule_id)
);

create table documents (
  id uuid primary key default gen_random_uuid(),
  owner_user_id uuid not null references users(id),
  document_type_id uuid not null references document_types(id),
  academic_unit_id uuid references academic_units(id),
  title text not null,
  current_version_no integer not null default 1,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table document_versions (
  id uuid primary key default gen_random_uuid(),
  document_id uuid not null references documents(id) on delete cascade,
  version_no integer not null,
  file_path text not null,
  original_filename text not null,
  mime_type text not null,
  size_bytes bigint not null,
  sha256 text not null,
  created_by uuid not null references users(id),
  parent_version_id uuid references document_versions(id),
  created_at timestamptz not null default now(),
  unique(document_id, version_no)
);

create table audit_jobs (
  id uuid primary key default gen_random_uuid(),
  document_version_id uuid not null references document_versions(id),
  profile_version_id uuid not null references profile_versions(id),
  status job_status not null default 'queued',
  resolved_rule_set_hash text,
  total_rules integer,
  error_count integer not null default 0,
  warning_count integer not null default 0,
  info_count integer not null default 0,
  score numeric(5,2),
  started_at timestamptz,
  completed_at timestamptz,
  error_message text,
  created_at timestamptz not null default now()
);

create table audit_findings (
  id uuid primary key default gen_random_uuid(),
  audit_job_id uuid not null references audit_jobs(id) on delete cascade,
  rule_id uuid not null references rules(id),
  severity text not null,
  message text not null,
  actual_value_json jsonb,
  expected_value_json jsonb,
  location_json jsonb not null,
  confidence numeric(5,2),
  status finding_status not null default 'open',
  created_at timestamptz not null default now()
);

create table fix_actions (
  id uuid primary key default gen_random_uuid(),
  finding_id uuid not null references audit_findings(id) on delete cascade,
  requested_by uuid not null references users(id),
  action_type text not null check (action_type in ('auto','confirm','manual','ignore')),
  before_json jsonb,
  after_json jsonb,
  status job_status not null default 'queued',
  result_document_version_id uuid references document_versions(id),
  created_at timestamptz not null default now(),
  completed_at timestamptz
);

create table document_reviews (
  id uuid primary key default gen_random_uuid(),
  document_version_id uuid not null references document_versions(id),
  reviewer_user_id uuid not null references users(id),
  status review_status not null default 'requested',
  comment text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table exports (
  id uuid primary key default gen_random_uuid(),
  document_version_id uuid not null references document_versions(id),
  audit_job_id uuid references audit_jobs(id),
  requested_by uuid not null references users(id),
  format text not null check (format in ('docx','pdf','audit_report_pdf','audit_report_json')),
  file_path text not null,
  sha256 text,
  created_at timestamptz not null default now()
);

create index idx_profile_rules_version on profile_rules(profile_version_id);
create index idx_document_versions_document on document_versions(document_id, version_no desc);
create index idx_audit_jobs_version on audit_jobs(document_version_id, created_at desc);
create index idx_findings_audit_status on audit_findings(audit_job_id, status, severity);
