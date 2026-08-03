import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import path from "node:path";

const ids = Object.freeze({
  rule: "94000000-0000-0000-0000-000000000001",
  profileRule: "94000000-0000-0000-0000-000000000002",
  document: "94000000-0000-0000-0000-000000000003",
  version: "94000000-0000-0000-0000-000000000004",
  audit: "94000000-0000-0000-0000-000000000005",
  invalidAudit: "94000000-0000-0000-0000-000000000006",
  snapshot: "94000000-0000-0000-0000-000000000007",
  finding: "94000000-0000-0000-0000-000000000008",
  documentType: "10000000-0000-0000-0000-000000000002",
  profileVersion: "21000000-0000-0000-0000-000000000001",
});

const user = Object.freeze({ email: "immutability-user@example.invalid", password: "Synthetic-passphrase-04" });
const originalHash = "a".repeat(64);

function report(name, passed) {
  console.log(`${name}: ${passed ? "PASS" : "FAIL"}`);
  return passed;
}

function run(command, args, { allowFailure = false } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.resume();
    child.on("error", () => reject(new Error("local command could not start")));
    child.on("close", (code) => {
      if (code === 0 || allowFailure) resolve({ code, stdout });
      else reject(new Error("local command failed"));
    });
  });
}

function parseEnvironment(output) {
  const values = new Map();
  for (const line of output.split(/\r?\n/)) {
    const separator = line.indexOf("=");
    if (separator > 0) values.set(line.slice(0, separator), line.slice(separator + 1).replace(/^"|"$/g, ""));
  }
  return values;
}

async function localEnvironment() {
  const command = process.platform === "win32" ? process.execPath : "npx";
  const args = process.platform === "win32"
    ? [path.join(path.dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js"), "exec", "--", "supabase", "status", "-o", "env"]
    : ["supabase", "status", "-o", "env"];
  const result = await run(command, args, { allowFailure: true });
  const values = parseEnvironment(result.stdout);
  const required = ["API_URL", "SERVICE_ROLE_KEY"];
  if (result.code !== 0 || required.some((name) => !values.get(name))) throw new Error("local stack unavailable");
  return Object.fromEntries(required.map((name) => [name, values.get(name)]));
}

async function projectId() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const match = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m);
  if (!match) throw new Error("local project configuration invalid");
  return match[1];
}

async function databaseContainer(project) {
  const result = await run("docker", ["ps", "--filter", `name=supabase_db_${project}`, "--format", "{{.Names}}"]);
  const container = result.stdout.split(/\r?\n/).find(Boolean);
  if (!container) throw new Error("local database unavailable");
  return container;
}

async function sql(container, statement) {
  const result = await run("docker", ["exec", container, "psql", "-qAt", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement]);
  return result.stdout.trim();
}

function serviceHeaders(env, json = false) {
  return {
    apikey: env.SERVICE_ROLE_KEY,
    authorization: `Bearer ${env.SERVICE_ROLE_KEY}`,
    prefer: "return=minimal",
    ...(json ? { "content-type": "application/json" } : {}),
  };
}

async function request(url, options) {
  try { return await fetch(url, options); }
  catch { throw new Error("local Data API unavailable"); }
}

async function write(env, table, { method = "POST", id, body } = {}) {
  const filter = id ? `?id=eq.${id}` : "";
  return request(`${env.API_URL}/rest/v1/${table}${filter}`, {
    method,
    headers: serviceHeaders(env, body !== undefined),
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
}

async function removeUser(env) {
  const list = await request(`${env.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, { headers: serviceHeaders(env) });
  if (!list.ok) throw new Error("local user lookup failed");
  const match = (await list.json()).users?.find((candidate) => candidate.email === user.email);
  if (!match) return;
  const removed = await request(`${env.API_URL}/auth/v1/admin/users/${match.id}`, { method: "DELETE", headers: serviceHeaders(env) });
  if (!removed.ok) throw new Error("local user cleanup failed");
}

async function createUser(env) {
  const response = await request(`${env.API_URL}/auth/v1/admin/users`, {
    method: "POST",
    headers: serviceHeaders(env, true),
    body: JSON.stringify({ email: user.email, password: user.password, email_confirm: true }),
  });
  if (!response.ok) throw new Error("local user creation failed");
  const created = await response.json();
  if (!/^[0-9a-f-]{36}$/.test(created.id)) throw new Error("local user id invalid");
  return created.id;
}

function canonicalize(value) {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonicalize(value[key])]));
  }
  return value;
}

function ruleSetHash(snapshot) {
  const canonical = [{
    rule_code: snapshot.rule_code,
    domain: snapshot.domain,
    subdomain: snapshot.subdomain,
    applies_to: snapshot.applies_to,
    element: snapshot.element,
    requirement: canonicalize(snapshot.requirement_json),
    validation_key: snapshot.validation_key,
    validation: canonicalize(snapshot.validation_json),
    severity: snapshot.severity,
    fix_mode: snapshot.fix_mode,
    source_reference: canonicalize(snapshot.source_reference_json),
    layer: snapshot.layer,
    precedence: snapshot.precedence,
    ordinal: snapshot.ordinal,
    snapshot_schema_version: snapshot.snapshot_schema_version,
  }];
  return createHash("sha256").update(JSON.stringify(canonical), "utf8").digest("hex");
}

const cleanupSql = `
delete from public.audit_trail_events where resource_id in ('${ids.document}', '${ids.version}', '${ids.audit}', '${ids.invalidAudit}');
delete from public.audit_findings where id = '${ids.finding}';
delete from public.audit_rule_snapshots where id = '${ids.snapshot}';
delete from public.audit_jobs where id in ('${ids.audit}', '${ids.invalidAudit}');
delete from public.profile_rules where id = '${ids.profileRule}';
delete from public.rules where id = '${ids.rule}';
delete from public.document_versions where id = '${ids.version}';
delete from public.documents where id = '${ids.document}';
revoke select, insert, update, delete on table
  public.document_versions,
  public.audit_jobs,
  public.audit_findings,
  public.rules,
  public.profile_rules
from service_role;
revoke update, delete on table public.audit_rule_snapshots from service_role;`;

async function main() {
  let env;
  let container;
  let passed = true;
  let userId;

  try {
    env = await localEnvironment();
    container = await databaseContainer(await projectId());
    passed = report("local-stack-database-ready", true) && passed;
  } catch {
    report("local-stack-database-ready", false);
    report("database-owner-fixture-cleanup", true);
    process.exitCode = 1;
    return;
  }

  try {
    await sql(container, cleanupSql);
    await removeUser(env);
    userId = await createUser(env);
    await sql(container, `
insert into public.rules (id, rule_code, domain, applies_to, element, official_requirement, expected_value_pattern, severity, fix_mode, validation_key, is_implemented)
values ('${ids.rule}', 'TEST-IMM-001', 'Layout', 'Document', 'Page', 'A4', 'A4', 'Error', 'Report', 'section.page-size-a4', true);
insert into public.profile_rules (id, profile_version_id, rule_id) values ('${ids.profileRule}', '${ids.profileVersion}', '${ids.rule}');
insert into public.documents (id, owner_user_id, document_type_id, title, current_version_no)
values ('${ids.document}', '${userId}', '${ids.documentType}', 'Synthetic immutability fixture', 1);
insert into public.document_versions (id, document_id, version_no, storage_bucket, storage_key, original_filename, mime_type, size_bytes, sha256, created_by_user_id)
values ('${ids.version}', '${ids.document}', 1, 'documents-original', '${userId}/${ids.document}/${ids.version}/original.docx', 'original.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 4, '${originalHash}', '${userId}');
-- Temporary local-test privileges make trigger enforcement observable instead
-- of passing merely because the fresh local service_role lacks table grants.
grant select, insert, update, delete on table
  public.document_versions,
  public.audit_jobs,
  public.audit_findings,
  public.rules,
  public.profile_rules
to service_role;
grant update, delete on table public.audit_rule_snapshots to service_role;`);
    passed = report("immutable-document-version-fixture-created", true) && passed;

    const updateVersion = await write(env, "document_versions", { method: "PATCH", id: ids.version, body: { sha256: "b".repeat(64) } });
    passed = report("service-role-document-version-update-denied", !updateVersion.ok) && passed;
    const deleteVersion = await write(env, "document_versions", { method: "DELETE", id: ids.version });
    passed = report("service-role-document-version-delete-denied", !deleteVersion.ok) && passed;
    const versionState = await sql(container, `select sha256 || ':' || size_bytes from public.document_versions where id = '${ids.version}'`);
    passed = report("document-version-row-and-hash-unchanged", versionState === `${originalHash}:4`) && passed;

    const queued = await write(env, "audit_jobs", { body: { id: ids.audit, document_version_id: ids.version, profile_version_id: ids.profileVersion, document_kind_snapshot: "Skripsi", requested_by_user_id: userId, status: "Queued" } });
    passed = report("audit-queued-created", queued.ok) && passed;
    const startedAt = new Date().toISOString();
    const processing = await write(env, "audit_jobs", { method: "PATCH", id: ids.audit, body: { status: "Processing", started_at: startedAt } });
    passed = report("audit-queued-to-processing-allowed", processing.ok) && passed;
    const identityChange = await write(env, "audit_jobs", { method: "PATCH", id: ids.audit, body: { document_version_id: "94000000-0000-0000-0000-000000000099" } });
    passed = report("audit-identity-update-denied", !identityChange.ok) && passed;
    const documentKindChange = await write(env, "audit_jobs", { method: "PATCH", id: ids.audit, body: { document_kind_snapshot: "Tesis" } });
    const documentKindState = await sql(container, `select document_kind_snapshot from public.audit_jobs where id = '${ids.audit}'`);
    passed = report("audit-document-kind-snapshot-update-denied", !documentKindChange.ok && documentKindState === "Skripsi") && passed;

    const snapshot = {
      id: ids.snapshot,
      audit_job_id: ids.audit,
      rule_id: ids.rule,
      rule_code: "TEST-IMM-001",
      domain: "Layout",
      subdomain: null,
      applies_to: "Document",
      element: "Page",
      requirement_json: { officialRequirement: "A4", expectedValuePattern: "A4" },
      validation_key: "section.page-size-a4",
      validation_json: {},
      severity: "Error",
      fix_mode: "Report",
      source_reference_json: { sourceSection: null, pdfPage: null, printedPage: null },
      layer: "profile",
      precedence: 0,
      ordinal: 1,
      snapshot_schema_version: 1,
    };
    const snapshotHash = ruleSetHash(snapshot);
    const insertedSnapshot = await write(env, "audit_rule_snapshots", { body: snapshot });
    passed = report("processing-rule-snapshot-created", insertedSnapshot.ok) && passed;
    const duplicateSnapshot = await write(env, "audit_rule_snapshots", { body: { ...snapshot, id: "94000000-0000-0000-0000-000000000009" } });
    passed = report("snapshot-retry-duplicate-denied", !duplicateSnapshot.ok) && passed;

    const finding = await write(env, "audit_findings", { body: { id: ids.finding, audit_job_id: ids.audit, rule_id: ids.rule, severity: "Error", rule_code_snapshot: snapshot.rule_code, fix_mode_snapshot: snapshot.fix_mode, message: "Synthetic finding", actual_value: {}, expected_value: {}, location: {} } });
    passed = report("processing-finding-created", finding.ok) && passed;
    const completedAt = new Date(Date.now() + 10).toISOString();
    const completed = await write(env, "audit_jobs", { method: "PATCH", id: ids.audit, body: { status: "Completed", resolved_rule_set_hash: snapshotHash, applicable_rule_count: 1, total_rules: 1, error_count: 1, completed_at: completedAt } });
    passed = report("processing-to-completed-with-snapshot-allowed", completed.ok) && passed;

    const invalidQueued = await write(env, "audit_jobs", { body: { id: ids.invalidAudit, document_version_id: ids.version, profile_version_id: ids.profileVersion, requested_by_user_id: userId, status: "Queued" } });
    passed = report("legacy-null-document-kind-snapshot-accepted", invalidQueued.ok) && passed;
    const directComplete = await write(env, "audit_jobs", { method: "PATCH", id: ids.invalidAudit, body: { status: "Completed", started_at: startedAt, completed_at: completedAt, resolved_rule_set_hash: "c".repeat(64) } });
    passed = report("queued-direct-to-completed-denied", !directComplete.ok) && passed;
    const reopen = await write(env, "audit_jobs", { method: "PATCH", id: ids.audit, body: { status: "Processing", completed_at: null } });
    passed = report("completed-to-processing-denied", !reopen.ok) && passed;
    const terminalUpdate = await write(env, "audit_jobs", { method: "PATCH", id: ids.audit, body: { error_count: 0 } });
    passed = report("completed-audit-update-denied", !terminalUpdate.ok) && passed;
    const terminalDelete = await write(env, "audit_jobs", { method: "DELETE", id: ids.audit });
    passed = report("completed-audit-delete-denied", !terminalDelete.ok) && passed;

    const snapshotUpdate = await write(env, "audit_rule_snapshots", { method: "PATCH", id: ids.snapshot, body: { precedence: 1 } });
    const snapshotDelete = await write(env, "audit_rule_snapshots", { method: "DELETE", id: ids.snapshot });
    passed = report("rule-snapshot-update-delete-denied", !snapshotUpdate.ok && !snapshotDelete.ok) && passed;
    const findingUpdate = await write(env, "audit_findings", { method: "PATCH", id: ids.finding, body: { status: "Ignored" } });
    const findingDelete = await write(env, "audit_findings", { method: "DELETE", id: ids.finding });
    passed = report("terminal-finding-update-delete-denied", !findingUpdate.ok && !findingDelete.ok) && passed;

    const snapshotBefore = await sql(container, `select requirement_json::text from public.audit_rule_snapshots where id = '${ids.snapshot}'`);
    const hashBefore = await sql(container, `select resolved_rule_set_hash from public.audit_jobs where id = '${ids.audit}'`);
    const ruleChanged = await write(env, "rules", { method: "PATCH", id: ids.rule, body: { official_requirement: "Changed after audit" } });
    const assignmentChanged = await write(env, "profile_rules", { method: "DELETE", id: ids.profileRule });
    const snapshotAfter = await sql(container, `select requirement_json::text from public.audit_rule_snapshots where id = '${ids.snapshot}'`);
    const hashAfter = await sql(container, `select resolved_rule_set_hash from public.audit_jobs where id = '${ids.audit}'`);
    passed = report("catalog-change-does-not-change-old-snapshot", ruleChanged.ok && assignmentChanged.ok && snapshotBefore === snapshotAfter) && passed;
    passed = report("catalog-change-does-not-change-old-hash", hashBefore === snapshotHash && hashAfter === snapshotHash) && passed;
  } catch {
    passed = report("immutability-smoke-execution", false) && passed;
  } finally {
    let cleaned = true;
    try { await sql(container, cleanupSql); } catch { cleaned = false; }
    try { await removeUser(env); } catch { cleaned = false; }
    report("database-owner-fixture-cleanup", cleaned);
    passed = cleaned && passed;
  }

  process.exitCode = passed ? 0 : 1;
}

main();
