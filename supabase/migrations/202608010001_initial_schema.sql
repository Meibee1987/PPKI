create extension if not exists pgcrypto;

create table if not exists public.user_profiles (
  id uuid primary key references auth.users(id) on delete cascade,
  email text not null,
  full_name text not null default '',
  role text not null default 'Student' check (role in ('Student','Reviewer','PPKIAdmin','UnitAdmin')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create or replace function public.handle_new_user() returns trigger language plpgsql security definer set search_path = '' as $$
begin
  insert into public.user_profiles (id,email,full_name)
  values (new.id,coalesce(new.email,''),coalesce(new.raw_user_meta_data ->> 'full_name',''))
  on conflict (id) do nothing;
  return new;
end; $$;
drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created after insert on auth.users for each row execute procedure public.handle_new_user();

create table if not exists public.document_types (
  id uuid primary key default gen_random_uuid(), code text not null unique, name text not null, kind text not null,
  created_at timestamptz not null default now()
);
create table if not exists public.formatting_profiles (
  id uuid primary key default gen_random_uuid(), name text not null, source_title text not null, edition text not null,
  created_at timestamptz not null default now()
);
create table if not exists public.profile_versions (
  id uuid primary key default gen_random_uuid(), profile_id uuid not null references public.formatting_profiles(id),
  version_no integer not null, status text not null, effective_at timestamptz, created_at timestamptz not null default now(),
  unique(profile_id,version_no)
);
create table if not exists public.rules (
  id uuid primary key default gen_random_uuid(), rule_code text not null unique, domain text not null, subdomain text,
  applies_to text not null, element text not null, official_requirement text not null, expected_value_pattern text not null,
  severity text not null, fix_mode text not null, validation_key text not null, is_implemented boolean not null default false,
  pdf_page integer, printed_page text, source_section text, created_at timestamptz not null default now()
);
create table if not exists public.documents (
  id uuid primary key default gen_random_uuid(), owner_user_id uuid not null references auth.users(id),
  document_type_id uuid not null references public.document_types(id), title text not null, current_version_no integer not null default 1,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now()
);
create index if not exists ix_documents_owner on public.documents(owner_user_id);
create table if not exists public.document_versions (
  id uuid primary key default gen_random_uuid(), document_id uuid not null references public.documents(id) on delete cascade,
  version_no integer not null, storage_bucket text not null, storage_key text not null, original_filename text not null,
  mime_type text not null, size_bytes bigint not null, sha256 text not null, created_by_user_id uuid not null references auth.users(id),
  parent_version_id uuid references public.document_versions(id), created_at timestamptz not null default now(),
  unique(document_id,version_no), unique(storage_bucket,storage_key)
);
create table if not exists public.audit_jobs (
  id uuid primary key default gen_random_uuid(), document_version_id uuid not null references public.document_versions(id),
  profile_version_id uuid not null references public.profile_versions(id), status text not null default 'Queued',
  resolved_rule_set_hash text, total_rules integer not null default 0, error_count integer not null default 0,
  warning_count integer not null default 0, info_count integer not null default 0, score numeric(5,2),
  started_at timestamptz, completed_at timestamptz, error_message text, created_at timestamptz not null default now()
);
create index if not exists ix_audit_jobs_status_created on public.audit_jobs(status,created_at);
create table if not exists public.audit_findings (
  id uuid primary key default gen_random_uuid(), audit_job_id uuid not null references public.audit_jobs(id) on delete cascade,
  rule_id uuid not null references public.rules(id), severity text not null, message text not null,
  actual_value jsonb not null, expected_value jsonb not null, location jsonb not null,
  confidence numeric(5,4), status text not null default 'Open', created_at timestamptz not null default now()
);

insert into public.document_types(id,code,name,kind) values
('10000000-0000-0000-0000-000000000001','LAPORAN_AKHIR','Laporan Akhir','LaporanAkhir'),
('10000000-0000-0000-0000-000000000002','SKRIPSI','Skripsi','Skripsi'),
('10000000-0000-0000-0000-000000000003','TESIS','Tesis','Tesis'),
('10000000-0000-0000-0000-000000000004','DISERTASI','Disertasi','Disertasi')
on conflict(code) do nothing;
insert into public.formatting_profiles(id,name,source_title,edition) values
('20000000-0000-0000-0000-000000000001','PPKI IPB Edisi Ke-4','Pedoman Penulisan Karya Ilmiah Tugas Akhir Mahasiswa','Edisi Ke-4 (2019)')
on conflict(id) do nothing;
insert into public.profile_versions(id,profile_id,version_no,status,effective_at) values
('21000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001',1,'Active','2019-01-01T00:00:00Z')
on conflict(profile_id,version_no) do nothing;

insert into storage.buckets (id,name,public,file_size_limit,allowed_mime_types) values
('documents-original','documents-original',false,52428800,array['application/vnd.openxmlformats-officedocument.wordprocessingml.document']),
('documents-versions','documents-versions',false,52428800,array['application/vnd.openxmlformats-officedocument.wordprocessingml.document']),
('audit-reports','audit-reports',false,52428800,array['application/pdf','application/json'])
on conflict(id) do update set public=false,file_size_limit=excluded.file_size_limit,allowed_mime_types=excluded.allowed_mime_types;

alter table public.user_profiles enable row level security;
alter table public.documents enable row level security;
alter table public.document_versions enable row level security;
alter table public.audit_jobs enable row level security;
alter table public.audit_findings enable row level security;
alter table public.document_types enable row level security;
alter table public.formatting_profiles enable row level security;
alter table public.profile_versions enable row level security;
alter table public.rules enable row level security;

create policy "profile read own" on public.user_profiles for select to authenticated using (id=auth.uid());
create policy "profile update own" on public.user_profiles for update to authenticated using (id=auth.uid()) with check (id=auth.uid());
create policy "documents read own" on public.documents for select to authenticated using (owner_user_id=auth.uid());
create policy "versions read own" on public.document_versions for select to authenticated using (exists(select 1 from public.documents d where d.id=document_id and d.owner_user_id=auth.uid()));
create policy "audits read own" on public.audit_jobs for select to authenticated using (exists(select 1 from public.document_versions v join public.documents d on d.id=v.document_id where v.id=document_version_id and d.owner_user_id=auth.uid()));
create policy "findings read own" on public.audit_findings for select to authenticated using (exists(select 1 from public.audit_jobs a join public.document_versions v on v.id=a.document_version_id join public.documents d on d.id=v.document_id where a.id=audit_job_id and d.owner_user_id=auth.uid()));
create policy "document types read" on public.document_types for select to authenticated using (true);
create policy "profiles read" on public.formatting_profiles for select to authenticated using (true);
create policy "profile versions read" on public.profile_versions for select to authenticated using (true);
create policy "rules read" on public.rules for select to authenticated using (true);

-- No storage.objects policies are created intentionally. Files are accessed only by the trusted ASP.NET API/worker using the secret key.
