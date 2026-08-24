-- Review readiness is a versioned PPKI Smart Formatter product policy. It is
-- not represented as an official IPB/PPKI classification.

alter table public.rules
  add column review_blocking_policy text not null default 'PendingApproval',
  add column readiness_policy_version text not null default 'ppki-ipb-2019-review-readiness-v1',
  add constraint ck_rules_review_blocking_policy
    check (review_blocking_policy in ('Blocking', 'NonBlocking', 'PendingApproval')),
  add constraint ck_rules_readiness_policy_version
    check (btrim(readiness_policy_version) <> '');

alter table public.audit_rule_snapshots
  add column review_blocking_policy text,
  add column readiness_policy_version text,
  add constraint ck_audit_rule_snapshots_review_policy check (
    (snapshot_schema_version = 1
      and review_blocking_policy is null
      and readiness_policy_version is null)
    or
    (snapshot_schema_version >= 2
      and review_blocking_policy in ('Blocking', 'NonBlocking')
      and btrim(readiness_policy_version) <> '')
  );

comment on column public.rules.review_blocking_policy is
  'Mutable PPKI Smart Formatter review-readiness product policy; not official IPB policy.';
comment on column public.rules.readiness_policy_version is
  'Version of the mutable PPKI Smart Formatter review-readiness product policy.';
comment on column public.audit_rule_snapshots.review_blocking_policy is
  'Immutable review policy copied when a schema-v2 audit snapshot is created; null means legacy Unknown.';
comment on column public.audit_rule_snapshots.readiness_policy_version is
  'Immutable review-readiness policy version; null for legacy schema-v1 snapshots.';
