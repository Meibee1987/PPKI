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
  document: "97000000-0000-0000-0000-000000000001",
  sourceVersion: "97000000-0000-0000-0000-000000000002",
  resultVersion: "97000000-0000-0000-0000-000000000003",
  sourceAudit: "97000000-0000-0000-0000-000000000004",
  resultAudit: "97000000-0000-0000-0000-000000000005",
  execution: "97000000-0000-0000-0000-000000000006",
  idempotency: "97000000-0000-0000-0000-000000000007",
  sourceSnapshot: "97000000-0000-0000-0000-000000000008",
  resultSnapshot: "97000000-0000-0000-0000-000000000009",
  documentType: "10000000-0000-0000-0000-000000000002",
  profileVersion: "21000000-0000-0000-0000-000000000001",
});
const rule = Object.freeze({
  rule_code: "PPKI-LAY-019", domain: "LAY", subdomain: "Paragraf",
  applies_to: "Semua", element: "Perataan paragraf",
  requirement: { expected: "justified" }, validation_key: "body.justified",
  validation: { alignment: "both" }, severity: "Error", fix_mode: "Auto",
  source_reference: { sourceSection: "synthetic" }, layer: "profile",
  precedence: 0, ordinal: 1, snapshot_schema_version: 1,
});
const resolvedHash = createHash("sha256").update(JSON.stringify([rule])).digest("hex");
const assertions = [];
let apiProcess;

function report(name, passed) {
  assertions.push(Boolean(passed));
  console.log(`${name}: ${passed ? "PASS" : "FAIL"}`);
  if (!passed) throw new Error("runtime assertion failed");
}

function run(command, args, { env = process.env, timeoutMs = 120000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), env, shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    const timeout = setTimeout(() => child.kill("SIGKILL"), timeoutMs);
    child.stdout.on("data", (chunk) => { if (stdout.length < 32768) stdout += chunk; });
    child.stderr.on("data", (chunk) => { if (stderr.length < 4096) stderr += chunk; });
    child.once("error", () => { clearTimeout(timeout); reject(new Error("local command could not start")); });
    child.once("close", (code) => {
      clearTimeout(timeout);
      if (code === 0) resolve(stdout);
      else {
        const diagnostic = stderr.split(/\r?\n/)
          .find((line) => /(?:error|fatal):/i.test(line))
          ?.replace(/'[^']*'/gu, "'[redacted]'")
          .replace(/[\u0000-\u001f\u007f]/gu, " ")
          .slice(0, 256);
        reject(new Error(diagnostic || "local command failed"));
      }
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

async function databaseContainer() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const project = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m)?.[1];
  if (!project) throw new Error("local project configuration is invalid");
  const expected = `supabase_db_${project}`;
  let output;
  try {
    output = await run("docker", ["ps", "--filter", `name=${expected}`, "--format", "{{.Names}}"]) ;
  } catch {
    throw new Error("local Docker daemon is unavailable");
  }
  if (!output.split(/\r?\n/).includes(expected)) throw new Error("local database unavailable");
  return expected;
}

async function sql(container, statement) {
  return (await run("docker", ["exec", container, "psql", "-X", "-q", "-A", "-t",
    "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement],
  { timeoutMs: 60000 })).trim();
}

function localFetch(url, options = {}) {
  const parsed = new URL(url);
  if (parsed.protocol !== "http:" || !["localhost", "127.0.0.1", "::1"].includes(parsed.hostname))
    throw new Error("non-local request rejected");
  return fetch(url, options);
}

function headers(apiKey, token, json = false) {
  return { apikey: apiKey, ...(token ? { authorization: `Bearer ${token}` } : {}),
    ...(json ? { "content-type": "application/json" } : {}) };
}

async function json(response) {
  const value = await response.text();
  try { return value ? JSON.parse(value) : null; } catch { return null; }
}

async function authenticate(environment, identity) {
  const email = `audit-comparison-${identity}@example.invalid`;
  const password = `${randomUUID()}-Aa9!`;
  const adminHeaders = headers(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY, true);
  const list = await localFetch(`${environment.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, { headers: adminHeaders });
  if (!list.ok) throw new Error("local auth lookup failed");
  const existing = ((await list.json()).users ?? []).find((value) => value.email === email);
  const saved = await localFetch(`${environment.API_URL}/auth/v1/admin/users${existing ? `/${existing.id}` : ""}`, {
    method: existing ? "PUT" : "POST", headers: adminHeaders,
    body: JSON.stringify({ email, password, email_confirm: true, user_metadata: { full_name: "Synthetic Comparison User" } }),
  });
  const user = await json(saved);
  if (!saved.ok || !user?.id) throw new Error("local auth fixture failed");
  const signIn = await localFetch(`${environment.API_URL}/auth/v1/token?grant_type=password`, {
    method: "POST", headers: headers(environment.ANON_KEY, undefined, true),
    body: JSON.stringify({ email, password }),
  });
  const session = await json(signIn);
  if (!signIn.ok || !session?.access_token) throw new Error("local sign-in failed");
  return { id: user.id, token: session.access_token };
}

function actual(value) {
  return JSON.stringify({ Property: "alignment", NormalizedValue: value, Unit: null,
    ResolutionState: "Resolved", SourceKind: "DirectFormatting", Inherited: false, RawValue: "not-public" });
}
function expected() {
  return JSON.stringify({ Property: "alignment", AcceptedValues: ["both"], ValidationKey: "body.justified" });
}
function location(index) {
  return JSON.stringify({ CompactLocation: `body/paragraph/${index}`, BodyElementIndex: index, ParagraphIndex: index });
}

function findingsSql(auditId, before) {
  const values = before
    ? [[5, 1, "left"], [1, 2, "left"], [4, 3, "left"], [2, 5, "left"], [3, 5, "left"]]
    : [[9, 1, "left"], [7, 2, "both"], [8, 4, "both"], [6, 5, "left"]];
  return values.map(([suffix, index, value]) => `
select '97000000-0000-0000-0001-${String(suffix).padStart(12, "0")}'::uuid,
  '${auditId}'::uuid, rule.id, 'Error', '${rule.rule_code}', 'Auto', 'synthetic',
  'paragraph-alignment-invalid', '${actual(value)}'::jsonb, '${expected()}'::jsonb,
  '${location(index)}'::jsonb, 'Open'
from public.rules as rule where rule.rule_code = '${rule.rule_code}'`).join(" union all ");
}

function fixtureSql(ownerId) {
  const snapshotColumns = `rule_id, rule_code, domain, subdomain, applies_to, element,
    requirement_json, validation_key, validation_json, severity, fix_mode,
    source_reference_json, layer, precedence, ordinal, snapshot_schema_version`;
  const snapshotSelect = `rule.id, '${rule.rule_code}', '${rule.domain}', '${rule.subdomain}',
    '${rule.applies_to}', '${rule.element}', '${JSON.stringify(rule.requirement)}'::jsonb,
    '${rule.validation_key}', '${JSON.stringify(rule.validation)}'::jsonb, '${rule.severity}',
    '${rule.fix_mode}', '${JSON.stringify(rule.source_reference)}'::jsonb, '${rule.layer}',
    ${rule.precedence}, ${rule.ordinal}, ${rule.snapshot_schema_version}
    from public.rules as rule where rule.rule_code = '${rule.rule_code}'`;
  return `
insert into public.documents (id, owner_user_id, document_type_id, title, current_version_no)
values ('${ids.document}', '${ownerId}', '${ids.documentType}', 'Synthetic comparison smoke', 2)
on conflict (id) do nothing;
insert into public.document_versions
  (id, document_id, version_no, storage_bucket, storage_key, original_filename, mime_type,
   size_bytes, sha256, created_by_user_id, parent_version_id) values
  ('${ids.sourceVersion}', '${ids.document}', 1, 'documents-original', 'comparison/source.docx',
   'synthetic-source.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
   1, '${"a".repeat(64)}', '${ownerId}', null),
  ('${ids.resultVersion}', '${ids.document}', 2, 'documents-versions', 'comparison/result.docx',
   'synthetic-result.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
   1, '${"b".repeat(64)}', '${ownerId}', '${ids.sourceVersion}') on conflict (id) do nothing;
insert into public.audit_jobs
  (id, document_version_id, profile_version_id, requested_by_user_id, document_kind_snapshot,
   status, resolved_rule_set_hash, applicable_rule_count, total_rules, error_count, started_at, completed_at)
values ('${ids.sourceAudit}', '${ids.sourceVersion}', '${ids.profileVersion}', '${ownerId}',
  'Skripsi', 'Completed', '${resolvedHash}', 1, 1, 5, now(), now()) on conflict (id) do nothing;
insert into public.audit_rule_snapshots (id, audit_job_id, ${snapshotColumns})
select '${ids.sourceSnapshot}', '${ids.sourceAudit}', ${snapshotSelect} on conflict (id) do nothing;
insert into public.audit_findings
  (id, audit_job_id, rule_id, severity, rule_code_snapshot, fix_mode_snapshot,
   source_section_snapshot, message, actual_value, expected_value, location, status)
${findingsSql(ids.sourceAudit, true)} on conflict (id) do nothing;
insert into public.fix_execution_jobs
  (id, audit_job_id, source_document_version_id, requested_by_user_id, idempotency_key,
   plan_hash, planner_version, selected_finding_ids, approved_plan_snapshot, state, planned_operation_count)
values ('${ids.execution}', '${ids.sourceAudit}', '${ids.sourceVersion}', '${ownerId}', '${ids.idempotency}',
  '${"c".repeat(64)}', 'fix-plan-v1', '["97000000-0000-0000-0001-000000000005"]',
  '{"schemaVersion":1}', 'Queued', 1)
on conflict (id) do nothing;
update public.fix_execution_jobs set state = 'Processing', started_at = now(),
  lease_expires_at = now() + interval '10 minutes' where id = '${ids.execution}' and state = 'Queued';
update public.fix_execution_jobs set state = 'Completed', result_document_version_id = '${ids.resultVersion}',
  result_sha256 = '${"b".repeat(64)}', completed_operation_count = 1, lease_expires_at = null,
  completed_at = now() where id = '${ids.execution}' and state = 'Processing';
begin;
insert into public.audit_jobs
  (id, document_version_id, profile_version_id, requested_by_user_id, document_kind_snapshot,
   status, resolved_rule_set_hash, applicable_rule_count, source_audit_job_id, source_fix_execution_id)
values ('${ids.resultAudit}', '${ids.resultVersion}', '${ids.profileVersion}', '${ownerId}',
  'Skripsi', 'Queued', '${resolvedHash}', 1, '${ids.sourceAudit}', '${ids.execution}') on conflict (id) do nothing;
insert into public.audit_rule_snapshots (id, audit_job_id, ${snapshotColumns})
select '${ids.resultSnapshot}', '${ids.resultAudit}', ${snapshotSelect} on conflict (id) do nothing;
commit;
insert into public.audit_findings
  (id, audit_job_id, rule_id, severity, rule_code_snapshot, fix_mode_snapshot,
   source_section_snapshot, message, actual_value, expected_value, location, status)
${findingsSql(ids.resultAudit, false)} on conflict (id) do nothing;
update public.audit_jobs set status = 'Completed', total_rules = 1, error_count = 4,
  started_at = coalesce(started_at, now()), completed_at = now()
where id = '${ids.resultAudit}' and status = 'Queued';
do $fixture$ begin
  if (select count(*) from public.audit_findings where audit_job_id = '${ids.sourceAudit}') <> 5
    or (select count(*) from public.audit_findings where audit_job_id = '${ids.resultAudit}') <> 4
    or not exists (select 1 from public.audit_jobs where id = '${ids.resultAudit}' and status = 'Completed')
  then raise exception 'bounded comparison fixture invalid'; end if;
end $fixture$;`;
}

async function startApi(environment) {
  const settings = localSettings({ API_PORT: String(await freePort()) });
  const catalog = await resolveRuleCatalog(process.cwd());
  const childEnvironment = buildChildEnvironment(process.env, environment, settings, catalog);
  apiProcess = spawn("dotnet", ["backend/services/Ppki.Api/bin/Release/net10.0/Ppki.Api.dll"],
    { cwd: process.cwd(), env: childEnvironment, shell: false, stdio: ["ignore", "pipe", "pipe"] });
  apiProcess.stdout.resume(); apiProcess.stderr.resume();
  for (let attempt = 0; attempt < 80; attempt += 1) {
    if (apiProcess.exitCode !== null) throw new Error("local API exited during startup");
    try { if ((await localFetch(`${settings.apiUrl}/health/live`)).ok) return settings.apiUrl; } catch { /* bounded retry */ }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error("local API startup timed out");
}

async function stopApi() {
  if (!apiProcess || apiProcess.exitCode !== null) return;
  apiProcess.kill("SIGTERM");
  await Promise.race([new Promise((resolve) => apiProcess.once("close", resolve)),
    new Promise((resolve) => setTimeout(resolve, 3000))]);
  if (apiProcess.exitCode === null) apiProcess.kill("SIGKILL");
}

async function comparison(apiUrl, environment, token, suffix = "") {
  const response = await localFetch(`${apiUrl}/api/fix-executions/${ids.execution}/comparison${suffix}`,
    { headers: headers(environment.ANON_KEY, token) });
  return { status: response.status, body: await json(response) };
}

function semantic(response) {
  return response.items.map((item) => [item.status, item.ruleCode, item.location?.compactLocation,
    item.before?.actual?.normalizedValue, item.after?.actual?.normalizedValue]);
}

function forbiddenResponseField(value) {
  if (!value || typeof value !== "object") return false;
  return Object.entries(value).some(([key, child]) =>
    /raw|json|fingerprint|semanticKey|text|filename|storage|path|url|xml|secret|token/i.test(key)
    || forbiddenResponseField(child));
}

function databaseStateSql() {
  return `select concat_ws(',',
    (select count(*) from public.documents),
    (select count(*) from public.document_versions),
    (select count(*) from public.audit_jobs),
    (select count(*) from public.audit_rule_snapshots),
    (select count(*) from public.audit_findings),
    (select count(*) from public.fix_execution_jobs),
    (select count(*) from public.audit_trail_events),
    (select coalesce(md5(string_agg(row_to_json(a)::text, '' order by a.id)), 'none')
      from public.audit_jobs a where a.id in ('${ids.sourceAudit}', '${ids.resultAudit}')),
    (select coalesce(md5(string_agg(row_to_json(s)::text, '' order by s.id)), 'none')
      from public.audit_rule_snapshots s
      where s.audit_job_id in ('${ids.sourceAudit}', '${ids.resultAudit}')),
    (select coalesce(md5(string_agg(row_to_json(f)::text, '' order by f.id)), 'none')
      from public.audit_findings f
      where f.audit_job_id in ('${ids.sourceAudit}', '${ids.resultAudit}')),
    (select coalesce(md5(string_agg(row_to_json(e)::text, '' order by e.id)), 'none')
      from public.fix_execution_jobs e where e.id = '${ids.execution}'),
    (select coalesce(md5(string_agg(row_to_json(t)::text, '' order by t.id)), 'none')
      from public.audit_trail_events t
      where t.resource_id in ('${ids.sourceAudit}', '${ids.resultAudit}', '${ids.execution}')));`;
}

async function setResultHashForMismatch(container, hash) {
  await sql(container, `begin;
set local session_replication_role = replica;
update public.audit_jobs set resolved_rule_set_hash = '${hash}' where id = '${ids.resultAudit}';
commit;`);
}

async function main() {
  console.log("SUITE audit-comparison-local");
  let completed = false;
  try {
    const environment = await getSupabaseEnvironment(process.cwd());
    const container = await databaseContainer();
    report("local-only-infrastructure-ready", true);
    const owner = await authenticate(environment, "owner");
    const foreign = await authenticate(environment, "foreign");
    const apiUrl = await startApi(environment);
    await sql(container, fixtureSql(owner.id));
    report("bounded-historical-fixture-ready", true);
    const baseline = await sql(container, databaseStateSql());

    const ownerResult = await comparison(apiUrl, environment, owner.token);
    report("owner-can-read-comparison", ownerResult.status === 200 && ownerResult.body?.comparisonState === "Ready");
    const counts = ownerResult.body?.summary;
    report("summary-and-duplicate-pairing-are-correct", counts?.sourceFindingCount === 5
      && counts?.resultFindingCount === 4 && counts?.stillDetectedCount === 2
      && counts?.changedCount === 1 && counts?.noLongerDetectedCount === 2
      && counts?.newlyDetectedCount === 1);
    const runtimeStatuses = (ownerResult.body?.items ?? []).reduce((countsByStatus, item) => {
      countsByStatus[item.status] = (countsByStatus[item.status] ?? 0) + 1;
      return countsByStatus;
    }, {});
    report("all-four-runtime-classifications-and-duplicates-are-correct",
      runtimeStatuses.StillDetected === 2
      && runtimeStatuses.Changed === 1
      && runtimeStatuses.NoLongerDetected === 2
      && runtimeStatuses.NewlyDetected === 1);
    report("response-excludes-raw-and-internal-fields", !forbiddenResponseField(ownerResult.body));
    const replay = await comparison(apiUrl, environment, owner.token);
    report("replay-and-database-row-order-are-semantic-stable",
      replay.status === 200 && JSON.stringify(semantic(replay.body)) === JSON.stringify(semantic(ownerResult.body)));
    const page = await comparison(apiUrl, environment, owner.token, "?page=1&pageSize=1");
    report("pagination-keeps-global-summary", page.status === 200 && page.body?.items?.length === 1
      && JSON.stringify(page.body?.summary) === JSON.stringify(ownerResult.body?.summary));
    const changed = await comparison(apiUrl, environment, owner.token, "?status=Changed&page=1&pageSize=100");
    report("filter-does-not-change-pairing-or-global-summary", changed.status === 200
      && changed.body?.totalCount === 1 && changed.body?.items?.[0]?.status === "Changed"
      && JSON.stringify(changed.body?.summary) === JSON.stringify(ownerResult.body?.summary));
    const foreignResult = await comparison(apiUrl, environment, foreign.token);
    report("foreign-execution-is-safe-not-found", foreignResult.status === 404);
    const unknown = await localFetch(`${apiUrl}/api/fix-executions/97000000-0000-0000-0000-999999999999/comparison`,
      { headers: headers(environment.ANON_KEY, owner.token) });
    report("unknown-and-foreign-have-the-same-safe-status", unknown.status === foreignResult.status);
    const unauthenticated = await localFetch(`${apiUrl}/api/fix-executions/${ids.execution}/comparison`);
    report("unauthenticated-request-is-rejected", unauthenticated.status === 401);

    await setResultHashForMismatch(container, "d".repeat(64));
    try {
      const mismatchBaseline = await sql(container, databaseStateSql());
      const mismatch = await comparison(apiUrl, environment, owner.token);
      const mismatchAfter = await sql(container, databaseStateSql());
      report("historical-context-mismatch-is-safe-and-read-only", mismatch.status === 409
        && mismatch.body?.code === "audit-comparison-historical-context-mismatch"
        && mismatchBaseline === mismatchAfter && !forbiddenResponseField(mismatch.body));
    } finally {
      await setResultHashForMismatch(container, resolvedHash);
    }

    const after = await sql(container, databaseStateSql());
    report("comparison-does-not-mutate-or-create-database-rows", baseline === after);
    const browserWrite = await sql(container, `select not has_table_privilege('authenticated', 'public.audit_findings', 'UPDATE')
      and not has_table_privilege('authenticated', 'public.audit_jobs', 'UPDATE')
      and not has_table_privilege('authenticated', 'public.fix_execution_jobs', 'UPDATE');`);
    report("browser-has-no-direct-write-capability", browserWrite.split(/\r?\n/).at(-1) === "t");
    completed = true;
  } catch (error) {
    console.log(`BLOCKER: ${error instanceof Error ? error.message : "local runtime unavailable"}`);
    console.log("audit-comparison-runtime-smoke-completed: FAIL");
    process.exitCode = 1;
  } finally {
    await stopApi();
  }
  if (completed && assertions.length > 0 && assertions.every(Boolean))
    console.log("audit-comparison-runtime-smoke-completed: PASS");
}

main();
