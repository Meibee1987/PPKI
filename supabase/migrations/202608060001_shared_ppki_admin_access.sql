-- S4-T04 final closure: ownership is provenance, while every exact database-role
-- PPKIAdmin shares internal business-resource read access.
begin;

drop policy if exists documents_select_internal_admin on public.documents;
drop policy if exists document_versions_select_internal_admin on public.document_versions;
drop policy if exists audit_jobs_select_internal_admin on public.audit_jobs;
drop policy if exists audit_findings_select_internal_admin on public.audit_findings;
drop policy if exists audit_rule_snapshots_select_internal_admin on public.audit_rule_snapshots;
drop policy if exists fix_execution_jobs_select_internal_admin on public.fix_execution_jobs;
drop policy if exists finding_resolution_cases_select_internal_admin on public.finding_resolution_cases;
drop policy if exists finding_resolution_events_select_internal_admin on public.finding_resolution_events;

create policy documents_select_internal_admin on public.documents for select to authenticated
  using (public.is_ppki_admin());
create policy document_versions_select_internal_admin on public.document_versions for select to authenticated
  using (public.is_ppki_admin());
create policy audit_jobs_select_internal_admin on public.audit_jobs for select to authenticated
  using (public.is_ppki_admin());
create policy audit_findings_select_internal_admin on public.audit_findings for select to authenticated
  using (public.is_ppki_admin());
create policy audit_rule_snapshots_select_internal_admin on public.audit_rule_snapshots for select to authenticated
  using (public.is_ppki_admin());
create policy fix_execution_jobs_select_internal_admin on public.fix_execution_jobs for select to authenticated
  using (public.is_ppki_admin());
create policy finding_resolution_cases_select_internal_admin on public.finding_resolution_cases for select to authenticated
  using (public.is_ppki_admin());
create policy finding_resolution_events_select_internal_admin on public.finding_resolution_events for select to authenticated
  using (public.is_ppki_admin());

comment on policy documents_select_internal_admin on public.documents is
  'All exact database-role PPKIAdmin users share internal document read access; owner_user_id remains provenance.';

commit;
