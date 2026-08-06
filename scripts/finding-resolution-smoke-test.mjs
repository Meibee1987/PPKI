import { createHash, randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { readFile } from "node:fs/promises";
import { createServer } from "node:net";
import path from "node:path";
import { buildChildEnvironment, getSupabaseEnvironment, localSettings, resolveRuleCatalog } from "./dev-bootstrap.mjs";

const ids = Object.freeze({
  document: "98700000-0000-0000-0000-000000000001", sourceVersion: "98700000-0000-0000-0000-000000000002",
  resultVersion: "98700000-0000-0000-0000-000000000003", sourceAudit: "98700000-0000-0000-0000-000000000004",
  resultAudit: "98700000-0000-0000-0000-000000000005", execution: "98700000-0000-0000-0000-000000000006",
  idempotency: "98700000-0000-0000-0000-000000000007", sourceSnapshot: "98700000-0000-0000-0000-000000000008",
  resultSnapshot: "98700000-0000-0000-0000-000000000009", documentType: "10000000-0000-0000-0000-000000000002",
  profileVersion: "21000000-0000-0000-0000-000000000001",
});
const sourceFindingIds = [1, 2, 3, 4, 5].map((value) => `98700000-0000-0000-0001-${String(value).padStart(12, "0")}`);
const unselectedFindingId = "98700000-0000-0000-0001-000000000006";
const allSourceFindingIds = [...sourceFindingIds, unselectedFindingId];
const resultFindingIds = [11, 12, 13, 14].map((value) => `98700000-0000-0000-0002-${String(value).padStart(12, "0")}`);
const reviewKeys = Object.freeze({
  adminBRequest: "98700000-0000-0000-0010-000000000001",
  adminBDecision: "98700000-0000-0000-0010-000000000002",
  adminARequest: "98700000-0000-0000-0010-000000000003",
  adminADecision: "98700000-0000-0000-0010-000000000004",
});
const rule = { rule_code: "PPKI-LAY-019", domain: "LAY", subdomain: "Paragraf", applies_to: "Semua",
  element: "Perataan paragraf", requirement: { expected: "justified" }, validation_key: "body.justified",
  validation: { alignment: "both" }, severity: "Error", fix_mode: "Auto", source_reference: { sourceSection: "synthetic" },
  layer: "profile", precedence: 0, ordinal: 1, snapshot_schema_version: 1 };
const resolvedHash = createHash("sha256").update(JSON.stringify([rule])).digest("hex");
const assertions = [];
let apiProcess;
let apiDiagnostics = "";

function report(name, passed) { assertions.push(Boolean(passed)); console.log(`${name}: ${passed ? "PASS" : "FAIL"}`); if (!passed) throw new Error("runtime assertion failed"); }
function run(command, args, { env = process.env, timeoutMs = 120000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), env, shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = ""; let stderr = ""; const timeout = setTimeout(() => child.kill("SIGKILL"), timeoutMs);
    child.stdout.on("data", (chunk) => { if (stdout.length < 65536) stdout += chunk; });
    child.stderr.on("data", (chunk) => { if (stderr.length < 4096) stderr += chunk; });
    child.once("error", () => { clearTimeout(timeout); reject(new Error("local command could not start")); });
    child.once("close", (code) => { clearTimeout(timeout); code === 0 ? resolve(stdout) : reject(new Error(stderr.split(/\r?\n/).find((line) => /error|fatal/i.test(line))?.slice(0, 256) || "local command failed")); });
  });
}
async function freePort() { return new Promise((resolve, reject) => { const server = createServer(); server.once("error", reject); server.listen(0, "127.0.0.1", () => { const address = server.address(); server.close(() => resolve(address.port)); }); }); }
async function databaseContainer() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const project = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m)?.[1]; if (!project) throw new Error("local project configuration is invalid");
  const expected = `supabase_db_${project}`; const output = await run("docker", ["ps", "--filter", `name=${expected}`, "--format", "{{.Names}}"]) ;
  if (!output.split(/\r?\n/).includes(expected)) throw new Error("local database unavailable"); return expected;
}
async function sql(container, statement) { return (await run("docker", ["exec", container, "psql", "-X", "-q", "-A", "-t", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement], { timeoutMs: 60000 })).trim(); }
function localFetch(url, options = {}) { const parsed = new URL(url); if (parsed.protocol !== "http:" || !["localhost", "127.0.0.1", "::1"].includes(parsed.hostname)) throw new Error("non-local request rejected"); return fetch(url, options); }
function headers(apiKey, token, json = false, idempotencyKey) { return { apikey: apiKey, ...(token ? { authorization: `Bearer ${token}` } : {}), ...(json ? { "content-type": "application/json" } : {}), ...(idempotencyKey ? { "Idempotency-Key": idempotencyKey } : {}) }; }
async function body(response) { const text = await response.text(); try { return text ? JSON.parse(text) : null; } catch { return null; } }
async function authenticate(environment, identity, claimedRole = "PPKIAdmin") {
  const email = `finding-resolution-${identity}@example.invalid`; const password = `${randomUUID()}-Aa9!`;
  const admin = headers(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY, true);
  const listed = await localFetch(`${environment.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, { headers: admin });
  const existing = ((await listed.json()).users ?? []).find((value) => value.email === email);
  const saved = await localFetch(`${environment.API_URL}/auth/v1/admin/users${existing ? `/${existing.id}` : ""}`, { method: existing ? "PUT" : "POST", headers: admin,
    body: JSON.stringify({ email, password, email_confirm: true, user_metadata: { full_name: "Synthetic Resolution User", role: claimedRole } }) });
  const user = await body(saved); if (!saved.ok || !user?.id) throw new Error("local auth fixture failed");
  const signed = await localFetch(`${environment.API_URL}/auth/v1/token?grant_type=password`, { method: "POST", headers: headers(environment.ANON_KEY, undefined, true), body: JSON.stringify({ email, password }) });
  const session = await body(signed); if (!signed.ok || !session?.access_token) throw new Error("local sign-in failed"); return { id: user.id, token: session.access_token };
}
function actual(value) { return { Property: "alignment", NormalizedValue: value, ResolutionState: "Resolved", SourceKind: "DirectFormatting", Inherited: false }; }
function expected() { return { Property: "alignment", AcceptedValues: ["both"], ValidationKey: "body.justified" }; }
function location(index) { return { CompactLocation: `body/paragraph/${index}`, BodyElementIndex: index, ParagraphIndex: index }; }
function finding(id, index, value) { return { findingId: id, ruleOrdinal: 1, ruleCode: rule.rule_code, domain: rule.domain, element: rule.element,
  validationKey: rule.validation_key, severity: "Error", fixMode: "Auto", findingState: "Open", actualJson: JSON.stringify(actual(value)),
  expectedJson: JSON.stringify(expected()), locationJson: JSON.stringify(location(index)), snapshotSchemaVersion: 1 }; }
function approvedPlan() {
  const findings = sourceFindingIds.map((id, index) => finding(id, index < 2 ? 1 : index, "left"));
  const operations = findings.map((value, index) => ({ operationKind: "SetProperty", capabilityId: "body.justified", capabilityVersion: "1.0",
    ruleCode: rule.rule_code, validationKey: rule.validation_key, sourceFindingIds: [value.findingId], target: { scope: "MainDocument", bodyElementIndex: index, sectionIndex: null, paragraphIndex: index, runIndex: null },
    propertyIdentifier: "alignment", expected: { type: "enum", value: "Justified" }, requiresConfirmation: false, ordinal: index + 1,
    preconditionCode: "finding-snapshot-match", summaryCode: "set-paragraph-alignment" }));
  return { schemaVersion: "fix-execution-plan/1.0", source: { auditId: ids.sourceAudit, auditStatus: "Completed", documentVersionId: ids.sourceVersion,
    sourceVersionSha256: "a".repeat(64), resolvedRuleSetHash: resolvedHash, documentKindSnapshot: "Skripsi", findings },
    preview: { auditId: ids.sourceAudit, sourceDocumentVersionId: ids.sourceVersion, sourceDocumentVersionSha256: "a".repeat(64), resolvedRuleSetHash: resolvedHash,
      documentKindSnapshot: "Skripsi", plannerVersion: "fix-plan-v1", selectedFindingCount: findings.length, plannedFindingCount: findings.length,
      unsupportedFindingCount: 0, conflictFindingCount: 0, invalidFindingCount: 0, items: findings.map((value) => ({ findingId: value.findingId,
        ruleCode: rule.rule_code, validationKey: rule.validation_key, ruleOrdinal: 1, disposition: "Planned", diagnosticCode: "fix-planned" })),
      operations, conflicts: [], planHash: "c".repeat(64), state: "Ready", diagnostics: [] } };
}
function snapshotSelect() { return `rule.id, '${rule.rule_code}', '${rule.domain}', '${rule.subdomain}', '${rule.applies_to}', '${rule.element}',
  '${JSON.stringify(rule.requirement)}'::jsonb, '${rule.validation_key}', '${JSON.stringify(rule.validation)}'::jsonb, 'Error', 'Auto',
  '${JSON.stringify(rule.source_reference)}'::jsonb, '${rule.layer}', 0, 1, 1 from public.rules rule where rule.rule_code = '${rule.rule_code}'`; }
function fixtureSql(ownerId) {
  const findingValues = allSourceFindingIds.map((id, index) => `('${id}'::uuid,'${ids.sourceAudit}'::uuid,(select id from public.rules where rule_code='${rule.rule_code}'),'Error','${rule.rule_code}','Auto','synthetic','paragraph-alignment-invalid',
    '${JSON.stringify(actual("left"))}'::jsonb,'${JSON.stringify(expected())}'::jsonb,'${JSON.stringify(location(index < 2 ? 1 : index))}'::jsonb,'Open')`).join(",");
  const plan = JSON.stringify(approvedPlan()).replaceAll("$", "");
  return `do $$ begin if to_regclass('public.finding_resolution_cases') is null then raise exception 'finding resolution migration is not applied'; end if; end $$;
insert into public.documents(id,owner_user_id,document_type_id,title,current_version_no) values('${ids.document}','${ownerId}','${ids.documentType}','Synthetic resolution smoke',2) on conflict(id) do nothing;
insert into public.document_versions(id,document_id,version_no,storage_bucket,storage_key,original_filename,mime_type,size_bytes,sha256,created_by_user_id,parent_version_id) values
('${ids.sourceVersion}','${ids.document}',1,'documents-original','resolution/${ids.document}/source.docx','synthetic.docx','application/vnd.openxmlformats-officedocument.wordprocessingml.document',1,'${"a".repeat(64)}','${ownerId}',null),
('${ids.resultVersion}','${ids.document}',2,'documents-versions','resolution/${ids.document}/result.docx','synthetic-result.docx','application/vnd.openxmlformats-officedocument.wordprocessingml.document',1,'${"b".repeat(64)}','${ownerId}','${ids.sourceVersion}') on conflict(id) do nothing;
insert into public.audit_jobs(id,document_version_id,profile_version_id,requested_by_user_id,document_kind_snapshot,status,resolved_rule_set_hash,applicable_rule_count,total_rules,error_count,started_at,completed_at)
values('${ids.sourceAudit}','${ids.sourceVersion}','${ids.profileVersion}','${ownerId}','Skripsi','Completed','${resolvedHash}',1,1,6,now(),now()) on conflict(id) do nothing;
insert into public.audit_rule_snapshots(id,audit_job_id,rule_id,rule_code,domain,subdomain,applies_to,element,requirement_json,validation_key,validation_json,severity,fix_mode,source_reference_json,layer,precedence,ordinal,snapshot_schema_version)
select '${ids.sourceSnapshot}','${ids.sourceAudit}',${snapshotSelect()} on conflict(id) do nothing;
insert into public.audit_findings(id,audit_job_id,rule_id,severity,rule_code_snapshot,fix_mode_snapshot,source_section_snapshot,message,actual_value,expected_value,location,status)
select value.* from (values ${findingValues}) value(id,audit_job_id,rule_id,severity,rule_code_snapshot,fix_mode_snapshot,source_section_snapshot,message,actual_value,expected_value,location,status) on conflict(id) do nothing;
insert into public.fix_execution_jobs(id,audit_job_id,source_document_version_id,requested_by_user_id,idempotency_key,plan_hash,planner_version,selected_finding_ids,approved_plan_snapshot,state,planned_operation_count)
values('${ids.execution}','${ids.sourceAudit}','${ids.sourceVersion}','${ownerId}','${ids.idempotency}','${"c".repeat(64)}','fix-plan-v1','${JSON.stringify(sourceFindingIds)}'::jsonb,$plan$${plan}$plan$::jsonb,'Queued',5) on conflict(id) do nothing;
update public.fix_execution_jobs set state='Processing',started_at=now(),lease_expires_at=now()+interval '10 minutes' where id='${ids.execution}' and state='Queued';
update public.fix_execution_jobs set state='Completed',result_document_version_id='${ids.resultVersion}',result_sha256='${"b".repeat(64)}',completed_operation_count=5,lease_expires_at=null,completed_at=now() where id='${ids.execution}' and state='Processing';
begin;
insert into public.audit_jobs(id,document_version_id,profile_version_id,requested_by_user_id,document_kind_snapshot,status,resolved_rule_set_hash,applicable_rule_count,source_audit_job_id,source_fix_execution_id)
values('${ids.resultAudit}','${ids.resultVersion}','${ids.profileVersion}','${ownerId}','Skripsi','Queued','${resolvedHash}',1,'${ids.sourceAudit}','${ids.execution}') on conflict(id) do nothing;
insert into public.audit_rule_snapshots(id,audit_job_id,rule_id,rule_code,domain,subdomain,applies_to,element,requirement_json,validation_key,validation_json,severity,fix_mode,source_reference_json,layer,precedence,ordinal,snapshot_schema_version)
select '${ids.resultSnapshot}','${ids.resultAudit}',${snapshotSelect()} on conflict(id) do nothing; commit;`;
}
function resultSql() {
  const rows = [[resultFindingIds[0], 1, "left"], [resultFindingIds[1], 2, "both"], [resultFindingIds[2], 3, "left"], [resultFindingIds[3], 9, "left"]];
  const values = rows.map(([id, index, value]) => `('${id}'::uuid,'${ids.resultAudit}'::uuid,(select id from public.rules where rule_code='${rule.rule_code}'),'Error','${rule.rule_code}','Auto','synthetic','paragraph-alignment-invalid','${JSON.stringify(actual(value))}'::jsonb,'${JSON.stringify(expected())}'::jsonb,'${JSON.stringify(location(index))}'::jsonb,'Open')`).join(",");
  return `insert into public.audit_findings(id,audit_job_id,rule_id,severity,rule_code_snapshot,fix_mode_snapshot,source_section_snapshot,message,actual_value,expected_value,location,status)
select value.* from (values ${values}) value(id,audit_job_id,rule_id,severity,rule_code_snapshot,fix_mode_snapshot,source_section_snapshot,message,actual_value,expected_value,location,status) on conflict(id) do nothing;
update public.audit_jobs set status='Completed',total_rules=1,error_count=4,started_at=coalesce(started_at,now()),completed_at=now() where id='${ids.resultAudit}' and status in ('Queued','Processing');`;
}
async function startApi(environment) { const settings = localSettings({ API_PORT: String(await freePort()) }); const catalog = await resolveRuleCatalog(process.cwd());
  apiProcess = spawn("dotnet", ["backend/services/Ppki.Api/bin/Release/net10.0/Ppki.Api.dll"], { cwd: process.cwd(), env: buildChildEnvironment(process.env, environment, settings, catalog), shell: false, stdio: ["ignore", "pipe", "pipe"] });
  const capture = (chunk) => { apiDiagnostics = `${apiDiagnostics}${chunk}`.slice(-32768); }; apiProcess.stdout.on("data", capture); apiProcess.stderr.on("data", capture); for (let attempt = 0; attempt < 80; attempt += 1) { if (apiProcess.exitCode !== null) throw new Error("local API exited during startup"); try { if ((await localFetch(`${settings.apiUrl}/health/live`)).ok) return settings.apiUrl; } catch {} await new Promise((resolve) => setTimeout(resolve, 250)); } throw new Error("local API startup timed out"); }
async function stopApi() { if (!apiProcess || apiProcess.exitCode !== null) return; apiProcess.kill("SIGTERM"); await Promise.race([new Promise((resolve) => apiProcess.once("close", resolve)), new Promise((resolve) => setTimeout(resolve, 3000))]); if (apiProcess.exitCode === null) apiProcess.kill("SIGKILL"); }
async function reconcile(apiUrl, environment, token, executionId = ids.execution) { const response = await localFetch(`${apiUrl}/api/fix-executions/${executionId}/resolution-reconciliation`, { method: "POST", headers: headers(environment.ANON_KEY, token) }); return { status: response.status, body: await body(response) }; }
async function resolution(apiUrl, environment, token, findingId, auditId = ids.sourceAudit) { const response = await localFetch(`${apiUrl}/api/audits/${auditId}/findings/${findingId}/resolution`, { headers: headers(environment.ANON_KEY, token) }); return { status: response.status, body: await body(response) }; }
async function requestReview(apiUrl, environment, token, auditId, findingId, key, disposition) { const response = await localFetch(`${apiUrl}/api/audits/${auditId}/findings/${findingId}/review-requests`, { method: "POST", headers: headers(environment.ANON_KEY, token, true, key), body: JSON.stringify({ requestedDisposition: disposition, note: "shared admin closure" }) }); return { status: response.status, body: await body(response) }; }
async function decideReview(apiUrl, environment, token, caseId, key, decision) { const response = await localFetch(`${apiUrl}/api/finding-reviews/${caseId}/decisions`, { method: "POST", headers: headers(environment.ANON_KEY, token, true, key), body: JSON.stringify({ decision, note: "shared admin decision" }) }); return { status: response.status, body: await body(response) }; }
function evidenceSql() { return `select concat_ws(',',(select count(*) from public.finding_resolution_cases where source_audit_job_id='${ids.sourceAudit}'),(select count(*) from public.finding_resolution_events where source_fix_execution_id='${ids.execution}'));`; }
function historySql() { return `select concat_ws(',',(select md5(string_agg(row_to_json(a)::text,'' order by a.id)) from public.audit_jobs a where id in ('${ids.sourceAudit}','${ids.resultAudit}')),(select md5(row_to_json(e)::text) from public.fix_execution_jobs e where id='${ids.execution}'),(select md5(string_agg(row_to_json(v)::text,'' order by v.id)) from public.document_versions v where id in ('${ids.sourceVersion}','${ids.resultVersion}')),(select coalesce(md5(string_agg(row_to_json(f)::text,'' order by f.id)),'none') from public.audit_findings f where audit_job_id in ('${ids.sourceAudit}','${ids.resultAudit}')),(select md5(string_agg(row_to_json(s)::text,'' order by s.id)) from public.audit_rule_snapshots s where audit_job_id in ('${ids.sourceAudit}','${ids.resultAudit}')));`; }
function safeResponse(value) { return !Object.keys(value ?? {}).some((key) => /actual|expected|fingerprint|semantic.?key|text|filename|storage|path|url|xml|secret/i.test(key)) && (typeof value !== "object" || value === null || Object.values(value).every(safeResponse)); }
async function expectConflict(apiUrl, environment, token, container, name, mutate, restore, code) {
  await sql(container, `begin; set local session_replication_role=replica; ${mutate}; commit;`);
  try { const response = await reconcile(apiUrl, environment, token); report(name, response.status === 409 && response.body?.code === code && safeResponse(response.body)); }
  finally { await sql(container, `begin; set local session_replication_role=replica; ${restore}; commit;`); }
}

async function main() {
  console.log("SUITE finding-resolution-local"); let originalSeverity;
  try {
    const environment = await getSupabaseEnvironment(process.cwd()); const container = await databaseContainer(); report("local-only-infrastructure-ready", true);
    const owner = await authenticate(environment, "owner", "PPKIAdmin");
    const adminB = await authenticate(environment, "admin-b", "PPKIAdmin");
    const foreign = await authenticate(environment, "foreign", "PPKIAdmin");
    await sql(container, `update public.user_profiles set role=case id when '${owner.id}' then 'PPKIAdmin' when '${adminB.id}' then 'PPKIAdmin' when '${foreign.id}' then 'Student' else role end where id in ('${owner.id}','${adminB.id}','${foreign.id}');`);
    const apiUrl = await startApi(environment);
    await sql(container, fixtureSql(owner.id)); const before = await sql(container, evidenceSql());
    const open = await resolution(apiUrl, environment, owner.token, unselectedFindingId);
    report("get-is-read-only-and-open-without-a-case", open.status === 200 && open.body?.currentState === "Open" && open.body?.resolutionCaseId === null
      && open.body?.eventCount === 0 && before === await sql(container, evidenceSql()));
    const persistedSelection = await sql(container, `select (select array_agg(value order by value) from jsonb_array_elements_text(selected_finding_ids) value)=(select array_agg(item->>'findingId' order by item->>'findingId') from jsonb_array_elements(approved_plan_snapshot#>'{source,findings}') item) from public.fix_execution_jobs where id='${ids.execution}';`);
    report("selected-findings-come-from-persisted-approved-plan", persistedSelection === "t");
    const resultStatus = await sql(container, `select status from public.audit_jobs where id='${ids.resultAudit}';`);
    if (resultStatus === "Queued") {
      const concurrent = await Promise.all([reconcile(apiUrl, environment, owner.token), reconcile(apiUrl, environment, owner.token)]);
      console.log(`concurrent-statuses: ${concurrent.map((value) => `${value.status}:${value.body?.code ?? "none"}`).join(",")}`);
      report("owner-post-and-concurrent-pending-reconciliation-converge", concurrent.every((value) => value.status === 202));
      const counts = await sql(container, evidenceSql()); report("selected-only-applied-and-one-event-per-source-fact", counts === "5,10" && await sql(container, `select count(*)=0 from public.finding_resolution_cases where source_audit_finding_id='${unselectedFindingId}';`) === "t");
      const pending = await resolution(apiUrl, environment, owner.token, sourceFindingIds[0]); report("queued-reaudit-projects-applied-and-reaudit-pending", pending.body?.currentState === "ReauditPending" && pending.body?.eventCount === 2);
      await sql(container, `update public.audit_jobs set status='Processing',started_at=coalesce(started_at,now()) where id='${ids.resultAudit}' and status='Queued';`);
      const processing = await reconcile(apiUrl, environment, owner.token); const processingState = await resolution(apiUrl, environment, owner.token, sourceFindingIds[0]);
      report("processing-reaudit-remains-pending-without-growth", processing.status === 202 && processingState.body?.currentState === "ReauditPending" && counts === await sql(container, evidenceSql()));
      await sql(container, resultSql());
    } else report("rerun-reuses-terminal-bounded-fixture", resultStatus === "Completed");
    const verified = await reconcile(apiUrl, environment, owner.token); report("completed-reconciliation-is-created-or-replayed", [200, 201].includes(verified.status));
    const replayBefore = await sql(container, evidenceSql()); const replay = await reconcile(apiUrl, environment, owner.token); report("replay-does-not-grow-data", replay.status === 200 && replay.body?.replayed === true && replayBefore === await sql(container, evidenceSql()));
    const states = await Promise.all(sourceFindingIds.map((id) => resolution(apiUrl, environment, owner.token, id)));
    const resolved = states.find((value) => value.body?.comparisonStatus === "NoLongerDetected"); const still = states.find((value) => value.body?.comparisonStatus === "StillDetected"); const changed = states.find((value) => value.body?.comparisonStatus === "Changed");
    report("resolved-still-and-changed-map-conservatively", resolved?.body?.currentState === "VerifiedResolved" && resolved.body?.resultFindingId === null
      && still?.body?.currentState === "VerifiedStillDetected" && Boolean(still.body?.resultFindingId)
      && changed?.body?.currentState === "VerifiedStillDetected" && Boolean(changed.body?.resultFindingId));
    report("duplicates-retain-separate-cases", states[0].body?.resolutionCaseId !== states[1].body?.resolutionCaseId);
    const unselected = await resolution(apiUrl, environment, owner.token, unselectedFindingId); const newly = await resolution(apiUrl, environment, owner.token, resultFindingIds[3], ids.resultAudit);
    report("unselected-and-newly-detected-remain-open", unselected.body?.currentState === "Open" && unselected.body?.resolutionCaseId === null && newly.body?.currentState === "Open" && newly.body?.resolutionCaseId === null);
    report("owner-get-and-response-privacy", states.every((value) => value.status === 200 && safeResponse(value.body)) && safeResponse(verified.body));
    const sequenceValid = await sql(container, `select not exists(select 1 from public.finding_resolution_events group by resolution_case_id having min(sequence)<>1 or max(sequence)<>count(*) or count(*)<>count(distinct sequence));`);
    report("event-sequences-are-contiguous-and-unique", sequenceValid === "t");
    const foreignRead = await resolution(apiUrl, environment, foreign.token, sourceFindingIds[0]); const foreignPost = await reconcile(apiUrl, environment, foreign.token);
    const unknownId = "98700000-0000-0000-0003-000000000099"; const unknownRead = await resolution(apiUrl, environment, owner.token, unknownId); const unknownPost = await reconcile(apiUrl, environment, owner.token, unknownId);
    report("non-admin-is-forbidden-before-resource-load-and-unknown-is-safe", foreignRead.status === 403 && foreignPost.status === 403 && unknownRead.status === 404 && unknownPost.status === 404 && safeResponse(foreignRead.body) && safeResponse(unknownRead.body));
    const unauth = await localFetch(`${apiUrl}/api/audits/${ids.sourceAudit}/findings/${sourceFindingIds[0]}/resolution`); report("unauthenticated-is-rejected", unauth.status === 401);
    const malformedRead = await resolution(apiUrl, environment, owner.token, "not-a-guid", "not-a-guid"); const malformedPost = await reconcile(apiUrl, environment, owner.token, "not-a-guid");
    report("malformed-identifiers-return-safe-bad-request", malformedRead.status === 400 && malformedRead.body?.code === "resolution-id-invalid" && malformedPost.status === 400 && malformedPost.body?.code === "resolution-execution-id-invalid" && safeResponse(malformedRead.body) && safeResponse(malformedPost.body));
    const browserCases = `${environment.API_URL}/rest/v1/finding_resolution_cases`;
    const browserEvents = `${environment.API_URL}/rest/v1/finding_resolution_events`;
    const browserBefore = await sql(container, evidenceSql()); const browserWrites = await Promise.all([
      localFetch(browserCases, { method: "POST", headers: headers(environment.ANON_KEY, owner.token, true), body: "{}" }),
      localFetch(browserEvents, { method: "POST", headers: headers(environment.ANON_KEY, owner.token, true), body: "{}" }),
      localFetch(`${browserCases}?source_audit_job_id=eq.${ids.sourceAudit}`, { method: "PATCH", headers: headers(environment.ANON_KEY, owner.token, true), body: "{}" }),
      localFetch(`${browserEvents}?source_fix_execution_id=eq.${ids.execution}`, { method: "DELETE", headers: headers(environment.ANON_KEY, owner.token) })]);
    report("browser-direct-case-and-event-inserts-are-rejected", browserWrites.slice(0, 2).every((response) => response.status >= 400));
    const immutable = await sql(container, `select not has_table_privilege('authenticated','public.finding_resolution_events','INSERT,UPDATE,DELETE') and not has_table_privilege('authenticated','public.finding_resolution_cases','INSERT,UPDATE,DELETE');`); report("browser-write-privileges-are-not-granted", immutable === "t");
    report("browser-update-and-delete-cannot-mutate", browserBefore === await sql(container, evidenceSql()));
    report("service-backend-insert-succeeded", replayBefore === "5,15");
    let rejected = false; try { await sql(container, `begin; set local role service_role; update public.finding_resolution_events set sequence=99 where source_fix_execution_id='${ids.execution}'; rollback;`); } catch { rejected = true; } report("event-update-is-rejected-by-trigger", rejected);
    rejected = false; try { await sql(container, `begin; set local role service_role; delete from public.finding_resolution_events where source_fix_execution_id='${ids.execution}'; rollback;`); } catch { rejected = true; } report("event-delete-is-rejected-by-trigger", rejected);
    rejected = false; try { await sql(container, `begin; set local role service_role; update public.finding_resolution_cases set source_audit_job_id='${ids.resultAudit}' where source_audit_finding_id='${sourceFindingIds[0]}'; rollback;`); } catch { rejected = true; } report("case-identity-update-is-rejected-by-trigger", rejected);
    const history = await sql(container, historySql()); const readOnlyBefore = await sql(container, evidenceSql()); await resolution(apiUrl, environment, owner.token, sourceFindingIds[0]);
    report("terminal-get-is-mutation-free", history === await sql(container, historySql()) && readOnlyBefore === await sql(container, evidenceSql()));
    await expectConflict(apiUrl, environment, owner.token, container, "non-completed-execution-is-safe", `update public.fix_execution_jobs set state='Queued' where id='${ids.execution}'`, `update public.fix_execution_jobs set state='Completed' where id='${ids.execution}'`, "resolution-execution-not-completed");
    await expectConflict(apiUrl, environment, owner.token, container, "missing-result-version-is-safe", `update public.fix_execution_jobs set result_document_version_id=null where id='${ids.execution}'`, `update public.fix_execution_jobs set result_document_version_id='${ids.resultVersion}' where id='${ids.execution}'`, "resolution-result-version-missing");
    await expectConflict(apiUrl, environment, owner.token, container, "missing-canonical-reaudit-is-safe", `update public.audit_jobs set source_fix_execution_id=null,source_audit_job_id=null where id='${ids.resultAudit}'`, `update public.audit_jobs set source_fix_execution_id='${ids.execution}',source_audit_job_id='${ids.sourceAudit}' where id='${ids.resultAudit}'`, "resolution-reaudit-missing");
    await expectConflict(apiUrl, environment, owner.token, container, "failed-reaudit-is-safe", `update public.audit_jobs set status='Failed' where id='${ids.resultAudit}'`, `update public.audit_jobs set status='Completed' where id='${ids.resultAudit}'`, "resolution-comparison-invalid");
    await expectConflict(apiUrl, environment, owner.token, container, "lineage-mismatch-is-safe", `update public.fix_execution_jobs set requested_by_user_id='${foreign.id}' where id='${ids.execution}'`, `update public.fix_execution_jobs set requested_by_user_id='${owner.id}' where id='${ids.execution}'`, "resolution-lineage-mismatch");
    await expectConflict(apiUrl, environment, owner.token, container, "historical-context-mismatch-is-safe", `update public.audit_jobs set resolved_rule_set_hash='${"d".repeat(64)}' where id='${ids.resultAudit}'`, `update public.audit_jobs set resolved_rule_set_hash='${resolvedHash}' where id='${ids.resultAudit}'`, "resolution-historical-context-mismatch");

    const sharedHistory = await sql(container, historySql());
    const adminADocuments = await localFetch(`${apiUrl}/api/documents`, { headers: headers(environment.ANON_KEY, owner.token) });
    const adminADetail = await localFetch(`${apiUrl}/api/documents/${ids.document}`, { headers: headers(environment.ANON_KEY, owner.token) });
    const adminAAudit = await localFetch(`${apiUrl}/api/audits/${ids.sourceAudit}`, { headers: headers(environment.ANON_KEY, owner.token) });
    report("admin-a-manages-own-document-version-audit-and-findings", adminADocuments.status === 200 && adminADetail.status === 200 && adminAAudit.status === 200);

    const adminBDocuments = await localFetch(`${apiUrl}/api/documents`, { headers: headers(environment.ANON_KEY, adminB.token) });
    const adminBDetail = await localFetch(`${apiUrl}/api/documents/${ids.document}`, { headers: headers(environment.ANON_KEY, adminB.token) });
    const adminBAudit = await localFetch(`${apiUrl}/api/audits/${ids.sourceAudit}`, { headers: headers(environment.ANON_KEY, adminB.token) });
    const adminBFindings = await localFetch(`${apiUrl}/api/audits/${ids.sourceAudit}/findings`, { headers: headers(environment.ANON_KEY, adminB.token) });
    const adminBFixPlan = await localFetch(`${apiUrl}/api/audits/${ids.sourceAudit}/fix-plan-preview`, { method: "POST", headers: headers(environment.ANON_KEY, adminB.token, true), body: JSON.stringify({ findingIds: [sourceFindingIds[0]] }) });
    const adminBFixStatus = await localFetch(`${apiUrl}/api/audits/${ids.sourceAudit}/fix-executions/${ids.execution}`, { headers: headers(environment.ANON_KEY, adminB.token) });
    const adminBReaudit = await localFetch(`${apiUrl}/api/fix-executions/${ids.execution}/re-audit`, { method: "POST", headers: headers(environment.ANON_KEY, adminB.token) });
    const adminBComparison = await localFetch(`${apiUrl}/api/fix-executions/${ids.execution}/comparison`, { headers: headers(environment.ANON_KEY, adminB.token) });
    const adminBResolution = await resolution(apiUrl, environment, adminB.token, sourceFindingIds[0]);
    const detailBody = await body(adminBDetail);
    report("admin-b-shares-admin-a-document-version-audit-findings-and-fixplan", adminBDocuments.status === 200 && adminBDetail.status === 200 && detailBody?.versions?.some(value => value.id === ids.sourceVersion) && adminBAudit.status === 200 && adminBFindings.status === 200 && adminBFixPlan.status === 200 && adminBFixStatus.status === 200);
    report("admin-b-reads-admin-a-reaudit-comparison-and-resolution-without-assignment", adminBReaudit.status === 200 && adminBComparison.status === 200 && adminBResolution.status === 200);

    const adminBRequest = await requestReview(apiUrl, environment, adminB.token, ids.sourceAudit, unselectedFindingId, reviewKeys.adminBRequest, "Ignore");
    const adminBDecision = await decideReview(apiUrl, environment, adminB.token, adminBRequest.body?.review?.reviewCaseId, reviewKeys.adminBDecision, "Ignore");
    report("admin-b-decides-admin-a-finding-with-authenticated-actor", [200,201].includes(adminBRequest.status) && [200,201].includes(adminBDecision.status) && adminBDecision.body?.review?.reviewState === "Ignored" && adminBDecision.body?.review?.events?.every(event => event.actorUserId === adminB.id));

    const adminARequest = await requestReview(apiUrl, environment, owner.token, ids.resultAudit, resultFindingIds[3], reviewKeys.adminARequest, "AcceptedRisk");
    const adminADecision = await decideReview(apiUrl, environment, owner.token, adminARequest.body?.review?.reviewCaseId, reviewKeys.adminADecision, "AcceptRisk");
    report("admin-a-self-review-remains-available", [200,201].includes(adminARequest.status) && [200,201].includes(adminADecision.status) && adminADecision.body?.review?.reviewState === "AcceptedRisk" && adminADecision.body?.review?.events?.every(event => event.actorUserId === owner.id));

    const rlsUrl = `${environment.API_URL}/rest/v1/documents?id=eq.${ids.document}&select=id,owner_user_id`;
    const rlsA = await localFetch(rlsUrl, { headers: headers(environment.ANON_KEY, owner.token) });
    const rlsB = await localFetch(rlsUrl, { headers: headers(environment.ANON_KEY, adminB.token) });
    const rlsForeign = await localFetch(rlsUrl, { headers: headers(environment.ANON_KEY, foreign.token) });
    const [rlsABody, rlsBBody, rlsForeignBody] = await Promise.all([body(rlsA), body(rlsB), body(rlsForeign)]);
    report("api-and-rls-share-exact-admin-decision", rlsA.status === 200 && rlsB.status === 200 && rlsABody?.length === 1 && rlsBBody?.length === 1 && (rlsForeign.status === 200 ? rlsForeignBody?.length === 0 : [401,403].includes(rlsForeign.status)));

    await sql(container, `update public.user_profiles set role='Student' where id='${adminB.id}';`);
    try {
      const downgradedApi = await localFetch(`${apiUrl}/api/documents/${ids.document}`, { headers: headers(environment.ANON_KEY, adminB.token) });
      const downgradedRls = await localFetch(rlsUrl, { headers: headers(environment.ANON_KEY, adminB.token) });
      const downgradedBody = await body(downgradedRls);
      report("admin-b-database-downgrade-immediately-overrides-ppkiadmin-token-claim", downgradedApi.status === 403 && (downgradedRls.status === 200 ? downgradedBody?.length === 0 : [401,403].includes(downgradedRls.status)));
    } finally {
      await sql(container, `update public.user_profiles set role='PPKIAdmin' where id='${adminB.id}';`);
    }

    const publicHealth = await localFetch(`${apiUrl}/health/live`);
    const signup = await localFetch(`${environment.API_URL}/auth/v1/signup`, { method: "POST", headers: headers(environment.ANON_KEY, undefined, true), body: JSON.stringify({ email: `closed-${randomUUID()}@example.invalid`, password: `${randomUUID()}-Aa9!` }) });
    report("existing-login-public-health-and-closed-signup-remain-correct", publicHealth.status === 200 && signup.status >= 400);
    report("shared-admin-closure-is-historically-immutable-and-bounded", sharedHistory === await sql(container, historySql()) && await sql(container, `select concat_ws(',',count(distinct review_case.id),count(event.id)) from public.finding_review_cases review_case join public.finding_review_events event on event.review_case_id=review_case.id where review_case.audit_finding_id in ('${unselectedFindingId}','${resultFindingIds[3]}');`) === "2,4");

    originalSeverity = await sql(container, `select severity from public.rules where rule_code='${rule.rule_code}';`); await sql(container, `update public.rules set severity='Warning' where rule_code='${rule.rule_code}';`);
    const liveRuleReplay = await reconcile(apiUrl, environment, owner.token); report("live-rules-mutation-does-not-change-historical-state", liveRuleReplay.status === 200 && replayBefore === await sql(container, evidenceSql()));
    await sql(container, `update public.rules set severity='${originalSeverity}' where rule_code='${rule.rule_code}';`); originalSeverity = undefined;
    report("historical-resources-remain-byte-stable", history === await sql(container, historySql()));
    report("second-run-cardinality-is-bounded", (await sql(container, evidenceSql())) === "5,15");
    console.log("finding-resolution-runtime-smoke-completed: PASS");
  } catch (error) { console.log(`BLOCKER: ${error instanceof Error ? error.message : "local runtime unavailable"}`); if (apiDiagnostics) console.log(`API-DIAGNOSTIC: ${apiDiagnostics.replace(/[0-9a-f]{8}-[0-9a-f-]{27}/gi, "[uuid]").replace(/[A-Z]:\\[^\r\n:]*/gi, "[path]").split(/\r?\n/).filter((line) => /error|exception|failed|invalid|npgsql|dbupdate|postgres|sqlstate|constraint|messagetext/i.test(line)).slice(-16).join(" | ").slice(0, 3072)}`); console.log("finding-resolution-runtime-smoke-completed: FAIL"); process.exitCode = 1; }
  finally { if (originalSeverity) { try { const environment = await getSupabaseEnvironment(process.cwd()); const container = await databaseContainer(); await sql(container, `update public.rules set severity='${originalSeverity}' where rule_code='${rule.rule_code}';`); } catch {} } await stopApi(); }
}
main();
