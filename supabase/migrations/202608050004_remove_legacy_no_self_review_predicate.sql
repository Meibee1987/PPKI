-- S4-T04 correction closure: the old non-owner helper is no longer a valid
-- authorization concept after the global PPKIAdmin-only migration replaced
-- every dependent review policy.
-- migration-hygiene: allow-destructive obsolete-no-self-review-helper
begin;

drop function if exists public.can_ppki_admin_review_finding(uuid);

commit;
