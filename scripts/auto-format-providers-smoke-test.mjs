import { randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { buildChildEnvironment, getSupabaseEnvironment, localSettings, resolveRuleCatalog } from "./dev-bootstrap.mjs";

const TITLE = "S5-T02 processor runtime provider coverage";
const IDEMPOTENCY_KEY = "98600000-0000-0000-0000-000000000009";
const DOCX_MIME = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
const FIXTURE = path.join(process.cwd(), "backend", "tests", "fixtures", "docx", "generated", "auto-format-provider-mixed.docx");
const SUPPORTED = new Set(["body.font-times-new-roman-12", "body.line-spacing-single", "body.first-line-indent-1cm",
  "abstract.skripsi-single-spacing-zero-paragraph-spacing", "abstract-summary-single-spacing-zero-paragraph-spacing",
  "heading.chapter-centered", "body.justified"]);
let apiProcess; let workerProcess; let diagnostics = "";

function report(name, passed) {
  console.log(`${name}: ${passed ? "PASS" : "FAIL"}`);
  if (!passed) throw new Error(`runtime assertion failed: ${name}`);
}
function run(command, args, { env = process.env, timeoutMs = 180_000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), env, shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = ""; let stderr = "";
    const timeout = setTimeout(() => child.kill("SIGKILL"), timeoutMs);
    child.stdout.on("data", chunk => { if (stdout.length < 131_072) stdout += chunk; });
    child.stderr.on("data", chunk => { if (stderr.length < 16_384) stderr += chunk; });
    child.once("error", () => { clearTimeout(timeout); reject(new Error("local command could not start")); });
    child.once("close", code => {
      clearTimeout(timeout);
      code === 0 ? resolve(stdout.trim()) : reject(new Error(stderr.split(/\r?\n/u).find(line => /error|fatal/i.test(line))?.slice(0, 300) || "local command failed"));
    });
  });
}
async function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer(); server.once("error", reject);
    server.listen(0, "127.0.0.1", () => { const address = server.address(); server.close(() => resolve(address.port)); });
  });
}
async function databaseContainer() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const project = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m)?.[1];
  if (!project) throw new Error("local project configuration is invalid");
  const expected = `supabase_db_${project}`;
  const output = await run("docker", ["ps", "--filter", `name=${expected}`, "--format", "{{.Names}}"]);
  if (!output.split(/\r?\n/u).includes(expected)) throw new Error("local database unavailable; run npm run dev:infra");
  return expected;
}
async function sql(container, statement) {
  return run("docker", ["exec", container, "psql", "-X", "-q", "-A", "-t", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement], { timeoutMs: 60_000 });
}
function localFetch(url, options = {}) {
  const parsed = new URL(url);
  if (parsed.protocol !== "http:" || !["localhost", "127.0.0.1", "::1"].includes(parsed.hostname)) throw new Error("non-local request rejected");
  return fetch(url, options);
}
function authHeaders(apiKey, token, json = false) {
  return { apikey: apiKey, ...(token ? { authorization: `Bearer ${token}` } : {}), ...(json ? { "content-type": "application/json" } : {}) };
}
async function body(response) { const text = await response.text(); try { return text ? JSON.parse(text) : null; } catch { return null; } }
async function authenticate(environment, identity) {
  const email = `auto-format-${identity}@example.invalid`; const password = `${randomUUID()}-Aa9!`;
  const adminHeaders = authHeaders(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY, true);
  const listed = await localFetch(`${environment.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, { headers: adminHeaders });
  const existing = ((await body(listed))?.users ?? []).find(value => value.email === email);
  const saved = await localFetch(`${environment.API_URL}/auth/v1/admin/users${existing ? `/${existing.id}` : ""}`, {
    method: existing ? "PUT" : "POST", headers: adminHeaders,
    body: JSON.stringify({ email, password, email_confirm: true, user_metadata: { full_name: "Auto Format Runtime User", role: "Student" } })
  });
  const user = await body(saved); if (!saved.ok || !user?.id) throw new Error("local auth fixture failed");
  const signed = await localFetch(`${environment.API_URL}/auth/v1/token?grant_type=password`, {
    method: "POST", headers: authHeaders(environment.ANON_KEY, undefined, true), body: JSON.stringify({ email, password })
  });
  const session = await body(signed); if (!signed.ok || !session?.access_token) throw new Error("local sign-in failed");
  return { id: user.id, token: session.access_token };
}
async function startServices(environment, overrides = {}) {
  await run("dotnet", ["build", "backend/PpkiSmartFormatter.slnx", "-c", "Release", "--no-restore", "--nologo"], { timeoutMs: 240_000 });
  const settings = localSettings({ API_PORT: String(await freePort()), WORKER_POLL_SECONDS: "1", ...overrides });
  const catalog = await resolveRuleCatalog(process.cwd()); const childEnvironment = buildChildEnvironment(process.env, environment, settings, catalog);
  if (overrides.DOCUMENT_RENDERER_BASE_URL) childEnvironment.DocumentRenderer__BaseUrl = overrides.DOCUMENT_RENDERER_BASE_URL;
  const capture = chunk => { diagnostics = `${diagnostics}${chunk}`.slice(-65_536); };
  apiProcess = spawn("dotnet", ["backend/services/Ppki.Api/bin/Release/net10.0/Ppki.Api.dll"], { cwd: process.cwd(), env: childEnvironment, shell: false, stdio: ["ignore", "pipe", "pipe"] });
  workerProcess = spawn("dotnet", ["backend/services/Ppki.Worker/bin/Release/net10.0/Ppki.Worker.dll"], { cwd: process.cwd(), env: childEnvironment, shell: false, stdio: ["ignore", "pipe", "pipe"] });
  for (const child of [apiProcess, workerProcess]) { child.stdout.on("data", capture); child.stderr.on("data", capture); }
  for (let attempt = 0; attempt < 120; attempt += 1) {
    if (apiProcess.exitCode !== null || workerProcess.exitCode !== null) throw new Error("local API or Worker exited during startup");
    try { if ((await localFetch(`${settings.apiUrl}/health/live`)).ok && (await localFetch(`${settings.apiUrl}/health/ready`)).ok) return settings.apiUrl; } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error("local API/Worker startup timed out");
}
async function stopProcess(child) {
  if (!child || child.exitCode !== null) return; child.kill("SIGTERM");
  await Promise.race([new Promise(resolve => child.once("close", resolve)), new Promise(resolve => setTimeout(resolve, 3_000))]);
  if (child.exitCode === null) child.kill("SIGKILL");
}
async function api(apiUrl, environment, token, route, { method = "GET", json, form, idempotencyKey } = {}) {
  const headers = authHeaders(environment.ANON_KEY, token, json !== undefined); if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
  const response = await localFetch(`${apiUrl}/api${route}`, { method, headers, ...(json !== undefined ? { body: JSON.stringify(json) } : {}), ...(form ? { body: form } : {}) });
  return { status: response.status, body: await body(response) };
}
async function apiBytes(apiUrl, environment, token, route) {
  const response = await localFetch(`${apiUrl}/api${route}`, { headers: authHeaders(environment.ANON_KEY, token) });
  return { status: response.status, contentType: response.headers.get("content-type")?.split(";", 1)[0] ?? null,
    bytes: Buffer.from(await response.arrayBuffer()) };
}
async function waitAudit(apiUrl, environment, token, auditId) {
  for (let attempt = 0; attempt < 180; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/audits/${auditId}`); if (result.status !== 200) throw new Error("audit status read failed");
    if (["Completed", "Failed"].includes(result.body?.status)) return result.body; await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("audit worker timed out");
}
async function waitExecution(apiUrl, environment, token, auditId, executionId) {
  for (let attempt = 0; attempt < 180; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/audits/${auditId}/fix-executions/${executionId}`); if (result.status !== 200) throw new Error("fix execution status read failed");
    if (["Completed", "Failed"].includes(result.body?.state)) return result.body; await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("fix execution worker timed out");
}
async function allFindings(apiUrl, environment, token, auditId) {
  const items = [];
  for (let page = 1; page <= 100; page += 1) {
    const result = await api(apiUrl, environment, token, `/audits/${auditId}/findings?page=${page}&pageSize=100`); if (result.status !== 200) throw new Error("audit findings read failed");
    items.push(...(result.body?.items ?? [])); if (items.length >= result.body.totalCount) return items;
  }
  throw new Error("audit findings exceeded bounded traversal");
}
function selectProductionFindings(findings) {
  const select = (validationKey, property, runIndex, paragraphIndex) => findings.find(value => value.validationKey === validationKey
    && (value.actual?.property ?? value.actual?.Property) === property
    && (runIndex === undefined || (value.location?.runIndex ?? value.location?.RunIndex) === runIndex)
    && (paragraphIndex === undefined || (value.location?.paragraphIndex ?? value.location?.ParagraphIndex) === paragraphIndex));
  return [select("body.font-times-new-roman-12", "font.ascii", 1, 0), select("body.font-times-new-roman-12", "fontSize", 1, 0),
    select("body.line-spacing-single", "lineSpacingValue", undefined, 0), select("body.first-line-indent-1cm", "firstLineIndent", undefined, 0),
    select("abstract.skripsi-single-spacing-zero-paragraph-spacing", "spacingBeforeTwips", undefined, 2),
    select("abstract.skripsi-single-spacing-zero-paragraph-spacing", "spacingAfterTwips", undefined, 2),
    select("heading.chapter-centered", "alignment", undefined, 3), select("body.justified", "alignment", undefined, 0)];
}
async function inspect(file) {
  return JSON.parse(await run("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/inspect-auto-format-docx.ps1", "-Path", file], { timeoutMs: 30_000 }));
}
async function download(apiUrl, environment, token, versionId, output) {
  const authorization = await api(apiUrl, environment, token, `/document-versions/${versionId}/download`);
  if (authorization.status !== 200 || !authorization.body?.url) return { status: authorization.status };
  const response = await localFetch(authorization.body.url); if (!response.ok) throw new Error("signed local download failed");
  await writeFile(output, Buffer.from(await response.arrayBuffer())); return { status: authorization.status, inspection: await inspect(output) };
}
function sameRunFormatting(before, after, index) { return JSON.stringify(before.firstParagraph.runs[index]) === JSON.stringify(after.firstParagraph.runs[index]); }

function fixturePathsSql(documentId) {
  return `with versions as (select * from public.document_versions where document_id='${documentId}'),
    jobs as (select * from public.document_render_jobs where document_version_id in (select id from versions)),
    artifacts as (select * from public.document_render_artifacts where document_version_id in (select id from versions) or render_job_id in (select id from jobs))
  select storage_bucket,storage_key from versions union select storage_bucket,storage_key from artifacts`;
}
async function cleanupFixture(environment, container, documentId, ownerId, title = TITLE, idempotencyKey = IDEMPOTENCY_KEY) {
  const owned = await sql(container, `select exists(select 1 from public.documents where id='${documentId}' and owner_user_id='${ownerId}' and title='${title}' and document_type_id='10000000-0000-0000-0000-000000000002')
    and (select count(*) from public.document_versions where document_id='${documentId}') between 1 and 2
    and not exists(select 1 from public.document_versions where document_id='${documentId}' and (created_by_user_id<>'${ownerId}' or version_no not in (1,2) or storage_key<>case when version_no=1 then '${ownerId}/${documentId}/'||id||'/original.docx' else '${ownerId}/${documentId}/'||id||'/document.docx' end or storage_bucket<>case when version_no=1 then 'documents-original' else 'documents-versions' end))
    and (select count(*) from public.audit_jobs where document_version_id in (select id from public.document_versions where document_id='${documentId}')) between 1 and 2
    and not exists(select 1 from public.audit_jobs where document_version_id in (select id from public.document_versions where document_id='${documentId}') and requested_by_user_id<>'${ownerId}')
    and not exists(select 1 from public.fix_execution_jobs execution where (execution.source_document_version_id in (select id from public.document_versions where document_id='${documentId}') or execution.result_document_version_id in (select id from public.document_versions where document_id='${documentId}')) and (execution.requested_by_user_id<>'${ownerId}' or execution.planner_version<>'fix-plan-preview/1.0' or execution.idempotency_key<>'${idempotencyKey}' and not exists(select 1 from public.automatic_remediation_orchestrations orchestration where orchestration.fix_execution_id=execution.id and orchestration.source_audit_job_id=execution.audit_job_id)))
    and not exists(select 1 from public.document_render_artifacts artifact join public.document_render_jobs job on job.id=artifact.render_job_id where job.document_version_id in (select id from public.document_versions where document_id='${documentId}') and (artifact.document_version_id<>job.document_version_id or artifact.storage_key<>'${ownerId}/${documentId}/'||job.id||'.pdf' or artifact.storage_bucket<>'audit-reports'));`);
  if (owned !== "t") throw new Error("exact auto-format fixture ownership mismatch");

  const paths = (await sql(container, `${fixturePathsSql(documentId)} order by storage_bucket,storage_key;`))
    .split(/\r?\n/u).filter(Boolean).map(row => row.split("|"));
  const storageGuard = await sql(container, `select concat_ws('|',count(*),coalesce(md5(string_agg(md5(to_jsonb(value)::text),'' order by value.id::text)),'none')) from storage.objects value where (value.bucket_id,value.name) not in (${paths.length ? paths.map(([bucket, key]) => `('${bucket}','${key.replaceAll("'", "''")}')`).join(",") : "(null,null)"});`);
  for (const [bucket, objectPath] of paths) {
    const encoded = objectPath.split("/").map(encodeURIComponent).join("/");
    const response = await localFetch(`${environment.API_URL}/storage/v1/object/${encodeURIComponent(bucket)}/${encoded}`, { method: "DELETE", headers: { apikey: environment.SERVICE_ROLE_KEY, authorization: `Bearer ${environment.SERVICE_ROLE_KEY}` } });
    if (!response.ok && response.status !== 404) throw new Error("exact auto-format fixture storage cleanup failed");
  }
  const storageAfter = await sql(container, `select concat_ws('|',count(*),coalesce(md5(string_agg(md5(to_jsonb(value)::text),'' order by value.id::text)),'none')) from storage.objects value where (value.bucket_id,value.name) not in (${paths.length ? paths.map(([bucket, key]) => `('${bucket}','${key.replaceAll("'", "''")}')`).join(",") : "(null,null)"});`);
  if (storageAfter !== storageGuard || await sql(container, `select exists(select 1 from storage.objects where (bucket_id,name) in (${paths.length ? paths.map(([bucket, key]) => `('${bucket}','${key.replaceAll("'", "''")}')`).join(",") : "(null,null)"}));`) !== "f")
    throw new Error("auto-format storage cleanup changed unrelated rows");

  const evidence = await sql(container, `begin;
set local session_replication_role=replica;
create temporary table cleanup_targets(table_name text not null,id uuid not null,primary key(table_name,id)) on commit drop;
insert into cleanup_targets select 'documents',id from public.documents where id='${documentId}';
insert into cleanup_targets select 'document_versions',id from public.document_versions where document_id='${documentId}';
insert into cleanup_targets select 'audit_jobs',id from public.audit_jobs where document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'fix_execution_jobs',id from public.fix_execution_jobs where audit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or source_document_version_id in (select id from cleanup_targets where table_name='document_versions') or result_document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'audit_jobs',id from public.audit_jobs where source_fix_execution_id in (select id from cleanup_targets where table_name='fix_execution_jobs') on conflict do nothing;
insert into cleanup_targets select 'audit_findings',id from public.audit_findings where audit_job_id in (select id from cleanup_targets where table_name='audit_jobs');
insert into cleanup_targets select 'audit_rule_snapshots',id from public.audit_rule_snapshots where audit_job_id in (select id from cleanup_targets where table_name='audit_jobs');
insert into cleanup_targets select 'fix_plans',id from public.fix_plans where source_audit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or source_document_version_id in (select id from cleanup_targets where table_name='document_versions') or id in (select fix_plan_id from public.fix_execution_jobs where id in (select id from cleanup_targets where table_name='fix_execution_jobs'));
insert into cleanup_targets select 'fix_plan_items',id from public.fix_plan_items where fix_plan_id in (select id from cleanup_targets where table_name='fix_plans');
insert into cleanup_targets select 'fix_plan_approval_snapshots',id from public.fix_plan_approval_snapshots where fix_plan_id in (select id from cleanup_targets where table_name='fix_plans');
insert into cleanup_targets select 'fix_item_results',id from public.fix_item_results where fix_execution_job_id in (select id from cleanup_targets where table_name='fix_execution_jobs') or fix_plan_id in (select id from cleanup_targets where table_name='fix_plans') or source_document_version_id in (select id from cleanup_targets where table_name='document_versions') or result_document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'automatic_remediation_orchestrations',id from public.automatic_remediation_orchestrations where source_audit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or reaudit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or fix_execution_id in (select id from cleanup_targets where table_name='fix_execution_jobs') or result_document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'finding_resolution_cases',id from public.finding_resolution_cases where source_audit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or source_document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'finding_resolution_events',id from public.finding_resolution_events where resolution_case_id in (select id from cleanup_targets where table_name='finding_resolution_cases') or source_fix_execution_id in (select id from cleanup_targets where table_name='fix_execution_jobs') or source_reaudit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or result_document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'finding_review_cases',id from public.finding_review_cases where audit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or source_document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'finding_review_events',id from public.finding_review_events where review_case_id in (select id from cleanup_targets where table_name='finding_review_cases');
insert into cleanup_targets select 'text_correction_analyses',id from public.text_correction_analyses where audit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'text_correction_proposals',id from public.text_correction_proposals where analysis_id in (select id from cleanup_targets where table_name='text_correction_analyses') or audit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'text_correction_decision_events',id from public.text_correction_decision_events where proposal_id in (select id from cleanup_targets where table_name='text_correction_proposals') or source_document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'text_correction_batches',id from public.text_correction_batches where source_audit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or reaudit_job_id in (select id from cleanup_targets where table_name='audit_jobs') or fix_execution_id in (select id from cleanup_targets where table_name='fix_execution_jobs') or source_document_version_id in (select id from cleanup_targets where table_name='document_versions') or result_document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'text_correction_batch_items',id from public.text_correction_batch_items where batch_id in (select id from cleanup_targets where table_name='text_correction_batches') or decision_event_id in (select id from cleanup_targets where table_name='text_correction_decision_events');
insert into cleanup_targets select 'document_render_jobs',id from public.document_render_jobs where document_version_id in (select id from cleanup_targets where table_name='document_versions');
insert into cleanup_targets select 'document_render_artifacts',id from public.document_render_artifacts where document_version_id in (select id from cleanup_targets where table_name='document_versions') or render_job_id in (select id from cleanup_targets where table_name='document_render_jobs');
insert into cleanup_targets select 'document_page_map_entries',id from public.document_page_map_entries where render_artifact_id in (select id from cleanup_targets where table_name='document_render_artifacts');
insert into cleanup_targets select 'audit_trail_events',id from public.audit_trail_events where resource_id in (select id from cleanup_targets);
create temporary table cleanup_unrelated_guard(table_name text primary key,row_count bigint not null,row_hash text not null) on commit drop;
do $guard$ declare target_table text; begin foreach target_table in array array['documents','document_versions','audit_jobs','audit_findings','audit_rule_snapshots','fix_plans','fix_plan_items','fix_plan_approval_snapshots','fix_execution_jobs','fix_item_results','automatic_remediation_orchestrations','finding_resolution_cases','finding_resolution_events','finding_review_cases','finding_review_events','text_correction_analyses','text_correction_proposals','text_correction_decision_events','text_correction_batches','text_correction_batch_items','document_render_jobs','document_render_artifacts','document_page_map_entries','audit_trail_events'] loop execute format('insert into cleanup_unrelated_guard select %L,count(*),coalesce(md5(string_agg(md5(to_jsonb(value)::text),'''' order by value.id::text)),''none'') from public.%I value where not exists(select 1 from cleanup_targets target where target.table_name=%L and target.id=value.id)',target_table,target_table,target_table); end loop; end $guard$;
delete from public.document_page_map_entries where id in (select id from cleanup_targets where table_name='document_page_map_entries');
delete from public.document_render_artifacts where id in (select id from cleanup_targets where table_name='document_render_artifacts');
delete from public.document_render_jobs where id in (select id from cleanup_targets where table_name='document_render_jobs');
delete from public.text_correction_batch_items where id in (select id from cleanup_targets where table_name='text_correction_batch_items');
delete from public.text_correction_batches where id in (select id from cleanup_targets where table_name='text_correction_batches');
delete from public.text_correction_decision_events where id in (select id from cleanup_targets where table_name='text_correction_decision_events');
delete from public.text_correction_proposals where id in (select id from cleanup_targets where table_name='text_correction_proposals');
delete from public.text_correction_analyses where id in (select id from cleanup_targets where table_name='text_correction_analyses');
delete from public.automatic_remediation_orchestrations where id in (select id from cleanup_targets where table_name='automatic_remediation_orchestrations');
delete from public.finding_review_events where id in (select id from cleanup_targets where table_name='finding_review_events');
delete from public.finding_review_cases where id in (select id from cleanup_targets where table_name='finding_review_cases');
delete from public.finding_resolution_events where id in (select id from cleanup_targets where table_name='finding_resolution_events');
delete from public.finding_resolution_cases where id in (select id from cleanup_targets where table_name='finding_resolution_cases');
delete from public.fix_item_results where id in (select id from cleanup_targets where table_name='fix_item_results');
delete from public.fix_plan_approval_snapshots where id in (select id from cleanup_targets where table_name='fix_plan_approval_snapshots');
delete from public.fix_plan_items where id in (select id from cleanup_targets where table_name='fix_plan_items');
delete from public.audit_trail_events where id in (select id from cleanup_targets where table_name='audit_trail_events');
delete from public.fix_execution_jobs where id in (select id from cleanup_targets where table_name='fix_execution_jobs');
delete from public.fix_plans where id in (select id from cleanup_targets where table_name='fix_plans');
delete from public.audit_findings where id in (select id from cleanup_targets where table_name='audit_findings');
delete from public.audit_rule_snapshots where id in (select id from cleanup_targets where table_name='audit_rule_snapshots');
delete from public.audit_jobs where id in (select id from cleanup_targets where table_name='audit_jobs');
delete from public.document_versions where id in (select id from cleanup_targets where table_name='document_versions');
delete from public.documents where id in (select id from cleanup_targets where table_name='documents');
do $verify$ declare target_table text; current_count bigint; current_hash text; expected cleanup_unrelated_guard%rowtype; target_remains boolean; begin foreach target_table in array array['documents','document_versions','audit_jobs','audit_findings','audit_rule_snapshots','fix_plans','fix_plan_items','fix_plan_approval_snapshots','fix_execution_jobs','fix_item_results','automatic_remediation_orchestrations','finding_resolution_cases','finding_resolution_events','finding_review_cases','finding_review_events','text_correction_analyses','text_correction_proposals','text_correction_decision_events','text_correction_batches','text_correction_batch_items','document_render_jobs','document_render_artifacts','document_page_map_entries','audit_trail_events'] loop execute format('select exists(select 1 from public.%I value join cleanup_targets target on target.table_name=%L and target.id=value.id)',target_table,target_table) into target_remains; if target_remains then raise exception using errcode='23514',message='Exact auto-format fixture cleanup left dependent rows'; end if; select * into expected from cleanup_unrelated_guard where table_name=target_table; execute format('select count(*),coalesce(md5(string_agg(md5(to_jsonb(value)::text),'''' order by value.id::text)),''none'') from public.%I value where not exists(select 1 from cleanup_targets target where target.table_name=%L and target.id=value.id)',target_table,target_table) into current_count,current_hash; if expected.row_count<>current_count or expected.row_hash<>current_hash then raise exception using errcode='23514',message='Auto-format cleanup changed unrelated rows'; end if; end loop; if exists(select 1 from public.document_render_jobs job where not exists(select 1 from public.document_versions version where version.id=job.document_version_id)) then raise exception using errcode='23514',message='Auto-format cleanup created an orphan render job'; end if; end $verify$;
select concat_ws(chr(9),not exists(select 1 from public.documents where id='${documentId}'),not exists(select 1 from public.document_versions where document_id='${documentId}'),not exists(select 1 from public.audit_jobs where id in (select id from cleanup_targets where table_name='audit_jobs')),not exists(select 1 from public.fix_execution_jobs where id in (select id from cleanup_targets where table_name='fix_execution_jobs')),not exists(select 1 from public.audit_jobs audit where audit.source_fix_execution_id in (select id from cleanup_targets where table_name='fix_execution_jobs') and not exists(select 1 from public.finding_resolution_events event where event.source_reaudit_job_id=audit.id and event.event_type in ('VerificationResolvedObserved','VerificationStillDetectedObserved'))),not exists(select 1 from public.document_render_jobs job where not exists(select 1 from public.document_versions version where version.id=job.document_version_id)),true);
commit;`);
  return evidence.split("\t");
}

async function main() {
  console.log("SUITE auto-format-providers-local-production-e2e"); let temporary; let environment; let container; let documentId; let ownerId; let cleanupComplete = false;
  try {
    await run("docker", ["info", "--format", "{{.ServerVersion}}"], { timeoutMs: 30_000 });
    environment = await getSupabaseEnvironment(process.cwd()); container = await databaseContainer();
    const users = { adminA: await authenticate(environment, "admin-a"), adminB: await authenticate(environment, "admin-b"), student: await authenticate(environment, "student") };
    ownerId = users.adminA.id;
    await sql(container, `update public.user_profiles set role=case id when '${users.adminA.id}' then 'PPKIAdmin' when '${users.adminB.id}' then 'PPKIAdmin' when '${users.student.id}' then 'Student' else role end where id in ('${users.adminA.id}','${users.adminB.id}','${users.student.id}');`);
    const apiUrl = await startServices(environment); report("local-production-api-and-audit-fix-workers-ready", apiProcess.exitCode === null && workerProcess.exitCode === null);

    const listed = await api(apiUrl, environment, users.adminA.token, "/documents"); if (listed.status !== 200) throw new Error("document list failed");
    const matching = listed.body.filter(value => value.title === TITLE); report("bounded-document-fixture-cardinality", matching.length <= 1);
    documentId = matching[0]?.id;
    if (!documentId) {
      const fixtureBytes = await readFile(FIXTURE); const form = new FormData(); form.set("title", TITLE); form.set("documentTypeCode", "SKRIPSI");
      form.set("file", new Blob([fixtureBytes], { type: DOCX_MIME }), "auto-format-provider-mixed.docx");
      const uploaded = await api(apiUrl, environment, users.adminA.token, "/documents", { method: "POST", form });
      if (uploaded.status !== 201 || !uploaded.body?.id) throw new Error("production DOCX upload failed"); documentId = uploaded.body.id;
    }
    let detail = await api(apiUrl, environment, users.adminA.token, `/documents/${documentId}`); if (detail.status !== 200) throw new Error("document detail failed");
    const sourceVersion = detail.body.versions.find(value => value.versionNo === 1);
    report("real-fixture-source-version-is-stable", Boolean(sourceVersion) && sourceVersion.sha256 === await sql(container, `select sha256 from public.document_versions where id='${sourceVersion.id}';`));
    const sourceAudits = sourceVersion.audits; report("bounded-source-audit-cardinality", sourceAudits.length <= 1); let sourceAuditId = sourceAudits[0]?.id;
    if (!sourceAuditId) {
      const queued = await api(apiUrl, environment, users.adminA.token, `/document-versions/${sourceVersion.id}/audits`, { method: "POST" });
      if (queued.status !== 202 || !queued.body?.id) throw new Error("production audit enqueue failed"); sourceAuditId = queued.body.id;
    }
    const sourceAudit = await waitAudit(apiUrl, environment, users.adminA.token, sourceAuditId);
    report("real-audit-worker-completed", sourceAudit.status === "Completed" && sourceAudit.persistedFindingCount > 0);
    const findings = await allFindings(apiUrl, environment, users.adminA.token, sourceAuditId); const selected = selectProductionFindings(findings);
    report("production-validators-created-eight-target-findings", selected.every(Boolean) && new Set(selected.map(value => value.id)).size === 8);
    report("historical-finding-contracts-have-actual-expected-location", selected.every(value => (value.actual?.property ?? value.actual?.Property)
      && Array.isArray(value.expected?.acceptedValues ?? value.expected?.AcceptedValues)
      && (value.expected?.acceptedValues ?? value.expected?.AcceptedValues).length === 1
      && (value.location?.compactLocation ?? value.location?.CompactLocation)?.toLowerCase().startsWith("maindocument/")));
    const findingIds = selected.map(value => value.id); const previewRequest = { findingIds };
    const previewResponse = await api(apiUrl, environment, users.adminA.token, `/audits/${sourceAuditId}/fix-plan-preview`, { method: "POST", json: previewRequest }); const preview = previewResponse.body;
    const providerPairs = new Set(preview?.operations?.map(value => `${value.capabilityId}/${value.capabilityVersion}`));
    report("server-selected-exact-provider-ids-and-versions", previewResponse.status === 200 && preview?.state === "Ready" && providerPairs.size === 6
      && providerPairs.has("body-font-direct-run/1.0") && providerPairs.has("body-line-spacing-direct-paragraph/1.0")
      && providerPairs.has("body-first-line-indent-direct-paragraph/1.0") && providerPairs.has("abstract-spacing-direct-paragraph/1.0")
      && providerPairs.has("chapter-centered-direct-paragraph/1.0") && providerPairs.has("body-justified-direct-paragraph/1.0"));
    report("one-plan-has-eight-deterministic-operations", preview.operations.length === 8 && preview.selectedFindingCount === 8 && preview.plannedFindingCount === 8);
    report("client-sends-only-finding-selection-to-preview", Object.keys(previewRequest).join() === "findingIds");

    const unsupported = findings.find(value => !SUPPORTED.has(value.validationKey)); report("production-audit-provides-unsupported-negative-finding", Boolean(unsupported));
    const negative = await api(apiUrl, environment, users.adminA.token, `/audits/${sourceAuditId}/fix-plan-preview`, { method: "POST", json: { findingIds: [unsupported.id] } });
    report("unsupported-runtime-preview-fails-safe-without-operations", negative.status === 200 && negative.body?.state === "NotAvailable" && negative.body?.unsupportedFindingCount === 1 && negative.body?.operations?.length === 0);

    const executionRequest = { findingIds, planHash: preview.planHash };
    const accepted = await api(apiUrl, environment, users.adminA.token, `/audits/${sourceAuditId}/fix-executions`, { method: "POST", json: executionRequest, idempotencyKey: IDEMPOTENCY_KEY });
    report("production-fix-execution-api-accepted-or-replayed-canonical-intent", [200, 202].includes(accepted.status) && Boolean(accepted.body?.id));
    const execution = await waitExecution(apiUrl, environment, users.adminA.token, sourceAuditId, accepted.body.id);
    report("real-queued-fix-worker-completed-all-operations", execution.state === "Completed" && execution.plannedOperationCount === 8 && execution.completedOperationCount === 8 && execution.failedOperationCount === 0 && Boolean(execution.resultDocumentVersionId));
    const replay = await api(apiUrl, environment, users.adminA.token, `/audits/${sourceAuditId}/fix-executions`, { method: "POST", json: executionRequest, idempotencyKey: IDEMPOTENCY_KEY });
    report("exact-intent-replay-returns-canonical-execution", replay.status === 200 && replay.body?.replayed === true && replay.body?.id === execution.id);

    detail = await api(apiUrl, environment, users.adminA.token, `/documents/${documentId}`); const resultVersion = detail.body?.versions?.find(value => value.id === execution.resultDocumentVersionId);
    report("one-result-version-and-current-version-advance", detail.status === 200 && detail.body.currentVersionNo === 2 && detail.body.versions.length === 2 && resultVersion?.versionNo === 2 && resultVersion.sha256 === execution.resultSha256);
    temporary = await mkdtemp(path.join(tmpdir(), "ppki-auto-format-e2e-")); const sourcePath = path.join(temporary, "source.docx"); const resultPath = path.join(temporary, "result.docx");
    const sourceDownload = await download(apiUrl, environment, users.adminA.token, sourceVersion.id, sourcePath); const resultDownload = await download(apiUrl, environment, users.adminB.token, resultVersion.id, resultPath);
    report("authorized-production-download-reads-source-and-result", sourceDownload.status === 200 && resultDownload.status === 200);
    const before = sourceDownload.inspection; const after = resultDownload.inspection;
    report("docx-package-and-relationships-remain-valid", before.packageValid && after.packageValid && before.entryCount === after.entryCount && before.entryNamesHash === after.entryNamesHash && before.relationshipsHash === after.relationshipsHash);
    report("full-document-text-fingerprint-is-identical", before.textFingerprint === after.textFingerprint && before.paragraphCount === after.paragraphCount);
    report("target-formatting-properties-changed-exactly", before.firstParagraph.runs[1].fontAscii === "Arial" && after.firstParagraph.runs[1].fontAscii === "Times New Roman"
      && before.firstParagraph.runs[1].size === "22" && after.firstParagraph.runs[1].size === "24" && before.firstParagraph.line === "276" && after.firstParagraph.line === "240"
      && before.firstParagraph.hanging === "360" && after.firstParagraph.hanging === null && after.firstParagraph.firstLine === "567"
      && before.firstParagraph.alignment === "left" && after.firstParagraph.alignment === "both"
      && before.abstractParagraph.before === "120" && before.abstractParagraph.after === "80"
      && after.abstractParagraph.before === "0" && after.abstractParagraph.after === "0"
      && before.chapterHeading.alignment === "left" && after.chapterHeading.alignment === "center");
    report("untargeted-formatting-and-hyperlink-are-preserved", after.firstParagraph.runs[1].fontHighAnsi === before.firstParagraph.runs[1].fontHighAnsi
      && after.firstParagraph.runs[1].bold === before.firstParagraph.runs[1].bold && after.firstParagraph.runs[1].underline === before.firstParagraph.runs[1].underline
      && after.firstParagraph.before === before.firstParagraph.before && after.firstParagraph.after === before.firstParagraph.after
      && after.firstParagraph.left === before.firstParagraph.left && after.firstParagraph.right === before.firstParagraph.right
      && sameRunFormatting(before, after, 0) && sameRunFormatting(before, after, 2) && before.firstParagraph.runs[2].parent === "hyperlink");

    const adminBStatus = await api(apiUrl, environment, users.adminB.token, `/audits/${sourceAuditId}/fix-executions/${execution.id}`);
    const studentStatus = await api(apiUrl, environment, users.student.token, `/audits/${sourceAuditId}/fix-executions/${execution.id}`);
    const studentDownload = await api(apiUrl, environment, users.student.token, `/document-versions/${resultVersion.id}/download`);
    report("shared-admin-b-reads-admin-a-execution-and-result", adminBStatus.status === 200 && adminBStatus.body?.id === execution.id && resultDownload.status === 200);
    report("database-role-non-admin-is-denied", studentStatus.status === 403 && studentDownload.status === 403);
    const reauditAccepted = await api(apiUrl, environment, users.adminA.token, `/fix-executions/${execution.id}/re-audit`, { method: "POST" });
    report("manual-production-reaudit-created-or-replayed", [200, 202].includes(reauditAccepted.status) && Boolean(reauditAccepted.body?.auditId));
    const resultAudit = await waitAudit(apiUrl, environment, users.adminB.token, reauditAccepted.body.auditId);
    report("production-parser-and-audit-worker-accept-result-package", resultAudit.status === "Completed" && resultAudit.documentVersionId === resultVersion.id);
    const resultFindings = await allFindings(apiUrl, environment, users.adminB.token, resultAudit.id);
    const remediatedIds = new Set(selected.map(value => `${value.validationKey}|${value.actual?.property ?? value.actual?.Property}|${value.location?.compactLocation ?? value.location?.CompactLocation}`));
    report("targeted-formatting-findings-do-not-reproduce-after-remediation", !resultFindings.some(value => remediatedIds.has(
      `${value.validationKey}|${value.actual?.property ?? value.actual?.Property}|${value.location?.compactLocation ?? value.location?.CompactLocation}`)));
    const comparison = await api(apiUrl, environment, users.adminB.token, `/fix-executions/${execution.id}/comparison`);
    report("manual-reaudit-comparison-read-path-is-compatible", comparison.status === 200 && comparison.body?.comparisonState === "Ready");

    const sourceShaAfter = await sql(container, `select sha256 from public.document_versions where id='${sourceVersion.id}';`);
    const persisted = await sql(container, `select concat_ws('|',
      (select count(*) from public.documents where title='${TITLE}'), (select count(*) from public.document_versions where document_id='${documentId}'),
      (select count(*) from public.document_versions where document_id='${documentId}' and version_no=1 and parent_version_id is null),
      (select count(*) from public.document_versions where document_id='${documentId}' and parent_version_id='${sourceVersion.id}'),
      (select count(*) from public.audit_jobs where document_version_id='${sourceVersion.id}'), (select count(*) from public.fix_execution_jobs where audit_job_id='${sourceAuditId}'),
      (select count(*) from public.audit_jobs where source_fix_execution_id='${execution.id}'), (select planned_operation_count from public.fix_execution_jobs where id='${execution.id}'),
      (select jsonb_array_length(selected_finding_ids::jsonb) from public.fix_execution_jobs where id='${execution.id}'),
      (select case when approved_plan_snapshot::jsonb #>> '{preview,planHash}'='${preview.planHash}' then 1 else 0 end from public.fix_execution_jobs where id='${execution.id}'))`);
    report("source-immutable-lineage-and-approved-plan-are-persisted", sourceShaAfter === sourceVersion.sha256 && persisted === "1|2|1|1|1|1|1|8|8|1");
    report("negative-preview-created-no-partial-result", detail.body.versions.length === 2);
    const cleanup = await cleanupFixture(environment, container, documentId, ownerId);
    report("complete-fixture-closure-and-recovery-candidate-removed", cleanup.length === 7 && cleanup.every(value => value === "t"));
    cleanupComplete = true;
    console.log("cardinality documents=1 sourceVersions=1 sourceAudits=1 selectedFindings=8 fixPlans=1 operations=8 executions=1 resultVersions=1 reAudits=1");
    console.log("auto-format-providers-production-e2e-completed: PASS");
  } catch (error) {
    console.log(`BLOCKER: ${error instanceof Error ? error.message : "local runtime unavailable"}`);
    const safe = diagnostics.split(/\r?\n/u).filter(line => /error|exception|failed|npgsql|postgres|sqlstate/i.test(line)).slice(-12).join(" | ").slice(0, 2500);
    if (safe) console.log(`SAFE-DIAGNOSTIC: ${safe}`); console.log("auto-format-providers-production-e2e-completed: FAIL"); process.exitCode = 1;
  } finally {
    await Promise.all([stopProcess(apiProcess), stopProcess(workerProcess)]);
    if (!cleanupComplete && environment && container && documentId && ownerId) {
      try {
        cleanupComplete = (await cleanupFixture(environment, container, documentId, ownerId)).every(value => value === "t");
        console.log(`exact-auto-format-fixture-final-cleanup: ${cleanupComplete ? "PASS" : "FAIL"}`);
        if (!cleanupComplete) process.exitCode = 1;
      } catch { console.log("exact-auto-format-fixture-final-cleanup: FAIL"); process.exitCode = 1; }
    }
    if (temporary && path.resolve(temporary).startsWith(path.resolve(tmpdir()))) await rm(temporary, { recursive: true, force: true });
  }
}
export {
  FIXTURE, DOCX_MIME, report, run, databaseContainer, sql, authenticate,
  startServices, api, apiBytes, waitAudit, allFindings, download, cleanupFixture
};
export async function stopServices() {
  await Promise.all([stopProcess(apiProcess), stopProcess(workerProcess)]);
  apiProcess = undefined; workerProcess = undefined;
}
export function safeServiceDiagnostics() {
  return diagnostics.split(/\r?\n/u).filter(line => /error|exception|failed|npgsql|postgres|sqlstate/i.test(line)).slice(-16).join(" | ").slice(0, 3500);
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
