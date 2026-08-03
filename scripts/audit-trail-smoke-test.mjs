import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import path from "node:path";

const ids = Object.freeze({
  document: "95000000-0000-0000-0000-000000000001",
  version: "95000000-0000-0000-0000-000000000002",
  audit: "95000000-0000-0000-0000-000000000003",
  userEvent: "95000000-0000-0000-0000-000000000004",
  serviceEvent: "95000000-0000-0000-0000-000000000005",
  invalidActorEvent: "95000000-0000-0000-0000-000000000006",
  invalidMetadataEvent: "95000000-0000-0000-0000-000000000007",
  forbiddenMetadataEvent: "95000000-0000-0000-0000-000000000008",
  documentType: "10000000-0000-0000-0000-000000000002",
  profileVersion: "21000000-0000-0000-0000-000000000001",
});

const user = Object.freeze({ email: "audit-trail-user@example.invalid", password: "Synthetic-passphrase-05" });

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
  const required = ["API_URL", "PUBLISHABLE_KEY", "SERVICE_ROLE_KEY"];
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

function headers(apiKey, token, json = false) {
  return {
    apikey: apiKey,
    ...(token ? { authorization: `Bearer ${token}` } : {}),
    ...(json ? { "content-type": "application/json", prefer: "return=minimal" } : {}),
  };
}

async function request(url, options) {
  try { return await fetch(url, options); }
  catch { throw new Error("local Data API unavailable"); }
}

async function write(env, table, apiKey, token, { method = "POST", id, body } = {}) {
  return request(`${env.API_URL}/rest/v1/${table}${id ? `?id=eq.${id}` : ""}`, {
    method,
    headers: headers(apiKey, token, body !== undefined),
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
}

async function removeUser(env) {
  const list = await request(`${env.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, {
    headers: headers(env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY),
  });
  if (!list.ok) throw new Error("local user lookup failed");
  const existing = (await list.json()).users?.find((candidate) => candidate.email === user.email);
  if (!existing) return;
  const removed = await request(`${env.API_URL}/auth/v1/admin/users/${existing.id}`, {
    method: "DELETE",
    headers: headers(env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY),
  });
  if (!removed.ok) throw new Error("local user cleanup failed");
}

async function createUser(env) {
  const response = await request(`${env.API_URL}/auth/v1/admin/users`, {
    method: "POST",
    headers: headers(env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, true),
    body: JSON.stringify({ email: user.email, password: user.password, email_confirm: true }),
  });
  if (!response.ok) throw new Error("local user creation failed");
  const created = await response.json();
  if (!/^[0-9a-f-]{36}$/.test(created.id)) throw new Error("local user id invalid");
  return created.id;
}

async function signIn(env) {
  const response = await request(`${env.API_URL}/auth/v1/token?grant_type=password`, {
    method: "POST",
    headers: headers(env.PUBLISHABLE_KEY, undefined, true),
    body: JSON.stringify({ email: user.email, password: user.password }),
  });
  if (!response.ok) throw new Error("local sign-in failed");
  const session = await response.json();
  if (!session.access_token) throw new Error("local sign-in failed");
  return session.access_token;
}

const cleanupSql = `
delete from public.audit_trail_events where resource_id in ('${ids.document}', '${ids.version}', '${ids.audit}') or id in ('${ids.userEvent}', '${ids.serviceEvent}');
delete from public.audit_jobs where id = '${ids.audit}';
delete from public.document_versions where id = '${ids.version}';
delete from public.documents where id = '${ids.document}';
revoke select, update, delete on table public.audit_trail_events from service_role;
revoke select, update on table public.audit_jobs from service_role;`;

async function main() {
  let env;
  let container;
  let userId;
  let passed = true;

  try {
    env = await localEnvironment();
    container = await databaseContainer(await projectId());
    passed = report("local-stack-database-ready", true) && passed;
  } catch {
    report("local-stack-database-ready", false);
    report("database-owner-audit-cleanup", true);
    process.exitCode = 1;
    return;
  }

  try {
    await sql(container, cleanupSql);
    await removeUser(env);
    userId = await createUser(env);
    const userToken = await signIn(env);
    await sql(container, `
grant select, update, delete on table public.audit_trail_events to service_role;
grant select, update on table public.audit_jobs to service_role;
insert into public.documents (id, owner_user_id, document_type_id, title, current_version_no)
values ('${ids.document}', '${userId}', '${ids.documentType}', 'Synthetic audit trail fixture', 1);
insert into public.document_versions (id, document_id, version_no, storage_bucket, storage_key, original_filename, mime_type, size_bytes, sha256, created_by_user_id)
values ('${ids.version}', '${ids.document}', 1, 'documents-original', '${userId}/${ids.document}/${ids.version}/original.docx', 'original.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 4, '${"a".repeat(64)}', '${userId}');
insert into public.audit_jobs (id, document_version_id, profile_version_id, document_kind_snapshot, requested_by_user_id, status)
values ('${ids.audit}', '${ids.version}', '${ids.profileVersion}', 'Skripsi', '${userId}', 'Queued');`);

    const correlation = "95000000-0000-4000-8000-000000000010";
    const userEvent = await write(env, "audit_trail_events", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { body: {
      id: ids.userEvent, actor_type: "user", actor_user_id: userId, action: "document.download_authorized",
      resource_type: "document_version", resource_id: ids.version, owner_user_id: userId,
      correlation_id: correlation, metadata: { download_kind: "original" }, event_schema_version: 1, event_source: "application",
    } });
    passed = report("trusted-server-user-event-created", userEvent.ok) && passed;

    const serviceEvent = await write(env, "audit_trail_events", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { body: {
      id: ids.serviceEvent, actor_type: "service", actor_service: "worker", action: "audit.rule_snapshot_created",
      resource_type: "audit_job", resource_id: ids.audit, owner_user_id: userId,
      correlation_id: ids.audit, metadata: { applicable_rule_count: 1 }, event_schema_version: 1, event_source: "application",
    } });
    passed = report("trusted-server-service-event-created", serviceEvent.ok) && passed;

    const invalidActor = await write(env, "audit_trail_events", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { body: {
      id: ids.invalidActorEvent, actor_type: "user", action: "audit.requested", resource_type: "audit_job",
      resource_id: ids.audit, correlation_id: correlation, metadata: {}, event_schema_version: 1, event_source: "application",
    } });
    passed = report("actor-constraint-enforced", !invalidActor.ok) && passed;
    const invalidMetadata = await write(env, "audit_trail_events", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { body: {
      id: ids.invalidMetadataEvent, actor_type: "system", action: "audit.requested", resource_type: "audit_job",
      resource_id: ids.audit, correlation_id: correlation, metadata: [], event_schema_version: 1, event_source: "application",
    } });
    passed = report("metadata-object-constraint-enforced", !invalidMetadata.ok) && passed;
    const forbiddenMetadata = await write(env, "audit_trail_events", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { body: {
      id: ids.forbiddenMetadataEvent, actor_type: "system", action: "audit.requested", resource_type: "audit_job",
      resource_id: ids.audit, correlation_id: correlation, metadata: { signedUrl: "synthetic" }, event_schema_version: 1, event_source: "application",
    } });
    passed = report("forbidden-metadata-key-denied", !forbiddenMetadata.ok) && passed;

    const fingerprintBefore = await sql(container, `select md5(action || ':' || metadata::text || ':' || correlation_id::text) from public.audit_trail_events where id = '${ids.userEvent}'`);
    const authenticatedUpdate = await write(env, "audit_trail_events", env.PUBLISHABLE_KEY, userToken, { method: "PATCH", id: ids.userEvent, body: { action: "document.created" } });
    const authenticatedDelete = await write(env, "audit_trail_events", env.PUBLISHABLE_KEY, userToken, { method: "DELETE", id: ids.userEvent });
    passed = report("authenticated-event-update-denied", !authenticatedUpdate.ok) && passed;
    passed = report("authenticated-event-delete-denied", !authenticatedDelete.ok) && passed;
    const serviceUpdate = await write(env, "audit_trail_events", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { method: "PATCH", id: ids.userEvent, body: { action: "document.created" } });
    const serviceDelete = await write(env, "audit_trail_events", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { method: "DELETE", id: ids.userEvent });
    passed = report("service-role-event-update-denied", !serviceUpdate.ok) && passed;
    passed = report("service-role-event-delete-denied", !serviceDelete.ok) && passed;
    const fingerprintAfter = await sql(container, `select md5(action || ':' || metadata::text || ':' || correlation_id::text) from public.audit_trail_events where id = '${ids.userEvent}'`);
    passed = report("event-unchanged-after-mutation-attempts", fingerprintBefore.length > 0 && fingerprintBefore === fingerprintAfter) && passed;

    const versionEvents = await sql(container, `select count(*) from public.audit_trail_events where resource_id = '${ids.version}' and action = 'document.version_created'`);
    passed = report("document-version-trigger-event-created", versionEvents === "1") && passed;
    const processing = await write(env, "audit_jobs", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { method: "PATCH", id: ids.audit, body: { status: "Processing", started_at: new Date().toISOString() } });
    const cancelled = await write(env, "audit_jobs", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { method: "PATCH", id: ids.audit, body: { status: "Cancelled", completed_at: new Date(Date.now() + 10).toISOString() } });
    passed = report("audit-status-trigger-events-created", processing.ok && cancelled.ok && await sql(container, `select count(*) from public.audit_trail_events where resource_id = '${ids.audit}' and action in ('audit.processing_started','audit.cancelled')`) === "2") && passed;
    const retryCancelled = await write(env, "audit_jobs", env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY, { method: "PATCH", id: ids.audit, body: { status: "Cancelled" } });
    const terminalCount = await sql(container, `select count(*) from public.audit_trail_events where resource_id = '${ids.audit}' and action = 'audit.cancelled'`);
    passed = report("terminal-event-retry-not-duplicated", !retryCancelled.ok && terminalCount === "1") && passed;

    const forbiddenCount = await sql(container, "select count(*) from public.audit_trail_events where metadata ?| array['token','secret','connectionString','signedUrl','storagePath','documentText','exception','stackTrace']");
    passed = report("persisted-events-contain-no-forbidden-metadata", forbiddenCount === "0") && passed;
    const anonRead = await request(`${env.API_URL}/rest/v1/audit_trail_events?select=id`, { headers: headers(env.PUBLISHABLE_KEY) });
    const authenticatedRead = await request(`${env.API_URL}/rest/v1/audit_trail_events?select=id`, { headers: headers(env.PUBLISHABLE_KEY, userToken) });
    passed = report("anon-event-read-denied", !anonRead.ok) && passed;
    passed = report("authenticated-event-read-denied", !authenticatedRead.ok) && passed;
  } catch {
    passed = report("audit-trail-smoke-execution", false) && passed;
  } finally {
    let cleaned = true;
    try { await sql(container, cleanupSql); } catch { cleaned = false; }
    try { await removeUser(env); } catch { cleaned = false; }
    try {
      const acl = await sql(container, "select has_table_privilege('service_role','public.audit_trail_events','insert') and not has_table_privilege('service_role','public.audit_trail_events','select') and not has_table_privilege('service_role','public.audit_trail_events','update') and not has_table_privilege('service_role','public.audit_trail_events','delete')");
      if (acl !== "t") cleaned = false;
    } catch { cleaned = false; }
    report("database-owner-audit-cleanup", cleaned);
    report("audit-trail-acl-restored", cleaned);
    passed = cleaned && passed;
  }

  process.exitCode = passed ? 0 : 1;
}

main();
