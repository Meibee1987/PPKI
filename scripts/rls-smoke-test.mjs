import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import path from "node:path";

const fixture = Object.freeze({
  ruleId: "90000000-0000-0000-0000-000000000001",
  documentAId: "90000000-0000-0000-0000-000000000002",
  versionAId: "90000000-0000-0000-0000-000000000003",
  auditAId: "90000000-0000-0000-0000-000000000004",
  findingAId: "90000000-0000-0000-0000-000000000005",
  documentBId: "90000000-0000-0000-0000-000000000006",
  versionBId: "90000000-0000-0000-0000-000000000007",
  auditBId: "90000000-0000-0000-0000-000000000008",
  findingBId: "90000000-0000-0000-0000-000000000009",
  documentTypeId: "10000000-0000-0000-0000-000000000002",
  profileVersionId: "21000000-0000-0000-0000-000000000001",
});

const users = Object.freeze([
  { email: "user-a@example.invalid", password: "Synthetic-passphrase-01" },
  { email: "user-b@example.invalid", password: "Synthetic-passphrase-01" },
]);

function run(command, args, { capture = false, allowFailure = false } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.resume();
    child.on("error", () => reject(new Error("local command could not start")));
    child.on("close", (code) => {
      if (code === 0 || allowFailure) resolve(capture ? stdout : undefined);
      else reject(new Error("local command failed"));
    });
  });
}

function parseEnvironment(output) {
  const values = new Map();
  for (const line of output.split(/\r?\n/)) {
    const separator = line.indexOf("=");
    if (separator <= 0) continue;
    values.set(line.slice(0, separator), line.slice(separator + 1).replace(/^"|"$/g, ""));
  }
  return values;
}

async function localEnvironment() {
  const command = process.platform === "win32" ? process.execPath : "npx";
  const args = process.platform === "win32"
    ? [path.join(path.dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js"), "exec", "--", "supabase", "status", "-o", "env"]
    : ["supabase", "status", "-o", "env"];
  const values = parseEnvironment(await run(command, args, { capture: true, allowFailure: true }));
  const required = ["API_URL", "PUBLISHABLE_KEY", "SERVICE_ROLE_KEY"];
  if (required.some((name) => !values.get(name))) throw new Error("local stack is unavailable");
  return Object.fromEntries(required.map((name) => [name, values.get(name)]));
}

async function projectId() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const match = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m);
  if (!match) throw new Error("local project configuration is invalid");
  return match[1];
}

async function databaseContainer(id) {
  const output = await run("docker", ["ps", "--filter", `name=supabase_db_${id}`, "--format", "{{.Names}}"], { capture: true });
  const container = output.split(/\r?\n/).find(Boolean);
  if (!container) throw new Error("local database is unavailable");
  return container;
}

async function request(url, options = {}) {
  try {
    return await fetch(url, options);
  } catch {
    throw new Error("local Data API is unavailable");
  }
}

function headers(apiKey, token = undefined) {
  return {
    apikey: apiKey,
    ...(token ? { authorization: `Bearer ${token}` } : {}),
  };
}

async function json(response) {
  if (!response.ok) throw new Error("local admin request failed");
  return response.json();
}

async function deleteSyntheticUser(environment, email) {
  const list = await json(await request(`${environment.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, {
    headers: headers(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY),
  }));
  const existing = list.users?.find((user) => user.email === email);
  if (existing) {
    const response = await request(`${environment.API_URL}/auth/v1/admin/users/${existing.id}`, {
      method: "DELETE",
      headers: headers(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY),
    });
    if (!response.ok) throw new Error("synthetic user cleanup failed");
  }
}

async function createUser(environment, user) {
  const response = await request(`${environment.API_URL}/auth/v1/admin/users`, {
    method: "POST",
    headers: { ...headers(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY), "content-type": "application/json" },
    body: JSON.stringify({ email: user.email, password: user.password, email_confirm: true }),
  });
  return json(response);
}

async function signIn(environment, user) {
  const response = await request(`${environment.API_URL}/auth/v1/token?grant_type=password`, {
    method: "POST",
    headers: { ...headers(environment.PUBLISHABLE_KEY), "content-type": "application/json" },
    body: JSON.stringify({ email: user.email, password: user.password }),
  });
  const session = await json(response);
  if (!session.access_token) throw new Error("local sign-in failed");
  return session.access_token;
}

function sql(userAId, userBId) {
  return `
insert into public.rules (id, rule_code, domain, applies_to, element, official_requirement, expected_value_pattern, severity, fix_mode, validation_key, is_implemented)
values ('${fixture.ruleId}', 'TEST-RLS-001', 'TEST', 'Smoke', 'Synthetic', 'Synthetic local fixture', 'Synthetic', 'Info', 'Manual', 'test.rls', false);
insert into public.documents (id, owner_user_id, document_type_id, title, current_version_no) values
  ('${fixture.documentAId}', '${userAId}', '${fixture.documentTypeId}', 'RLS synthetic document A', 1),
  ('${fixture.documentBId}', '${userBId}', '${fixture.documentTypeId}', 'RLS synthetic document B', 1);
insert into public.document_versions (id, document_id, version_no, storage_bucket, storage_key, original_filename, mime_type, size_bytes, sha256, created_by_user_id) values
  ('${fixture.versionAId}', '${fixture.documentAId}', 1, 'documents-original', 'rls-smoke/a/v1.docx', 'rls-smoke-a.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1, '${"a".repeat(64)}', '${userAId}'),
  ('${fixture.versionBId}', '${fixture.documentBId}', 1, 'documents-original', 'rls-smoke/b/v1.docx', 'rls-smoke-b.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1, '${"b".repeat(64)}', '${userBId}');
insert into public.audit_jobs (id, document_version_id, profile_version_id, requested_by_user_id, status) values
  ('${fixture.auditAId}', '${fixture.versionAId}', '${fixture.profileVersionId}', '${userAId}', 'Queued'),
  ('${fixture.auditBId}', '${fixture.versionBId}', '${fixture.profileVersionId}', '${userBId}', 'Queued');
update public.audit_jobs
set status = 'Processing', started_at = now()
where id in ('${fixture.auditAId}', '${fixture.auditBId}');
insert into public.audit_findings (id, audit_job_id, rule_id, severity, rule_code_snapshot, fix_mode_snapshot, message, actual_value, expected_value, location) values
  ('${fixture.findingAId}', '${fixture.auditAId}', '${fixture.ruleId}', 'Info', 'TEST-RLS-001', 'Manual', 'Synthetic finding A', '{}', '{}', '{}'),
  ('${fixture.findingBId}', '${fixture.auditBId}', '${fixture.ruleId}', 'Info', 'TEST-RLS-001', 'Manual', 'Synthetic finding B', '{}', '{}', '{}');`;
}

const cleanupSql = `
delete from public.audit_trail_events where resource_id in ('${fixture.documentAId}', '${fixture.versionAId}', '${fixture.auditAId}', '${fixture.documentBId}', '${fixture.versionBId}', '${fixture.auditBId}');
delete from public.audit_findings where id in ('${fixture.findingAId}', '${fixture.findingBId}');
delete from public.audit_jobs where id in ('${fixture.auditAId}', '${fixture.auditBId}');
delete from public.document_versions where id in ('${fixture.versionAId}', '${fixture.versionBId}');
delete from public.documents where id in ('${fixture.documentAId}', '${fixture.documentBId}');
delete from public.rules where id = '${fixture.ruleId}';`;

async function executeSql(container, statement) {
  await run("docker", ["exec", container, "psql", "-q", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement]);
}

function report(name, passed) {
  console.log(`${name}: ${passed ? "PASS" : "FAIL"}`);
  return passed;
}

async function visibleIds(environment, token, table, ids) {
  const response = await request(`${environment.API_URL}/rest/v1/${table}?id=in.(${ids.join(",")})&select=id`, {
    headers: headers(environment.PUBLISHABLE_KEY, token),
  });
  if (!response.ok) return null;
  return (await response.json()).map((row) => row.id).sort();
}

async function main() {
  let environment;
  let container;
  let createdUsers = [];
  let setupComplete = false;
  let passed = true;

  try {
    environment = await localEnvironment();
    passed = report("local-supabase-status", true) && passed;
  } catch {
    report("local-supabase-status", false);
    report("synthetic-fixture-cleanup", true);
    process.exitCode = 1;
    return;
  }

  try {
    container = await databaseContainer(await projectId());
    passed = report("local-database-ready", true) && passed;
  } catch {
    report("local-database-ready", false);
    report("synthetic-fixture-cleanup", true);
    process.exitCode = 1;
    return;
  }

  try {
    await executeSql(container, cleanupSql);
    for (const user of users) await deleteSyntheticUser(environment, user.email);
    createdUsers = await Promise.all(users.map((user) => createUser(environment, user)));
    const [tokenA, tokenB] = await Promise.all(users.map((user) => signIn(environment, user)));
    await executeSql(container, sql(createdUsers[0].id, createdUsers[1].id));
    setupComplete = true;
    passed = report("fixture-created", true) && passed;

    const anonDocuments = await request(`${environment.API_URL}/rest/v1/documents?select=id`, { headers: headers(environment.PUBLISHABLE_KEY) });
    passed = report("anon-documents-denied", !anonDocuments.ok) && passed;

    const ownershipAssertions = [
      ["documents", fixture.documentAId, fixture.documentBId],
      ["document_versions", fixture.versionAId, fixture.versionBId],
      ["audit_jobs", fixture.auditAId, fixture.auditBId],
      ["audit_findings", fixture.findingAId, fixture.findingBId],
    ];
    for (const [table, idA, idB] of ownershipAssertions) {
      const [visibleA, visibleB] = await Promise.all([
        visibleIds(environment, tokenA, table, [idA, idB]),
        visibleIds(environment, tokenB, table, [idA, idB]),
      ]);
      passed = report(`${table}-owner-a-isolated`, JSON.stringify(visibleA) === JSON.stringify([idA])) && passed;
      passed = report(`${table}-owner-b-isolated`, JSON.stringify(visibleB) === JSON.stringify([idB])) && passed;
    }

    const blockedDocumentInsert = await request(`${environment.API_URL}/rest/v1/documents`, {
      method: "POST",
      headers: { ...headers(environment.PUBLISHABLE_KEY, tokenA), "content-type": "application/json" },
      body: JSON.stringify({ owner_user_id: createdUsers[0].id, document_type_id: fixture.documentTypeId, title: "Blocked direct write", current_version_no: 1 }),
    });
    passed = report("authenticated-document-insert-denied", !blockedDocumentInsert.ok) && passed;

    const blockedOwnerUpdate = await request(`${environment.API_URL}/rest/v1/documents?id=eq.${fixture.documentAId}`, {
      method: "PATCH",
      headers: { ...headers(environment.PUBLISHABLE_KEY, tokenA), "content-type": "application/json" },
      body: JSON.stringify({ owner_user_id: createdUsers[1].id }),
    });
    passed = report("authenticated-owner-update-denied", !blockedOwnerUpdate.ok) && passed;

    const blockedStatusUpdate = await request(`${environment.API_URL}/rest/v1/audit_jobs?id=eq.${fixture.auditAId}`, {
      method: "PATCH",
      headers: { ...headers(environment.PUBLISHABLE_KEY, tokenA), "content-type": "application/json" },
      body: JSON.stringify({ status: "Cancelled" }),
    });
    passed = report("authenticated-audit-status-update-denied", !blockedStatusUpdate.ok) && passed;

    const blockedDocumentDelete = await request(`${environment.API_URL}/rest/v1/documents?id=eq.${fixture.documentAId}`, {
      method: "DELETE",
      headers: headers(environment.PUBLISHABLE_KEY, tokenA),
    });
    passed = report("authenticated-document-delete-denied", !blockedDocumentDelete.ok) && passed;

    const allowedReference = await request(`${environment.API_URL}/rest/v1/document_types?select=id`, { headers: headers(environment.PUBLISHABLE_KEY, tokenA) });
    passed = report("authenticated-reference-read-allowed", allowedReference.ok) && passed;
    for (const table of ["rules", "profile_rules"]) {
      const response = await request(`${environment.API_URL}/rest/v1/${table}?select=id`, { headers: headers(environment.PUBLISHABLE_KEY, tokenA) });
      passed = report(`${table}-direct-read-denied`, !response.ok) && passed;
    }
  } catch {
    passed = report("rls-smoke-execution", false) && passed;
  } finally {
    let cleanupPassed = true;
    if (container) {
      try { await executeSql(container, cleanupSql); } catch { cleanupPassed = false; }
    }
    if (environment) {
      for (const user of users) {
        try { await deleteSyntheticUser(environment, user.email); } catch { cleanupPassed = false; }
      }
    }
    report("synthetic-fixture-cleanup", cleanupPassed);
    passed = cleanupPassed && passed;
  }

  process.exitCode = passed ? 0 : 1;
}

main();
