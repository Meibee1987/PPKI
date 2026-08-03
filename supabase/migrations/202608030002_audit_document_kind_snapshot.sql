-- S2-T06 closure: preserve the document-kind applicability context per audit.

begin;

alter table public.audit_jobs
  add column if not exists document_kind_snapshot text;

alter table public.audit_jobs
  add constraint ck_audit_jobs_document_kind_snapshot
  check (
    document_kind_snapshot is null
    or document_kind_snapshot in ('LaporanAkhir', 'Skripsi', 'Tesis', 'Disertasi')
  ) not valid;

comment on column public.audit_jobs.document_kind_snapshot is
  'Immutable document kind captured when the audit job is created; NULL only for historical rows.';

create or replace function public.reject_audit_document_kind_snapshot_mutation()
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
    return new;
  end if;

  if old.document_kind_snapshot is distinct from new.document_kind_snapshot then
    raise exception using
      errcode = '55000',
      message = 'Audit job document kind snapshot is immutable';
  end if;

  return new;
end;
$$;

create trigger trg_audit_jobs_document_kind_snapshot_immutable
  before update on public.audit_jobs
  for each row execute function public.reject_audit_document_kind_snapshot_mutation();

commit;
