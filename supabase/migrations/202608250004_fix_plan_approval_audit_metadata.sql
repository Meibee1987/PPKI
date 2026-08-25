-- S7-T08: permit the bounded metadata emitted by the S7-T06 approval audit event.
-- migration-hygiene: allow-destructive replace-audit-metadata-allowlist-for-three-fix-plan-approval-keys
alter table public.audit_trail_events
  drop constraint ck_audit_trail_metadata_allowlist;

alter table public.audit_trail_events
  add constraint ck_audit_trail_metadata_allowlist
  check (
    metadata - array[
      'version_number', 'previous_status', 'new_status', 'audit_status',
      'applicable_rule_count', 'finding_count', 'file_size_bytes',
      'mime_type', 'failure_category', 'cleanup_reason', 'download_kind',
      'plan_hash', 'snapshot_schema_version', 'item_count'
    ]::text[] = '{}'::jsonb
  );

comment on constraint ck_audit_trail_metadata_allowlist on public.audit_trail_events is
  'Bounded metadata keys for document, audit, and immutable fix-plan approval events.';
