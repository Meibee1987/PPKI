import { randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { readFile } from "node:fs/promises";
import { createServer } from "node:net";
import path from "node:path";
import { buildChildEnvironment, getSupabaseEnvironment, localSettings, resolveRuleCatalog } from "./dev-bootstrap.mjs";

const ids = Object.freeze({
  document: "98500000-0000-0000-0000-000000000001",
  version: "98500000-0000-0000-0000-000000000002",
  audit: "98500000-0000-0000-0000-000000000003",
  snapshot: "98500000-0000-0000-0000-000000000004",
  documentType: "10000000-0000-0000-0000-000000000002",
  profileVersion: "21000000-0000-0000-0000-000000000001"
});
const findingCount = 2_037;
const assertions = [];
let apiProcess;
let apiDiagnostics = "";

function report(name, passed) {
  assertions.push(Boolean(passed));
  console.log(`${name}: ${passed ? "PASS" : "FAIL"}`);
  if (!passed) throw new Error("runtime assertion failed");
}
function run(command, args, { env = process.env, timeoutMs = 120_000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), env, shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = ""; let stderr = "";
    const timeout = setTimeout(() => child.kill("SIGKILL"), timeoutMs);
    child.stdout.on("data", chunk => { if (stdout.length < 65_536) stdout += chunk; });
    child.stderr.on("data", chunk => { if (stderr.length < 8_192) stderr += chunk; });
    child.once("error", () => { clearTimeout(timeout); reject(new Error("local command could not start")); });
    child.once("close", code => {
      clearTimeout(timeout);
      code === 0 ? resolve(stdout) : reject(new Error(stderr.split(/\r?\n/).find(line => /error|fatal/i.test(line))?.slice(0, 256) || "local command failed"));
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
  const output = await run("docker", ["ps", "--filter", `name=${expected}`, "--format", "{{.Names}}"]);
  if (!output.split(/\r?\n/).includes(expected)) throw new Error("local database unavailable");
  return expected;
}
async function sql(container, statement) {
  return (await run("docker", ["exec", container, "psql", "-X", "-q", "-A", "-t", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement], { timeoutMs: 60_000 })).trim();
}
function localFetch(url, options = {}) {
  const parsed = new URL(url);
  if (parsed.protocol !== "http:" || !["localhost", "127.0.0.1", "::1"].includes(parsed.hostname)) throw new Error("non-local request rejected");
  return fetch(url, options);
}
function headers(apiKey, token, json = false) {
  return { apikey: apiKey, ...(token ? { authorization: `Bearer ${token}` } : {}), ...(json ? { "content-type": "application/json" } : {}) };
}
async function responseBody(response) {
  const text = await response.text();
  try { return text ? JSON.parse(text) : null; } catch { return null; }
}
async function authenticate(environment, identity, claimedRole) {
  const email = `audit-pagination-${identity}@example.invalid`;
  const password = `${randomUUID()}-Aa9!`;
  const adminHeaders = headers(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY, true);
  const listed = await localFetch(`${environment.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, { headers: adminHeaders });
  const existing = ((await listed.json()).users ?? []).find(value => value.email === email);
  const saved = await localFetch(`${environment.API_URL}/auth/v1/admin/users${existing ? `/${existing.id}` : ""}`, {
    method: existing ? "PUT" : "POST", headers: adminHeaders,
    body: JSON.stringify({ email, password, email_confirm: true, user_metadata: { full_name: "Synthetic Pagination User", role: claimedRole } })
  });
  const user = await responseBody(saved);
  if (!saved.ok || !user?.id) throw new Error("local auth fixture failed");
  const signed = await localFetch(`${environment.API_URL}/auth/v1/token?grant_type=password`, {
    method: "POST", headers: headers(environment.ANON_KEY, undefined, true), body: JSON.stringify({ email, password })
  });
  const session = await responseBody(signed);
  if (!signed.ok || !session?.access_token) throw new Error("local sign-in failed");
  return { id: user.id, token: session.access_token };
}
async function startApi(environment) {
  const settings = localSettings({ API_PORT: String(await freePort()) });
  const catalog = await resolveRuleCatalog(process.cwd());
  apiProcess = spawn("dotnet", ["backend/services/Ppki.Api/bin/Release/net10.0/Ppki.Api.dll"], {
    cwd: process.cwd(), env: buildChildEnvironment(process.env, environment, settings, catalog), shell: false, stdio: ["ignore", "pipe", "pipe"]
  });
  const capture = chunk => { apiDiagnostics = `${apiDiagnostics}${chunk}`.slice(-32_768); };
  apiProcess.stdout.on("data", capture); apiProcess.stderr.on("data", capture);
  for (let attempt = 0; attempt < 80; attempt += 1) {
    if (apiProcess.exitCode !== null) throw new Error("local API exited during startup");
    try { if ((await localFetch(`${settings.apiUrl}/health/live`)).ok) return settings.apiUrl; } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error("local API startup timed out");
}
async function stopApi() {
  if (!apiProcess || apiProcess.exitCode !== null) return;
  apiProcess.kill("SIGTERM");
  await Promise.race([new Promise(resolve => apiProcess.once("close", resolve)), new Promise(resolve => setTimeout(resolve, 3_000))]);
  if (apiProcess.exitCode === null) apiProcess.kill("SIGKILL");
}
function fixtureSql(ownerId) {
  return `
insert into public.documents(id,owner_user_id,document_type_id,title,current_version_no)
values('${ids.document}','${ownerId}','${ids.documentType}','Synthetic pagination smoke',1) on conflict(id) do nothing;
insert into public.document_versions(id,document_id,version_no,storage_bucket,storage_key,original_filename,mime_type,size_bytes,sha256,created_by_user_id,parent_version_id)
values('${ids.version}','${ids.document}',1,'documents-original','pagination/${ids.document}/source.docx','synthetic.docx','application/vnd.openxmlformats-officedocument.wordprocessingml.document',1,'${"a".repeat(64)}','${ownerId}',null) on conflict(id) do nothing;
insert into public.audit_jobs(id,document_version_id,profile_version_id,requested_by_user_id,document_kind_snapshot,status,resolved_rule_set_hash,applicable_rule_count,total_rules,error_count,warning_count,started_at,completed_at)
values('${ids.audit}','${ids.version}','${ids.profileVersion}','${ownerId}','Skripsi','Completed','${"b".repeat(64)}',1,1,1500,537,now(),now()) on conflict(id) do nothing;
insert into public.audit_rule_snapshots(id,audit_job_id,rule_id,rule_code,domain,subdomain,applies_to,element,requirement_json,validation_key,validation_json,severity,fix_mode,source_reference_json,layer,precedence,ordinal,snapshot_schema_version)
select '${ids.snapshot}','${ids.audit}',rule.id,rule.rule_code,'Layout',null,'Skripsi','Paragraph','{}'::jsonb,'pagination.synthetic','{}'::jsonb,'Error','Manual','{}'::jsonb,'Document',0,1,1
from public.rules rule where rule.rule_code='PPKI-LAY-019' on conflict(id) do nothing;
insert into public.audit_findings(id,audit_job_id,rule_id,severity,rule_code_snapshot,fix_mode_snapshot,source_section_snapshot,message,actual_value,expected_value,location,status)
select md5('audit-pagination-' || value)::uuid,'${ids.audit}',rule.id,
       case when value <= 1500 then 'Error' else 'Warning' end,
       rule.rule_code,case when value % 2 = 0 then 'Manual' else 'Report' end,'synthetic','pagination-finding','{}'::jsonb,'{}'::jsonb,
       jsonb_build_object('CompactLocation','body/paragraph/' || value,'BodyElementIndex',value,'SectionIndex',value / 500,'ParagraphIndex',value % 500),'Open'
from generate_series(1,${findingCount}) value cross join public.rules rule
where rule.rule_code='PPKI-LAY-019' on conflict(id) do nothing;`;
}
function historySql() {
  return `select concat_ws(',',
    (select md5(string_agg(row_to_json(f)::text,'' order by f.id)) from public.audit_findings f where f.audit_job_id='${ids.audit}'),
    (select md5(string_agg(row_to_json(s)::text,'' order by s.id)) from public.audit_rule_snapshots s where s.audit_job_id='${ids.audit}'),
    (select count(*) from public.audit_findings where audit_job_id='${ids.audit}'));`;
}
async function findings(apiUrl, environment, user, query = "") {
  const response = await localFetch(`${apiUrl}/api/audits/${ids.audit}/findings${query ? `?${query}` : ""}`, { headers: headers(environment.ANON_KEY, user?.token) });
  return { status: response.status, body: await responseBody(response) };
}

async function main() {
  console.log("SUITE audit-read-pagination-local");
  try {
    const environment = await getSupabaseEnvironment(process.cwd());
    const container = await databaseContainer();
    const users = {
      adminA: await authenticate(environment, "admin-a", "Student"),
      adminB: await authenticate(environment, "admin-b", "Reviewer"),
      student: await authenticate(environment, "student", "PPKIAdmin")
    };
    await sql(container, `update public.user_profiles set role=case id when '${users.adminA.id}' then 'PPKIAdmin' when '${users.adminB.id}' then 'PPKIAdmin' when '${users.student.id}' then 'Student' else role end where id in ('${users.adminA.id}','${users.adminB.id}','${users.student.id}');`);
    await sql(container, fixtureSql(users.adminA.id));
    report("reusable-fixture-has-more-than-two-thousand-findings", await sql(container, `select count(*) from public.audit_findings where audit_job_id='${ids.audit}';`) === String(findingCount));
    const apiUrl = await startApi(environment);
    const history = await sql(container, historySql());

    const defaultPage = await findings(apiUrl, environment, users.adminA);
    const first = await findings(apiUrl, environment, users.adminA, "page=1&pageSize=100");
    const middle = await findings(apiUrl, environment, users.adminA, "page=11&pageSize=100");
    const last = await findings(apiUrl, environment, users.adminA, "page=21&pageSize=100");
    const empty = await findings(apiUrl, environment, users.adminA, "page=22&pageSize=100");
    const repeated = await findings(apiUrl, environment, users.adminA, "page=11&pageSize=100");
    report("default-first-page-is-bounded", defaultPage.status === 200 && defaultPage.body?.page === 1 && defaultPage.body?.pageSize === 25 && defaultPage.body?.items?.length === 25);
    report("first-middle-last-and-empty-pages-are-bounded", first.body?.items?.length === 100 && middle.body?.items?.length === 100 && last.body?.items?.length === 37 && empty.body?.items?.length === 0);
    report("filtered-total-count-is-full-result", [first,middle,last,empty].every(value => value.status === 200 && value.body?.totalCount === findingCount));
    report("repeated-page-order-is-deterministic", JSON.stringify(middle.body?.items?.map(value => value.id)) === JSON.stringify(repeated.body?.items?.map(value => value.id)));

    const allPages = [];
    for (let page = 1; page <= 21; page += 1) {
      const value = await findings(apiUrl, environment, users.adminA, `page=${page}&pageSize=100`);
      if (value.status !== 200 || value.body?.items?.length > 100) throw new Error("bounded page traversal failed");
      allPages.push(...value.body.items.map(item => item.id));
    }
    report("all-pages-have-no-duplicate-or-missing-findings", allPages.length === findingCount && new Set(allPages).size === findingCount);

    const severity = await findings(apiUrl, environment, users.adminA, "severity=Warning&page=1&pageSize=100");
    const exact = await findings(apiUrl, environment, users.adminA, "domain=Layout&ruleCode=PPKI-LAY-019&validationKey=pagination.synthetic&page=6&pageSize=100");
    report("severity-and-snapshot-filters-run-before-bounded-page", severity.status === 200 && severity.body?.totalCount === 537 && severity.body?.items?.length === 100 && severity.body.items.every(item => item.severity === "Warning") && exact.status === 200 && exact.body?.totalCount === findingCount && exact.body?.items?.length === 100);

    const adminB = await findings(apiUrl, environment, users.adminB, "page=11&pageSize=100");
    const denied = await findings(apiUrl, environment, users.student, "page=1&pageSize=100");
    const malformed = await findings(apiUrl, environment, users.adminA, "page=101&pageSize=100");
    report("shared-admin-a-b-see-the-same-page", adminB.status === 200 && JSON.stringify(adminB.body) === JSON.stringify(middle.body));
    report("database-authoritative-non-admin-is-denied", denied.status === 403);
    report("out-of-window-page-is-rejected", malformed.status === 400);
    report("read-only-pagination-preserves-historical-resources", history === await sql(container, historySql()));
    console.log("audit-read-pagination-runtime-smoke-completed: PASS");
  } catch (error) {
    console.log(`BLOCKER: ${error instanceof Error ? error.message : "local runtime unavailable"}`);
    if (apiDiagnostics) console.log(`API-DIAGNOSTIC: ${apiDiagnostics.split(/\r?\n/).filter(line => /error|exception|failed|npgsql|postgres|sqlstate/i.test(line)).slice(-10).join(" | ").slice(0, 2048)}`);
    console.log("audit-read-pagination-runtime-smoke-completed: FAIL");
    process.exitCode = 1;
  } finally {
    await stopApi();
  }
}

main();
