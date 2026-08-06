import { createHash, randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { readFile } from "node:fs/promises";
import { createServer } from "node:net";
import path from "node:path";
import {
  buildChildEnvironment,
  getSupabaseEnvironment,
  localSettings,
  resolveRuleCatalog,
} from "./dev-bootstrap.mjs";

const ids = Object.freeze({
  document: "95000000-0000-0000-0000-000000000001",
  sourceVersion: "95000000-0000-0000-0000-000000000002",
  resultVersion: "95000000-0000-0000-0000-000000000003",
  sourceAudit: "95000000-0000-0000-0000-000000000004",
  sourceSnapshot: "95000000-0000-0000-0000-000000000005",
  sourceFinding: "95000000-0000-0000-0000-000000000006",
  execution: "95000000-0000-0000-0000-000000000007",
  idempotency: "95000000-0000-0000-0000-000000000008",
  foreignOwner: "95000000-0000-0000-0000-000000000009",
  documentType: "10000000-0000-0000-0000-000000000002",
  profileVersion: "21000000-0000-0000-0000-000000000001",
});

const sourceHashA = "a".repeat(64);
const resultHash = "b".repeat(64);
const planHash = "c".repeat(64);
const ruleSnapshot = Object.freeze({
  rule_code: "PPKI-LAY-019",
  domain: "LAY",
  subdomain: "Paragraf",
  applies_to: "Semua",
  element: "Perataan paragraf",
  requirement: { expected: "justified" },
  validation_key: "body.justified",
  validation: { alignment: "both" },
  severity: "Error",
  fix_mode: "Auto",
  source_reference: { sourceSection: "synthetic" },
  layer: "profile",
  precedence: 0,
  ordinal: 1,
  snapshot_schema_version: 1,
});
const resolvedHash = createHash("sha256").update(JSON.stringify([ruleSnapshot])).digest("hex");
const assertions = [];
let apiProcess;

function report(name, passed) {
  const result = Boolean(passed);
  assertions.push(result);
  console.log(`${name}: ${result ? "PASS" : "FAIL"}`);
  return result;
}

function requireResult(name, passed) {
  if (!report(name, passed)) throw new Error("runtime assertion failed");
}

function isLoopback(hostname) {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "::1";
}

function run(command, args, { allowFailure = false, env = process.env, timeoutMs = 120000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: process.cwd(), env, shell: false, stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    const timeout = setTimeout(() => child.kill("SIGKILL"), timeoutMs);
    child.stdout.on("data", (chunk) => { if (stdout.length < 16384) stdout += chunk; });
    child.stderr.on("data", (chunk) => { if (stderr.length < 16384) stderr += chunk; });
    child.once("error", () => {
      clearTimeout(timeout);
      reject(new Error("local command could not start"));
    });
    child.once("close", (code) => {
      clearTimeout(timeout);
      if (code === 0 || allowFailure) resolve({ code, stdout, stderr });
      else reject(new Error("local command failed"));
    });
  });
}

async function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      server.close(() => resolve(address.port));
    });
  });
}

async function projectId() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const match = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m);
  if (!match) throw new Error("local project configuration is invalid");
  return match[1];
}

async function databaseContainer() {
  const expected = `supabase_db_${await projectId()}`;
  const result = await run("docker", ["ps", "--filter", `name=${expected}`, "--format", "{{.Names}}"]);
  const container = result.stdout.split(/\r?\n/).find((value) => value === expected);
  if (!container) throw new Error("local database unavailable");
  return container;
}

async function sql(container, statement) {
  const result = await run("docker", ["exec", container, "psql", "-X", "-q", "-A", "-t",
    "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement], { timeoutMs: 60000 });
  return result.stdout.trim();
}

async function localFetch(url, options = {}) {
  const parsed = new URL(url);
  if (!isLoopback(parsed.hostname) || parsed.protocol !== "http:") throw new Error("non-local request rejected");
  return fetch(url, options);
}

function headers(apiKey, token, json = false) {
  return {
    apikey: apiKey,
    ...(token ? { authorization: `Bearer ${token}` } : {}),
    ...(json ? { "content-type": "application/json" } : {}),
  };
}

async function responseJson(response) {
  const text = await response.text();
  if (!text) return null;
  try { return JSON.parse(text); } catch { return null; }
}

async function authenticate(environment) {
  const email = "reaudit-smoke@example.invalid";
  const password = `${randomUUID()}-Aa9!`;
  const adminHeaders = headers(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY, true);
  const list = await localFetch(`${environment.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, { headers: adminHeaders });
  if (!list.ok) throw new Error("local auth lookup failed");
  const users = (await list.json()).users ?? [];
  let user = users.find((value) => value.email === email);
  const response = await localFetch(`${environment.API_URL}/auth/v1/admin/users${user ? `/${user.id}` : ""}`, {
    method: user ? "PUT" : "POST",
    headers: adminHeaders,
    body: JSON.stringify({ email, password, email_confirm: true, user_metadata: { full_name: "Synthetic Reaudit User" } }),
  });
  const updated = await responseJson(response);
  if (!response.ok || !updated?.id) throw new Error("local auth fixture failed");
  user = updated;
  const signIn = await localFetch(`${environment.API_URL}/auth/v1/token?grant_type=password`, {
    method: "POST",
    headers: headers(environment.ANON_KEY, undefined, true),
    body: JSON.stringify({ email, password }),
  });
  const session = await responseJson(signIn);
  if (!signIn.ok || !session?.access_token) throw new Error("local sign-in failed");
  return { userId: user.id, token: session.access_token };
}

function setupSql(ownerId) {
  return `
insert into public.documents (id, owner_user_id, document_type_id, title, current_version_no)
values ('${ids.document}', '${ownerId}', '${ids.documentType}', 'Synthetic re-audit smoke', 2)
on conflict (id) do nothing;

insert into public.document_versions
  (id, document_id, version_no, storage_bucket, storage_key, original_filename, mime_type,
   size_bytes, sha256, created_by_user_id, parent_version_id) values
  ('${ids.sourceVersion}', '${ids.document}', 1, 'documents-original',
   'reaudit-smoke/source.docx', 'synthetic-source.docx',
   'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1,
   '${sourceHashA}', '${ownerId}', null),
  ('${ids.resultVersion}', '${ids.document}', 2, 'documents-versions',
   'reaudit-smoke/result.docx', 'synthetic-result.docx',
   'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1,
   '${resultHash}', '${ownerId}', '${ids.sourceVersion}')
on conflict (id) do nothing;

insert into public.audit_jobs
  (id, document_version_id, profile_version_id, requested_by_user_id, document_kind_snapshot,
   status, resolved_rule_set_hash, applicable_rule_count, total_rules, error_count,
   started_at, completed_at)
values ('${ids.sourceAudit}', '${ids.sourceVersion}', '${ids.profileVersion}', '${ownerId}',
  'Skripsi', 'Completed', '${resolvedHash}', 1, 1, 1, now(), now())
on conflict (id) do nothing;

insert into public.audit_rule_snapshots
  (id, audit_job_id, rule_id, rule_code, domain, subdomain, applies_to, element,
   requirement_json, validation_key, validation_json, severity, fix_mode,
   source_reference_json, layer, precedence, ordinal, snapshot_schema_version)
select '${ids.sourceSnapshot}', '${ids.sourceAudit}', rule.id, '${ruleSnapshot.rule_code}',
  '${ruleSnapshot.domain}', '${ruleSnapshot.subdomain}', '${ruleSnapshot.applies_to}',
  '${ruleSnapshot.element}', '${JSON.stringify(ruleSnapshot.requirement)}',
  '${ruleSnapshot.validation_key}', '${JSON.stringify(ruleSnapshot.validation)}',
  '${ruleSnapshot.severity}', '${ruleSnapshot.fix_mode}',
  '${JSON.stringify(ruleSnapshot.source_reference)}', '${ruleSnapshot.layer}',
  ${ruleSnapshot.precedence}, ${ruleSnapshot.ordinal}, ${ruleSnapshot.snapshot_schema_version}
from public.rules as rule where rule.rule_code = '${ruleSnapshot.rule_code}'
on conflict (id) do nothing;

insert into public.audit_findings
  (id, audit_job_id, rule_id, severity, rule_code_snapshot, fix_mode_snapshot,
   source_section_snapshot, message, actual_value, expected_value, location, status)
select '${ids.sourceFinding}', '${ids.sourceAudit}', rule.id, 'Error', '${ruleSnapshot.rule_code}',
  'Auto', 'synthetic', 'Synthetic source finding', '{"alignment":"left"}',
  '{"alignment":"both"}', '{"paragraphIndex":1}', 'Open'
from public.rules as rule where rule.rule_code = '${ruleSnapshot.rule_code}'
on conflict (id) do nothing;

insert into public.fix_execution_jobs
  (id, audit_job_id, source_document_version_id, requested_by_user_id, idempotency_key,
   plan_hash, planner_version, selected_finding_ids, approved_plan_snapshot, state,
   planned_operation_count)
values ('${ids.execution}', '${ids.sourceAudit}', '${ids.sourceVersion}', '${ownerId}',
  '${ids.idempotency}', '${planHash}', 'fix-plan-v1', '["${ids.sourceFinding}"]',
  '{"schemaVersion":1}', 'Queued', 1)
on conflict (id) do nothing;

update public.fix_execution_jobs set state = 'Processing', started_at = now(),
  lease_expires_at = now() + interval '10 minutes'
where id = '${ids.execution}' and state = 'Queued';

update public.fix_execution_jobs set state = 'Completed',
  result_document_version_id = '${ids.resultVersion}', result_sha256 = '${resultHash}',
  completed_operation_count = 1, lease_expires_at = null, completed_at = now()
where id = '${ids.execution}' and state = 'Processing';

do $fixture$
begin
  if not exists (select 1 from public.fix_execution_jobs where id = '${ids.execution}'
      and state = 'Completed' and requested_by_user_id = '${ownerId}')
    or not exists (select 1 from public.audit_rule_snapshots where id = '${ids.sourceSnapshot}')
    or not exists (select 1 from public.audit_findings where id = '${ids.sourceFinding}') then
    raise exception 'bounded re-audit fixture is invalid';
  end if;
end $fixture$;`;
}

async function startApi(environment) {
  const port = await freePort();
  const settings = localSettings({ API_PORT: String(port) });
  const catalog = await resolveRuleCatalog(process.cwd());
  const childEnvironment = buildChildEnvironment(process.env, environment, settings, catalog);
  apiProcess = spawn("dotnet", ["backend/services/Ppki.Api/bin/Debug/net10.0/Ppki.Api.dll"], {
    cwd: process.cwd(), env: childEnvironment, shell: false, stdio: ["ignore", "pipe", "pipe"],
  });
  apiProcess.stdout.resume();
  apiProcess.stderr.resume();
  for (let attempt = 0; attempt < 80; attempt += 1) {
    if (apiProcess.exitCode !== null) throw new Error("local API exited during startup");
    try {
      const response = await localFetch(`${settings.apiUrl}/health/live`);
      if (response.ok) return settings.apiUrl;
    } catch { /* retry bounded startup */ }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error("local API startup timed out");
}

async function stopApi() {
  if (!apiProcess || apiProcess.exitCode !== null) return;
  apiProcess.kill("SIGTERM");
  await Promise.race([
    new Promise((resolve) => apiProcess.once("close", resolve)),
    new Promise((resolve) => setTimeout(resolve, 3000)),
  ]);
  if (apiProcess.exitCode === null) apiProcess.kill("SIGKILL");
}

async function callReaudit(apiUrl, environment, token) {
  const response = await localFetch(`${apiUrl}/api/fix-executions/${ids.execution}/re-audit`, {
    method: "POST",
    headers: headers(environment.ANON_KEY, token),
  });
  return { status: response.status, json: await responseJson(response) };
}

async function assertSql(container, name, statement) {
  const result = await sql(container, statement);
  requireResult(name, result.split(/\r?\n/).filter(Boolean).at(-1) === "t");
}

async function main() {
  console.log("SUITE reaudit-local");
  let container;
  let environment;
  let auth;
  let apiUrl;
  let auditId;
  try {
    environment = await getSupabaseEnvironment(process.cwd());
    container = await databaseContainer();
    requireResult("local-only-infrastructure-ready", true);
    auth = await authenticate(environment);
    requireResult("synthetic-owner-authenticated", true);
    await sql(container, `update public.user_profiles set role='PPKIAdmin' where id='${auth.userId}';`);
    apiUrl = await startApi(environment);
    requireResult("local-api-ready", true);
    await sql(container, setupSql(auth.userId));
    requireResult("bounded-completed-fix-fixture-ready", true);

    const unauthenticated = await localFetch(`${apiUrl}/api/fix-executions/${ids.execution}/re-audit`, { method: "POST" });
    requireResult("unauthenticated-request-rejected", unauthenticated.status === 401);
    const malformed = await localFetch(`${apiUrl}/api/fix-executions/not-a-guid/re-audit`, {
      method: "POST", headers: headers(environment.ANON_KEY, auth.token),
    });
    requireResult("malformed-execution-id-rejected", malformed.status === 400);

    const concurrent = await Promise.all([
      callReaudit(apiUrl, environment, auth.token),
      callReaudit(apiUrl, environment, auth.token),
    ]);
    auditId = concurrent[0].json?.auditId;
    requireResult("parallel-requests-return-one-canonical-audit",
      concurrent.every((value) => [200, 202].includes(value.status))
      && auditId && concurrent.every((value) => value.json?.auditId === auditId));
    requireResult("new-or-bounded-replay-response-is-private",
      concurrent.every((value) => value.json
        && !Object.keys(value.json).some((key) => /path|filename|text|xml|secret|token|finding/i.test(key))));

    const replay = await callReaudit(apiUrl, environment, auth.token);
    requireResult("replay-returns-same-audit", replay.status === 200 && replay.json?.auditId === auditId
      && replay.json?.replayed === true);

    await assertSql(container, "one-reaudit-uses-result-and-exact-source-context", `
select count(*) = 1
  and bool_and(target.document_version_id = '${ids.resultVersion}')
  and bool_and(target.profile_version_id = source.profile_version_id)
  and bool_and(target.document_kind_snapshot = source.document_kind_snapshot)
  and bool_and(target.resolved_rule_set_hash = source.resolved_rule_set_hash)
  and bool_and(target.applicable_rule_count = source.applicable_rule_count)
from public.audit_jobs as target
join public.audit_jobs as source on source.id = target.source_audit_job_id
where target.source_fix_execution_id = '${ids.execution}';`);

    await assertSql(container, "snapshot-clone-is-exact-and-findings-are-not-copied", `
with target as (select id from public.audit_jobs where source_fix_execution_id = '${ids.execution}'),
mismatch as (
  (select rule_id, rule_code, domain, subdomain, applies_to, element, requirement_json,
          validation_key, validation_json, severity, fix_mode, source_reference_json,
          layer, precedence, ordinal, snapshot_schema_version
   from public.audit_rule_snapshots where audit_job_id = '${ids.sourceAudit}'
   except
   select rule_id, rule_code, domain, subdomain, applies_to, element, requirement_json,
          validation_key, validation_json, severity, fix_mode, source_reference_json,
          layer, precedence, ordinal, snapshot_schema_version
   from public.audit_rule_snapshots where audit_job_id = (select id from target))
  union all
  (select rule_id, rule_code, domain, subdomain, applies_to, element, requirement_json,
          validation_key, validation_json, severity, fix_mode, source_reference_json,
          layer, precedence, ordinal, snapshot_schema_version
   from public.audit_rule_snapshots where audit_job_id = (select id from target)
   except
   select rule_id, rule_code, domain, subdomain, applies_to, element, requirement_json,
          validation_key, validation_json, severity, fix_mode, source_reference_json,
          layer, precedence, ordinal, snapshot_schema_version
   from public.audit_rule_snapshots where audit_job_id = '${ids.sourceAudit}')
)
select (select count(*) from public.audit_rule_snapshots where audit_job_id = (select id from target)) = 1
  and not exists (select 1 from mismatch)
  and (select count(*) from public.audit_findings where audit_job_id = (select id from target)) = 0
  and (select count(*) from public.audit_findings where audit_job_id = '${ids.sourceAudit}') = 1;`);

    await assertSql(container, "owner-rls-visible-and-foreign-hidden", `begin;
set local role authenticated;
set local request.jwt.claim.sub = '${auth.userId}';
select count(*) = 1 from public.audit_jobs where id = '${auditId}';
rollback;
begin;
set local role authenticated;
set local request.jwt.claim.sub = '${ids.foreignOwner}';
select count(*) = 0 from public.audit_jobs where id = '${auditId}';
rollback;`);

    await assertSql(container, "browser-cannot-write-lineage", `begin;
set local role authenticated;
set local request.jwt.claim.sub = '${auth.userId}';
select not has_table_privilege('authenticated', 'public.audit_jobs', 'INSERT')
  and not has_table_privilege('authenticated', 'public.audit_jobs', 'UPDATE');
rollback;`);

    await assertSql(container, "database-trigger-rejects-lineage-mutation", `begin;
grant select, update on public.audit_jobs to service_role;
set local role service_role;
do $expected$
begin
  begin
    update public.audit_jobs set source_fix_execution_id = null where id = '${auditId}';
    raise exception 'expected lineage rejection';
  exception when sqlstate '55000' then null;
  end;
end $expected$;
reset role;
select source_fix_execution_id = '${ids.execution}' from public.audit_jobs where id = '${auditId}';
rollback;`);

    const status = await sql(container, `select status from public.audit_jobs where id = '${auditId}';`);
    if (status === "Queued") {
      await assertSql(container, "worker-claim-contract-and-lifecycle-update", `begin;
set local app.actor_service = 'worker';
update public.audit_jobs set status = 'Processing', started_at = now()
where id = '${auditId}' and status = 'Queued';
select status = 'Processing' and started_at is not null
from public.audit_jobs where id = '${auditId}';
rollback;`);
      await sql(container, `begin;
set local app.actor_service = 'worker';
update public.audit_jobs set status = 'Processing', started_at = now()
where id = '${auditId}' and status = 'Queued';
update public.audit_jobs set status = 'Failed', completed_at = now(), error_message = 'reaudit-smoke-terminalized'
where id = '${auditId}' and status = 'Processing';
commit;`);
    } else {
      requireResult("worker-claim-contract-and-lifecycle-update", ["Processing", "Completed", "Failed", "Cancelled"].includes(status));
    }
    await assertSql(container, "bounded-fixture-left-nonclaimable-and-source-unchanged", `
select target.status in ('Completed','Failed','Cancelled')
  and source.status = 'Completed'
  and source.resolved_rule_set_hash = '${resolvedHash}'
  and (select count(*) from public.audit_findings where audit_job_id = '${ids.sourceAudit}') = 1
from public.audit_jobs as target
join public.audit_jobs as source on source.id = '${ids.sourceAudit}'
where target.id = '${auditId}';`);
  } catch {
    report("reaudit-runtime-smoke-completed", false);
  } finally {
    await stopApi();
  }
  if (assertions.every(Boolean)) report("reaudit-runtime-smoke-completed", true);
  process.exitCode = assertions.every(Boolean) ? 0 : 1;
}

main();
