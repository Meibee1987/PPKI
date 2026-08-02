import { createHash, randomUUID } from "node:crypto";
import { createWriteStream } from "node:fs";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";

const suiteVersion = "1.0.0";
const docxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
const buckets = ["documents-original", "documents-versions", "audit-reports"];
const identities = [
  { email: "user-a@example.invalid", label: "a" },
  { email: "user-b@example.invalid", label: "b" },
];
const assertions = [];
const responseSamples = [];
const processes = new Set();
let logDirectory;

console.log("SUITE security-integration-local");

function assertResult(component, name, passed) {
  const result = { component, name, passed: Boolean(passed) };
  assertions.push(result);
  console.log(`ASSERT ${name} ${result.passed ? "PASS" : "FAIL"}`);
  return result.passed;
}

function requireResult(component, name, passed) {
  if (!assertResult(component, name, passed)) throw new Error("integration assertion failed");
}

function isLocalHost(hostname) {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "::1";
}

function encodePath(value) {
  return value.split("/").map(encodeURIComponent).join("/");
}

function uuid(value) {
  return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function run(command, args, { allowFailure = false, timeoutMs = 120000, env = process.env } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), shell: false, env, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    let timedOut = false;
    const timeout = setTimeout(() => {
      timedOut = true;
      child.kill("SIGKILL");
    }, timeoutMs);
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("error", () => {
      clearTimeout(timeout);
      reject(new Error("local command could not start"));
    });
    child.on("close", (code) => {
      clearTimeout(timeout);
      if ((!timedOut && code === 0) || allowFailure) resolve({ code, stdout, stderr, timedOut });
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
  const result = await run(command, args, { allowFailure: true, timeoutMs: 30000 });
  const values = parseEnvironment(result.stdout);
  const required = ["API_URL", "DB_URL", "PUBLISHABLE_KEY", "SECRET_KEY", "SERVICE_ROLE_KEY"];
  if (result.code !== 0 || required.some((name) => !values.get(name))) throw new Error("local stack unavailable");
  const api = new URL(values.get("API_URL"));
  const database = new URL(values.get("DB_URL"));
  if (api.protocol !== "http:" || !isLocalHost(api.hostname)
      || !["postgres:", "postgresql:"].includes(database.protocol) || !isLocalHost(database.hostname)) {
    throw new Error("non-local target rejected");
  }
  return Object.fromEntries(required.map((name) => [name, values.get(name)]));
}

async function localProjectId() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const match = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m);
  if (!match) throw new Error("local project configuration invalid");
  return match[1];
}

async function localDatabaseContainer(projectId) {
  const result = await run("docker", ["ps", "--filter", `name=supabase_db_${projectId}`, "--format", "{{.Names}}"], { timeoutMs: 30000 });
  const exact = result.stdout.split(/\r?\n/).find((name) => name === `supabase_db_${projectId}`);
  if (!exact) throw new Error("local database unavailable");
  return exact;
}

function databaseConnectionString(databaseUrl) {
  const parsed = new URL(databaseUrl);
  const database = decodeURIComponent(parsed.pathname.replace(/^\//, ""));
  return `Host=${parsed.hostname};Port=${parsed.port};Database=${database};Username=${decodeURIComponent(parsed.username)};Password=${decodeURIComponent(parsed.password)};SSL Mode=Disable;Include Error Detail=false`;
}

async function sql(container, statement) {
  const result = await run("docker", ["exec", container, "psql", "-qAt", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement], { timeoutMs: 60000 });
  return result.stdout.trim();
}

async function fetchLocal(url, options = {}) {
  const parsed = new URL(url);
  if (!isLocalHost(parsed.hostname)) throw new Error("non-local request rejected");
  return fetch(url, options);
}

function supabaseHeaders(apiKey, token, json = false) {
  return {
    apikey: apiKey,
    ...(token ? { authorization: `Bearer ${token}` } : {}),
    ...(json ? { "content-type": "application/json" } : {}),
  };
}

async function body(response, collect = false) {
  const text = await response.text();
  if (collect) responseSamples.push(text);
  if (!text) return null;
  try { return JSON.parse(text); } catch { return null; }
}

async function authUsers(environment) {
  const response = await fetchLocal(`${environment.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, {
    headers: supabaseHeaders(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY),
  });
  if (!response.ok) throw new Error("local user lookup failed");
  return (await response.json()).users ?? [];
}

async function deleteAuthUser(environment, id) {
  const response = await fetchLocal(`${environment.API_URL}/auth/v1/admin/users/${id}`, {
    method: "DELETE",
    headers: supabaseHeaders(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY),
  });
  if (!response.ok && response.status !== 404) throw new Error("local user cleanup failed");
}

async function createAuthUser(environment, identity, password) {
  const response = await fetchLocal(`${environment.API_URL}/auth/v1/admin/users`, {
    method: "POST",
    headers: supabaseHeaders(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY, true),
    body: JSON.stringify({ email: identity.email, password, email_confirm: true, user_metadata: { full_name: `Synthetic User ${identity.label.toUpperCase()}` } }),
  });
  const json = await body(response);
  if (!response.ok || !uuid(json?.id)) throw new Error("local user creation failed");
  return json.id;
}

async function signIn(environment, identity, password) {
  const response = await fetchLocal(`${environment.API_URL}/auth/v1/token?grant_type=password`, {
    method: "POST",
    headers: supabaseHeaders(environment.PUBLISHABLE_KEY, undefined, true),
    body: JSON.stringify({ email: identity.email, password }),
  });
  const json = await body(response);
  if (!response.ok || typeof json?.access_token !== "string") throw new Error("local sign-in failed");
  return json.access_token;
}

async function deleteStorageObject(environment, bucket, objectPath) {
  const response = await fetchLocal(`${environment.API_URL}/storage/v1/object/${encodeURIComponent(bucket)}/${encodePath(objectPath)}`, {
    method: "DELETE",
    headers: supabaseHeaders(environment.SECRET_KEY),
  });
  if (!response.ok && response.status !== 404) throw new Error("storage cleanup failed");
}

async function cleanupOwners(environment, container, ownerIds) {
  if (ownerIds.length === 0) return;
  const quoted = ownerIds.map((id) => `'${id}'::uuid`).join(",");
  const objects = await sql(container, `select bucket_id || chr(9) || name from storage.objects where name ~ '^(${ownerIds.join("|")})/'`);
  for (const line of objects.split(/\r?\n/).filter(Boolean)) {
    const separator = line.indexOf("\t");
    if (separator > 0) await deleteStorageObject(environment, line.slice(0, separator), line.slice(separator + 1));
  }
  await sql(container, `
delete from public.audit_trail_events where owner_user_id in (${quoted}) or actor_user_id in (${quoted});
delete from public.audit_findings where audit_job_id in (select audit.id from public.audit_jobs audit join public.document_versions version on version.id=audit.document_version_id join public.documents document on document.id=version.document_id where document.owner_user_id in (${quoted}));
delete from public.audit_rule_snapshots where audit_job_id in (select audit.id from public.audit_jobs audit join public.document_versions version on version.id=audit.document_version_id join public.documents document on document.id=version.document_id where document.owner_user_id in (${quoted}));
delete from public.audit_jobs where document_version_id in (select version.id from public.document_versions version join public.documents document on document.id=version.document_id where document.owner_user_id in (${quoted}));
delete from public.document_versions where document_id in (select id from public.documents where owner_user_id in (${quoted}));
delete from public.documents where owner_user_id in (${quoted});
delete from public.user_profiles where id in (${quoted});`);
}

async function cleanupSynthetic(environment, container) {
  const existing = (await authUsers(environment)).filter((candidate) => identities.some((identity) => identity.email === candidate.email));
  await cleanupOwners(environment, container, existing.map((candidate) => candidate.id).filter(uuid));
  for (const candidate of existing) await deleteAuthUser(environment, candidate.id);
}

async function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.unref();
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      server.close(() => resolve(address.port));
    });
  });
}

function backendEnvironment(environment, port) {
  return {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: "SecurityIntegrationTest",
    DOTNET_ENVIRONMENT: "SecurityIntegrationTest",
    ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    ConnectionStrings__Database: databaseConnectionString(environment.DB_URL),
    Supabase__Url: environment.API_URL,
    Supabase__PublishableKey: environment.PUBLISHABLE_KEY,
    Supabase__SecretKey: environment.SECRET_KEY,
    Supabase__Storage__OriginalBucket: buckets[0],
    Supabase__Storage__VersionBucket: buckets[1],
    Supabase__Storage__ReportBucket: buckets[2],
    Supabase__Storage__SignedUrlLifetimeSeconds: "120",
    HealthChecks__TimeoutSeconds: "3",
    Worker__PollSeconds: "1",
    RuleCatalog__Path: path.join(process.cwd(), "rules", "ppki-ipb-2019", "rules.json"),
    Logging__LogLevel__Default: "Information",
  };
}

function startBackend(name, dll, env) {
  const logPath = path.join(logDirectory, `${name}.log`);
  const stream = createWriteStream(logPath, { flags: "a" });
  const child = spawn("dotnet", [dll], { cwd: process.cwd(), shell: false, env, stdio: ["ignore", "pipe", "pipe"] });
  child.stdout.pipe(stream);
  child.stderr.pipe(stream);
  const closed = new Promise((resolve) => child.once("close", (code) => resolve(code)));
  const entry = { name, child, closed, stream, logPath };
  processes.add(entry);
  return entry;
}

async function stopBackend(entry) {
  if (!entry) return;
  if (entry.child.exitCode === null) entry.child.kill("SIGTERM");
  await Promise.race([entry.closed, delay(5000)]);
  if (entry.child.exitCode === null) entry.child.kill("SIGKILL");
  await Promise.race([entry.closed, delay(3000)]);
  entry.stream.end();
  processes.delete(entry);
}

async function waitForHealth(baseUrl, route, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetchLocal(`${baseUrl}${route}`);
      if (response.ok) return true;
    } catch { }
    await delay(250);
  }
  return false;
}

async function waitForLog(entry, text, timeoutMs = 15000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (entry.child.exitCode !== null) return false;
    try {
      if ((await readFile(entry.logPath, "utf8")).includes(text)) return true;
    } catch { }
    await delay(200);
  }
  return false;
}

function bearer(token) {
  return { authorization: `Bearer ${token}` };
}

async function apiRequest(baseUrl, route, token, options = {}) {
  return fetchLocal(`${baseUrl}${route}`, {
    ...options,
    headers: { ...bearer(token), ...(options.headers ?? {}) },
  });
}

async function uploadDocument(baseUrl, token, fixture, title, spoofOwner) {
  const form = new FormData();
  form.append("title", title);
  form.append("documentTypeCode", "SKRIPSI");
  if (spoofOwner) {
    form.append("owner_user_id", spoofOwner);
    form.append("storage_key", "../forbidden.docx");
  }
  form.append("file", new Blob([await readFile(fixture)], { type: docxMime }), "../../synthetic-upload.docx");
  const response = await apiRequest(baseUrl, "/api/documents", token, { method: "POST", body: form });
  return { response, json: await body(response, !response.ok) };
}

async function requestAudit(baseUrl, token, versionId) {
  const response = await apiRequest(baseUrl, `/api/document-versions/${versionId}/audits`, token, { method: "POST" });
  return { response, json: await body(response, !response.ok) };
}

async function waitForAudit(baseUrl, token, auditId, statuses, timeoutMs = 45000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const response = await apiRequest(baseUrl, `/api/audits/${auditId}`, token);
    const json = await body(response, !response.ok);
    if (response.ok && statuses.includes(json?.status)) return json;
    await delay(250);
  }
  return null;
}

async function expectMaskedNotFound(baseUrl, route, token, forbidden) {
  const response = await apiRequest(baseUrl, route, token, route.includes("/audits") && route.includes("document-versions") ? { method: "POST" } : {});
  const text = await response.text();
  responseSamples.push(text);
  return response.status === 404 && forbidden.every((value) => !text.includes(value));
}

async function dataRequest(environment, table, token, query = "select=id", options = {}) {
  return fetchLocal(`${environment.API_URL}/rest/v1/${table}?${query}`, {
    ...options,
    headers: {
      ...supabaseHeaders(environment.PUBLISHABLE_KEY, token, options.body !== undefined),
      ...(options.headers ?? {}),
    },
  });
}

async function serviceDataRequest(environment, table, query, options = {}) {
  return fetchLocal(`${environment.API_URL}/rest/v1/${table}?${query}`, {
    ...options,
    headers: {
      ...supabaseHeaders(environment.SERVICE_ROLE_KEY, environment.SERVICE_ROLE_KEY, options.body !== undefined),
      prefer: "return=minimal",
      ...(options.headers ?? {}),
    },
  });
}

async function createFault(container, name, table, condition, action) {
  await sql(container, `
create or replace function private.${name}() returns trigger language plpgsql set search_path='' as $$
begin
  if ${condition} then ${action}; end if;
  return new;
end;
$$;
drop trigger if exists ${name} on public.${table};
create trigger ${name} before insert on public.${table} for each row execute function private.${name}();`);
}

async function dropFaults(container) {
  await sql(container, `
drop trigger if exists s1t06_fail_document_insert on public.documents;
drop trigger if exists s1t06_fail_snapshot_insert on public.audit_rule_snapshots;
drop trigger if exists s1t06_fail_finding_insert on public.audit_findings;
drop trigger if exists s1t06_pause_snapshot_insert on public.audit_rule_snapshots;
drop function if exists private.s1t06_fail_document_insert();
drop function if exists private.s1t06_fail_snapshot_insert();
drop function if exists private.s1t06_fail_finding_insert();
drop function if exists private.s1t06_pause_snapshot_insert();`);
}

async function startWorker(name, workerDll, backendEnv) {
  const worker = startBackend(name, workerDll, backendEnv);
  requireResult("process", `${name}-process-ready`, await waitForLog(worker, "Worker startup completed"));
  return worker;
}

async function scanLogsAndResponses(environment, password, signedUrl) {
  const logs = [];
  for (const entry of await readdir(logDirectory)) logs.push(await readFile(path.join(logDirectory, entry), "utf8"));
  const combined = `${logs.join("\n")}\n${responseSamples.join("\n")}`;
  const exactSecrets = [environment.PUBLISHABLE_KEY, environment.SECRET_KEY, environment.SERVICE_ROLE_KEY, password, signedUrl].filter(Boolean);
  const hasExactSecret = exactSecrets.some((value) => combined.includes(value));
  const hasPattern = /sb_secret_|authorization:\s*bearer|[?&](token|signature)=|Host=[^;]+;Port=\d+;Database=/i.test(combined);
  const storagePattern = /[0-9a-f-]{36}\/[0-9a-f-]{36}\/[0-9a-f-]{36}\/(original|document)\.docx/i;
  const storageIndex = combined.search(storagePattern);
  const hasStoragePath = storageIndex >= 0;
  let storagePathCategory;
  if (hasStoragePath) {
    const prefix = combined.slice(Math.max(0, storageIndex - 3000), storageIndex);
    const categories = [...prefix.matchAll(/(?:info|warn|fail|dbug|trce):\s+([A-Za-z0-9_.]+)/g)];
    storagePathCategory = categories.at(-1)?.[1]?.toLowerCase().replace(/[^a-z0-9_.-]/g, "-");
  }
  return { passed: !hasExactSecret && !hasPattern && !hasStoragePath, hasExactSecret, hasPattern, hasStoragePath, storagePathCategory };
}

async function safeDiagnosticCodes() {
  if (!logDirectory) return [];
  const combined = (await Promise.all((await readdir(logDirectory)).map((entry) => readFile(path.join(logDirectory, entry), "utf8")))).join("\n");
  const codes = [];
  if (/DbUpdateException/.test(combined)) codes.push("db-update-exception");
  if (/PostgresException/.test(combined)) codes.push("postgres-exception");
  for (const code of ["23503", "23505", "23514", "42501", "42703", "42P01", "55000"]) {
    if (combined.includes(code)) codes.push(`sqlstate-${code.toLowerCase()}`);
  }
  if (/Storage upload failed/i.test(combined)) codes.push("storage-upload-failure");
  if (/Audit context requires/i.test(combined)) codes.push("audit-context-failure");
  const constraint = combined.match(/constraint ["']([a-z0-9_]+)["']/i)?.[1];
  if (constraint) codes.push(`constraint-${constraint.toLowerCase()}`);
  return [...new Set(codes)];
}

async function temporaryDocxNames() {
  return new Set((await readdir(os.tmpdir())).filter((name) => /^ppki-[0-9a-f]+\.docx$/i.test(name) || /^ppki-upload-[0-9a-f]+\.tmp$/i.test(name)));
}

async function componentSmoke(script) {
  const command = process.platform === "win32" ? process.execPath : "npm";
  const args = process.platform === "win32"
    ? [path.join(path.dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js"), "run", script]
    : ["run", script];
  const result = await run(command, args, { allowFailure: true, timeoutMs: 180000 });
  return result.code === 0 && !result.timedOut;
}

async function writeSummary(startedAt, cleanupPassed) {
  const totals = {
    total: assertions.length,
    passed: assertions.filter((item) => item.passed).length,
    failed: assertions.filter((item) => !item.passed).length,
  };
  const components = {};
  for (const item of assertions) {
    components[item.component] ??= { total: 0, passed: 0, failed: 0 };
    components[item.component].total += 1;
    components[item.component][item.passed ? "passed" : "failed"] += 1;
  }
  await mkdir(path.join(process.cwd(), "artifacts"), { recursive: true });
  await writeFile(path.join(process.cwd(), "artifacts", "security-integration-summary.json"), `${JSON.stringify({
    suiteVersion,
    timestamp: new Date().toISOString(),
    localOnly: true,
    durationMilliseconds: Date.now() - startedAt,
    totals,
    components,
    cleanupPassed,
  }, null, 2)}\n`, "utf8");
}

async function main() {
  const startedAt = Date.now();
  const password = `S1T06-${randomUUID()}-local`;
  const fixtureInvalid = path.join(process.cwd(), "backend", "tests", "fixtures", "docx", "generated", "minimal-invalid-layout.docx");
  const fixtureCompliant = path.join(process.cwd(), "backend", "tests", "fixtures", "docx", "generated", "minimal-compliant-layout.docx");
  const fixtureHash = createHash("sha256").update(await readFile(fixtureInvalid)).digest("hex");
  const tempBefore = await temporaryDocxNames();
  let environment;
  let container;
  let api;
  let apiBaseUrl;
  let backendEnv;
  let userIds = [];
  let tokens = [];
  let documentA;
  let documentB;
  let auditA;
  let signedUrl;
  let cleanupPassed = true;

  try {
    environment = await localEnvironment();
    container = await localDatabaseContainer(await localProjectId());
    requireResult("prerequisite", "local-supabase-target-verified", true);
    await dropFaults(container);
    await cleanupSynthetic(environment, container);
    requireResult("cleanup", "preexisting-synthetic-state-cleaned", true);

    const build = await run("dotnet", ["build", "backend/PpkiSmartFormatter.slnx", "--no-restore", "--nologo"], { allowFailure: true, timeoutMs: 180000 });
    requireResult("process", "backend-integration-binaries-built", build.code === 0 && !build.timedOut);
    logDirectory = await mkdtemp(path.join(os.tmpdir(), "ppki-security-integration-"));
    const port = await freePort();
    apiBaseUrl = `http://127.0.0.1:${port}`;
    backendEnv = backendEnvironment(environment, port);
    const apiDll = path.join(process.cwd(), "backend", "services", "Ppki.Api", "bin", "Debug", "net10.0", "Ppki.Api.dll");
    const workerDll = path.join(process.cwd(), "backend", "services", "Ppki.Worker", "bin", "Debug", "net10.0", "Ppki.Worker.dll");
    api = startBackend("api", apiDll, backendEnv);
    requireResult("process", "api-liveness-ready", await waitForHealth(apiBaseUrl, "/health/live"));
    requireResult("process", "api-dependency-readiness-ready", await waitForHealth(apiBaseUrl, "/health/ready"));

    userIds = await Promise.all(identities.map((identity) => createAuthUser(environment, identity, password)));
    tokens = await Promise.all(identities.map((identity) => signIn(environment, identity, password)));
    requireResult("identity", "synthetic-users-authenticated", userIds.every(uuid) && tokens.every(Boolean));
    for (const token of tokens) {
      const me = await apiRequest(apiBaseUrl, "/api/me", token);
      requireResult("identity", "api-principal-profile-established", me.ok);
      await body(me);
    }
    const anonymous = await fetchLocal(`${apiBaseUrl}/api/documents`);
    assertResult("authorization", "anonymous-api-access-denied", anonymous.status === 401);

    const uploadA = await uploadDocument(apiBaseUrl, tokens[0], fixtureInvalid, "Synthetic lifecycle document A");
    if (uploadA.response.status !== 201) assertResult("diagnostic", `user-a-document-upload-http-${uploadA.response.status}`, false);
    requireResult("lifecycle", "user-a-document-upload-created", uploadA.response.status === 201 && uuid(uploadA.json?.id) && uuid(uploadA.json?.versionId));
    documentA = uploadA.json;
    const uploadB = await uploadDocument(apiBaseUrl, tokens[1], fixtureCompliant, "Synthetic lifecycle document B", userIds[0]);
    requireResult("authorization", "spoofed-owner-input-ignored", uploadB.response.status === 201 && uuid(uploadB.json?.id) && uuid(uploadB.json?.versionId));
    documentB = uploadB.json;

    const ownerAndPath = await sql(container, `select count(*)=2 and bool_and(version.storage_key=(document.owner_user_id::text||'/'||document.id::text||'/'||version.id::text||'/original.docx')) and bool_and(version.sha256 ~ '^[0-9a-f]{64}$') and bool_and(version.size_bytes>0) from public.documents document join public.document_versions version on version.document_id=document.id where document.id in ('${documentA.id}','${documentB.id}') and ((document.id='${documentA.id}' and document.owner_user_id='${userIds[0]}') or (document.id='${documentB.id}' and document.owner_user_id='${userIds[1]}'));`);
    assertResult("lifecycle", "principal-owner-server-ids-checksum-size-and-canonical-path", ownerAndPath === "t");
    const leakedFilename = await sql(container, `select count(*) from public.document_versions where id in ('${documentA.versionId}','${documentB.versionId}') and storage_key like '%synthetic-upload%'`);
    assertResult("storage", "request-filename-cannot-influence-storage-path", leakedFilename === "0");

    for (const bucket of buckets) {
      const response = await fetchLocal(`${environment.API_URL}/storage/v1/bucket/${bucket}`, { headers: supabaseHeaders(environment.SECRET_KEY) });
      const config = response.ok ? await response.json() : null;
      assertResult("storage", `${bucket}-bucket-private`, config?.public === false);
    }

    const workerA = await startWorker("worker-a", workerDll, backendEnv);
    const workerB = await startWorker("worker-b", workerDll, backendEnv);
    const requested = await requestAudit(apiBaseUrl, tokens[0], documentA.versionId);
    requireResult("lifecycle", "audit-request-queued-through-api", requested.response.status === 202 && requested.json?.status === "Queued" && uuid(requested.json?.id));
    auditA = requested.json;
    const completed = await waitForAudit(apiBaseUrl, tokens[0], auditA.id, ["Completed", "Failed"]);
    requireResult("lifecycle", "worker-completes-owner-audit", completed?.status === "Completed");
    const snapshotCount = Number(await sql(container, `select count(*) from public.audit_rule_snapshots where audit_job_id='${auditA.id}'`));
    assertResult("lifecycle", "resolved-snapshot-hash-and-count-consistent", snapshotCount > 0 && completed.totalRules === snapshotCount && /^[0-9a-f]{64}$/.test(completed.resolvedRuleSetHash ?? ""));
    const findingsResponse = await apiRequest(apiBaseUrl, `/api/audits/${auditA.id}/findings`, tokens[0]);
    const findings = await body(findingsResponse);
    requireResult("lifecycle", "audit-findings-returned-to-owner", findingsResponse.ok && Array.isArray(findings) && findings.length > 0);

    const forbidden = [documentA.id, documentA.versionId, auditA.id, "Synthetic lifecycle document A", documentA.sha256];
    const crossChecks = [
      ["cross-user-document-detail-masked", `/api/documents/${documentA.id}`, "GET"],
      ["cross-user-audit-request-masked", `/api/document-versions/${documentA.versionId}/audits`, "POST"],
      ["cross-user-audit-status-masked", `/api/audits/${auditA.id}`, "GET"],
      ["cross-user-findings-masked", `/api/audits/${auditA.id}/findings`, "GET"],
      ["cross-user-download-masked", `/api/document-versions/${documentA.versionId}/download`, "GET"],
    ];
    for (const [name, route, method] of crossChecks) {
      const response = await apiRequest(apiBaseUrl, route, tokens[1], { method });
      const text = await response.text();
      responseSamples.push(text);
      assertResult("authorization", name, response.status === 404 && forbidden.every((value) => !text.includes(value)));
    }
    const bListResponse = await apiRequest(apiBaseUrl, "/api/documents", tokens[1]);
    const bList = await body(bListResponse);
    assertResult("authorization", "cross-user-list-exposes-only-own-document", bListResponse.ok && Array.isArray(bList) && bList.some((item) => item.id === documentB.id) && !bList.some((item) => item.id === documentA.id));
    const serviceRead = await serviceDataRequest(environment, "documents", `id=eq.${documentA.id}&select=id`);
    await body(serviceRead);
    const databaseRole = decodeURIComponent(new URL(environment.DB_URL).username);
    const documentTableOwner = await sql(container, "select tableowner from pg_catalog.pg_tables where schemaname='public' and tablename='documents'");
    assertResult("authorization", "service-role-data-api-follows-explicit-table-grants", !serviceRead.ok);
    assertResult("authorization", "api-authorization-enforced-above-database-owner-rls-bypass", databaseRole === documentTableOwner);

    const dataA = await dataRequest(environment, "documents", tokens[0], `id=in.(${documentA.id},${documentB.id})&select=id`);
    const dataB = await dataRequest(environment, "documents", tokens[1], `id=in.(${documentA.id},${documentB.id})&select=id`);
    const rowsA = await body(dataA);
    const rowsB = await body(dataB);
    assertResult("data-api", "data-api-document-cross-user-isolation", dataA.ok && dataB.ok && rowsA?.length === 1 && rowsA[0].id === documentA.id && rowsB?.length === 1 && rowsB[0].id === documentB.id);
    const snapshotA = await dataRequest(environment, "audit_rule_snapshots", tokens[0], `audit_job_id=eq.${auditA.id}&select=id`);
    const snapshotB = await dataRequest(environment, "audit_rule_snapshots", tokens[1], `audit_job_id=eq.${auditA.id}&select=id`);
    const snapshotRowsA = await body(snapshotA);
    const snapshotRowsB = await body(snapshotB);
    assertResult("data-api", "data-api-snapshot-ownership-chain", snapshotA.ok && snapshotB.ok && snapshotRowsA?.length === snapshotCount && snapshotRowsB?.length === 0);
    const trailRead = await dataRequest(environment, "audit_trail_events", tokens[0]);
    assertResult("data-api", "audit-trail-remains-server-only", !trailRead.ok);
    const directWrite = await dataRequest(environment, "documents", tokens[1], `id=eq.${documentA.id}`, { method: "PATCH", body: JSON.stringify({ owner_user_id: userIds[1] }) });
    assertResult("data-api", "authenticated-direct-write-remains-denied", !directWrite.ok);
    const referenceRead = await dataRequest(environment, "document_types", tokens[0]);
    const ruleRead = await dataRequest(environment, "rules", tokens[0]);
    const assignmentRead = await dataRequest(environment, "profile_rules", tokens[0]);
    assertResult("data-api", "reference-grants-remain-least-privilege", referenceRead.ok && !ruleRead.ok && !assignmentRead.ok);

    const objectPath = `${userIds[0]}/${documentA.id}/${documentA.versionId}/original.docx`;
    const directObjectUrl = `${environment.API_URL}/storage/v1/object/authenticated/${buckets[0]}/${encodePath(objectPath)}`;
    const [anonRead, userARead, userBRead] = await Promise.all([
      fetchLocal(directObjectUrl, { headers: supabaseHeaders(environment.PUBLISHABLE_KEY) }),
      fetchLocal(directObjectUrl, { headers: supabaseHeaders(environment.PUBLISHABLE_KEY, tokens[0]) }),
      fetchLocal(directObjectUrl, { headers: supabaseHeaders(environment.PUBLISHABLE_KEY, tokens[1]) }),
    ]);
    assertResult("storage", "direct-storage-read-denied-for-browser-principals", !anonRead.ok && !userARead.ok && !userBRead.ok);
    const directUpload = await fetchLocal(`${environment.API_URL}/storage/v1/object/${buckets[0]}/${encodePath(`${userIds[0]}/${randomUUID()}/${randomUUID()}/original.docx`)}`, {
      method: "POST", headers: { ...supabaseHeaders(environment.PUBLISHABLE_KEY, tokens[0]), "content-type": docxMime, "x-upsert": "false" }, body: new Uint8Array([1]),
    });
    const directDelete = await fetchLocal(`${environment.API_URL}/storage/v1/object/${buckets[0]}/${encodePath(objectPath)}`, { method: "DELETE", headers: supabaseHeaders(environment.PUBLISHABLE_KEY, tokens[0]) });
    assertResult("storage", "authenticated-storage-write-delete-denied", !directUpload.ok && !directDelete.ok);

    const download = await apiRequest(apiBaseUrl, `/api/document-versions/${documentA.versionId}/download`, tokens[0]);
    const downloadJson = await body(download, !download.ok);
    signedUrl = downloadJson?.url;
    requireResult("lifecycle", "owner-download-signed-url-issued", download.ok && typeof signedUrl === "string" && isLocalHost(new URL(signedUrl).hostname));
    const signedRead = await fetchLocal(signedUrl);
    const signedBytes = signedRead.ok ? Buffer.from(await signedRead.arrayBuffer()) : Buffer.alloc(0);
    assertResult("lifecycle", "signed-url-reads-original-before-expiry", signedRead.ok && createHash("sha256").update(signedBytes).digest("hex") === fixtureHash);

    const eventActions = (await sql(container, `select action from public.audit_trail_events where owner_user_id='${userIds[0]}' order by action`)).split(/\r?\n/).filter(Boolean);
    const expectedEvents = ["document.created", "document.version_created", "document.upload_completed", "audit.requested", "audit.processing_started", "audit.rule_snapshot_created", "audit.completed", "document.download_authorized"];
    assertResult("audit-trail", "full-lifecycle-audit-events-present", expectedEvents.every((action) => eventActions.includes(action)));
    const auditCorrelation = await sql(container, `select count(*)=4 and count(distinct correlation_id)=1 and min(correlation_id::text)='${auditA.id}' from public.audit_trail_events where resource_id='${auditA.id}' and action in ('audit.requested','audit.processing_started','audit.rule_snapshot_created','audit.completed')`);
    assertResult("audit-trail", "audit-correlation-consistent-across-api-worker-trigger", auditCorrelation === "t");
    const actors = await sql(container, `select count(*)=4 and bool_and((action='audit.requested' and actor_type='user' and actor_user_id='${userIds[0]}') or (action<>'audit.requested' and actor_type='service' and actor_service='worker')) from public.audit_trail_events where resource_id='${auditA.id}' and action in ('audit.requested','audit.processing_started','audit.rule_snapshot_created','audit.completed')`);
    assertResult("audit-trail", "audit-actors-match-user-and-worker", actors === "t");
    const safeMetadata = await sql(container, `select count(*)=0 from public.audit_trail_events where owner_user_id='${userIds[0]}' and (metadata - array['version_number','previous_status','new_status','audit_status','applicable_rule_count','finding_count','file_size_bytes','mime_type','failure_category','cleanup_reason','download_kind']::text[] <> '{}'::jsonb or metadata::text ~* '(token|secret|connection|string|signed|storage.?path|document.?text|exception|stack.?trace|https?://)')`);
    assertResult("audit-trail", "audit-metadata-allowlist-and-sensitive-data-hygiene", safeMetadata === "t");

    const beforeRetry = await sql(container, `select (select count(*) from public.audit_rule_snapshots where audit_job_id='${auditA.id}')||','||(select count(*) from public.audit_findings where audit_job_id='${auditA.id}')||','||(select count(*) from public.audit_trail_events where resource_id='${auditA.id}' and action='audit.completed')`);
    await delay(2500);
    const afterRetry = await sql(container, `select (select count(*) from public.audit_rule_snapshots where audit_job_id='${auditA.id}')||','||(select count(*) from public.audit_findings where audit_job_id='${auditA.id}')||','||(select count(*) from public.audit_trail_events where resource_id='${auditA.id}' and action='audit.completed')`);
    assertResult("concurrency", "two-workers-claim-once-and-retry-does-not-duplicate", beforeRetry === afterRetry && beforeRetry.endsWith(",1"));
    const distinctSnapshots = await sql(container, `select count(*)=count(distinct rule_code) and count(*)=count(distinct ordinal) from public.audit_rule_snapshots where audit_job_id='${auditA.id}'`);
    assertResult("concurrency", "snapshot-set-remains-unique-under-two-workers", distinctSnapshots === "t");

    const immutableBefore = await sql(container, `select sha256||','||storage_key||','||version_no from public.document_versions where id='${documentA.versionId}'`);
    const versionUpdate = await serviceDataRequest(environment, "document_versions", `id=eq.${documentA.versionId}`, { method: "PATCH", body: JSON.stringify({ sha256: "b".repeat(64) }) });
    const versionDelete = await serviceDataRequest(environment, "document_versions", `id=eq.${documentA.versionId}`, { method: "DELETE" });
    const auditUpdate = await serviceDataRequest(environment, "audit_jobs", `id=eq.${auditA.id}`, { method: "PATCH", body: JSON.stringify({ status: "Processing" }) });
    const auditDelete = await serviceDataRequest(environment, "audit_jobs", `id=eq.${auditA.id}`, { method: "DELETE" });
    const snapshotUpdate = await serviceDataRequest(environment, "audit_rule_snapshots", `audit_job_id=eq.${auditA.id}`, { method: "PATCH", body: JSON.stringify({ ordinal: 999 }) });
    const snapshotDelete = await serviceDataRequest(environment, "audit_rule_snapshots", `audit_job_id=eq.${auditA.id}`, { method: "DELETE" });
    const findingUpdate = await serviceDataRequest(environment, "audit_findings", `audit_job_id=eq.${auditA.id}`, { method: "PATCH", body: JSON.stringify({ confidence: 0 }) });
    const findingDelete = await serviceDataRequest(environment, "audit_findings", `audit_job_id=eq.${auditA.id}`, { method: "DELETE" });
    const immutableAfter = await sql(container, `select sha256||','||storage_key||','||version_no from public.document_versions where id='${documentA.versionId}'`);
    assertResult("immutability", "runtime-version-audit-snapshot-finding-mutations-denied", [versionUpdate, versionDelete, auditUpdate, auditDelete, snapshotUpdate, snapshotDelete, findingUpdate, findingDelete].every((response) => !response.ok) && immutableBefore === immutableAfter);
    const immutableObjectRead = await fetchLocal(signedUrl);
    const immutableObjectBytes = immutableObjectRead.ok ? Buffer.from(await immutableObjectRead.arrayBuffer()) : Buffer.alloc(0);
    assertResult("immutability", "original-storage-object-unchanged-after-runtime-mutations", immutableObjectRead.ok && createHash("sha256").update(immutableObjectBytes).digest("hex") === fixtureHash);

    const auditEventId = await sql(container, `select id from public.audit_trail_events where resource_id='${auditA.id}' limit 1`);
    const trailUpdate = await serviceDataRequest(environment, "audit_trail_events", `id=eq.${auditEventId}`, { method: "PATCH", body: JSON.stringify({ action: "audit.failed" }) });
    const trailDelete = await serviceDataRequest(environment, "audit_trail_events", `id=eq.${auditEventId}`, { method: "DELETE" });
    assertResult("audit-trail", "service-role-cannot-update-or-delete-audit-event", !trailUpdate.ok && !trailDelete.ok);

    await stopBackend(workerA);
    await stopBackend(workerB);

    await createFault(container, "s1t06_fail_document_insert", "documents", "new.title='S1T06 synthetic database failure'", "raise exception using message='Synthetic integration failure'");
    const objectCountBefore = await sql(container, `select count(*) from storage.objects where bucket_id='${buckets[0]}' and name like '${userIds[0]}/%'`);
    const cleanupEventBefore = Number(await sql(container, `select count(*) from public.audit_trail_events where owner_user_id='${userIds[0]}' and action='storage.orphan_cleanup'`));
    const failedUpload = await uploadDocument(apiBaseUrl, tokens[0], fixtureCompliant, "S1T06 synthetic database failure");
    await dropFaults(container);
    await delay(500);
    const objectCountAfter = await sql(container, `select count(*) from storage.objects where bucket_id='${buckets[0]}' and name like '${userIds[0]}/%'`);
    const failedDocuments = await sql(container, "select count(*) from public.documents where title='S1T06 synthetic database failure'");
    const cleanupEventAfter = Number(await sql(container, `select count(*) from public.audit_trail_events where owner_user_id='${userIds[0]}' and action='storage.orphan_cleanup'`));
    assertResult("failure-injection", "storage-success-database-failure-cleans-orphan-and-partial-row", failedUpload.response.status >= 500 && objectCountBefore === objectCountAfter && failedDocuments === "0" && cleanupEventAfter === cleanupEventBefore + 1);

    const snapshotFailureRequest = await requestAudit(apiBaseUrl, tokens[0], documentA.versionId);
    requireResult("failure-injection", "snapshot-failure-job-queued", snapshotFailureRequest.response.status === 202 && uuid(snapshotFailureRequest.json?.id));
    const snapshotFailureId = snapshotFailureRequest.json.id;
    await createFault(container, "s1t06_fail_snapshot_insert", "audit_rule_snapshots", `new.audit_job_id='${snapshotFailureId}'::uuid`, "raise exception using message='Synthetic integration failure'");
    const snapshotFaultWorker = await startWorker("worker-snapshot-fault", workerDll, backendEnv);
    const snapshotFailed = await waitForAudit(apiBaseUrl, tokens[0], snapshotFailureId, ["Failed"]);
    await stopBackend(snapshotFaultWorker);
    await dropFaults(container);
    const snapshotFailureState = await sql(container, `select (select count(*) from public.audit_rule_snapshots where audit_job_id='${snapshotFailureId}')||','||(select count(*) from public.audit_trail_events where resource_id='${snapshotFailureId}' and action='audit.failed' and metadata->>'failure_category'='processing_error')`);
    assertResult("failure-injection", "snapshot-failure-rolls-back-and-records-generic-failure", snapshotFailed?.status === "Failed" && snapshotFailureState === "0,1");

    const findingFailureRequest = await requestAudit(apiBaseUrl, tokens[0], documentA.versionId);
    requireResult("failure-injection", "finding-failure-job-queued", findingFailureRequest.response.status === 202 && uuid(findingFailureRequest.json?.id));
    const findingFailureId = findingFailureRequest.json.id;
    await createFault(container, "s1t06_fail_finding_insert", "audit_findings", `new.audit_job_id='${findingFailureId}'::uuid`, "raise exception using message='Synthetic integration failure'");
    const findingFaultWorker = await startWorker("worker-finding-fault", workerDll, backendEnv);
    const findingFailed = await waitForAudit(apiBaseUrl, tokens[0], findingFailureId, ["Failed", "Completed"]);
    await stopBackend(findingFaultWorker);
    await dropFaults(container);
    const findingFailureState = await sql(container, `select (select count(*) from public.audit_rule_snapshots where audit_job_id='${findingFailureId}')||','||(select count(*) from public.audit_findings where audit_job_id='${findingFailureId}')||','||(select count(*) from public.audit_trail_events where resource_id='${findingFailureId}' and action='audit.completed')`);
    const [findingSnapshots, failedFindings, falseCompletion] = findingFailureState.split(",").map(Number);
    assertResult("failure-injection", "finding-failure-prevents-false-completion-and-partial-findings", findingFailed?.status === "Failed" && findingSnapshots > 0 && failedFindings === 0 && falseCompletion === 0);

    const stoppedRequest = await requestAudit(apiBaseUrl, tokens[0], documentA.versionId);
    requireResult("failure-injection", "worker-stop-job-queued", stoppedRequest.response.status === 202 && uuid(stoppedRequest.json?.id));
    const stoppedAuditId = stoppedRequest.json.id;
    await createFault(container, "s1t06_pause_snapshot_insert", "audit_rule_snapshots", `new.audit_job_id='${stoppedAuditId}'::uuid`, "perform pg_catalog.pg_sleep(20)");
    const stoppedWorker = await startWorker("worker-stop-after-claim", workerDll, backendEnv);
    const processing = await waitForAudit(apiBaseUrl, tokens[0], stoppedAuditId, ["Processing"], 15000);
    await stopBackend(stoppedWorker);
    await dropFaults(container);
    const recoveryWorker = await startWorker("worker-recovery-contract", workerDll, backendEnv);
    await delay(2500);
    await stopBackend(recoveryWorker);
    const stoppedState = await sql(container, `select status||','||(select count(*) from public.audit_rule_snapshots where audit_job_id='${stoppedAuditId}') from public.audit_jobs where id='${stoppedAuditId}'`);
    assertResult("failure-injection", "worker-stop-after-claim-leaves-consistent-documented-processing-state", processing?.status === "Processing" && stoppedState === "Processing,0");

    const downloadEventsBeforeFailure = await sql(container, `select count(*) from public.audit_trail_events where resource_id='${documentA.versionId}' and action='document.download_authorized'`);
    await deleteStorageObject(environment, buckets[0], objectPath);
    const failedDownload = await apiRequest(apiBaseUrl, `/api/document-versions/${documentA.versionId}/download`, tokens[0]);
    responseSamples.push(await failedDownload.text());
    const deniedAfterObjectRemoval = await apiRequest(apiBaseUrl, `/api/document-versions/${documentA.versionId}/download`, tokens[1]);
    responseSamples.push(await deniedAfterObjectRemoval.text());
    const downloadEventsAfterFailure = await sql(container, `select count(*) from public.audit_trail_events where resource_id='${documentA.versionId}' and action='document.download_authorized'`);
    assertResult("failure-injection", "signed-url-failure-has-no-success-or-audit-event-and-auth-precedes-storage", failedDownload.status >= 500 && deniedAfterObjectRemoval.status === 404 && downloadEventsBeforeFailure === downloadEventsAfterFailure);

    const documentCorrelations = await sql(container, `select count(distinct correlation_id) from public.audit_trail_events where action='document.upload_completed' and owner_user_id in ('${userIds[0]}','${userIds[1]}')`);
    assertResult("audit-trail", "transaction-local-context-does-not-leak-between-users", documentCorrelations === "2");
  } catch {
    try {
      for (const code of await safeDiagnosticCodes()) assertResult("diagnostic", `diagnostic-${code}`, false);
    } catch { }
    assertResult("suite", "security-integration-execution", false);
  } finally {
    try { if (container) await dropFaults(container); } catch { cleanupPassed = false; }
    for (const entry of [...processes]) {
      try { await stopBackend(entry); } catch { cleanupPassed = false; }
    }
    if (environment && container) {
      try { await cleanupSynthetic(environment, container); } catch { cleanupPassed = false; }
      try {
        const leftovers = (await authUsers(environment)).filter((candidate) => identities.some((identity) => identity.email === candidate.email));
        const rowLeftovers = await sql(container, "select count(*) from public.documents where title like 'Synthetic lifecycle document %' or title='S1T06 synthetic database failure'");
        if (leftovers.length !== 0 || rowLeftovers !== "0") cleanupPassed = false;
      } catch { cleanupPassed = false; }
    }
    const tempAfter = await temporaryDocxNames();
    const newTempFiles = [...tempAfter].filter((name) => !tempBefore.has(name));
    assertResult("cleanup", "temporary-docx-files-cleaned", newTempFiles.length === 0);
    if (environment && logDirectory) {
      try {
        const hygiene = await scanLogsAndResponses(environment, password, signedUrl);
        if (hygiene.hasExactSecret) assertResult("diagnostic", "diagnostic-hygiene-exact-secret", false);
        if (hygiene.hasPattern) assertResult("diagnostic", "diagnostic-hygiene-sensitive-pattern", false);
        if (hygiene.hasStoragePath) assertResult("diagnostic", `diagnostic-hygiene-storage-path-${hygiene.storagePathCategory ?? "unknown-category"}`, false);
        assertResult("hygiene", "api-worker-response-log-hygiene", hygiene.passed);
      }
      catch { assertResult("hygiene", "api-worker-response-log-hygiene", false); }
    } else {
      assertResult("hygiene", "api-worker-response-log-hygiene", false);
    }
    if (logDirectory) {
      try { await rm(logDirectory, { recursive: true, force: true }); } catch { cleanupPassed = false; }
    }
    assertResult("cleanup", "synthetic-users-rows-storage-processes-and-faults-cleaned", cleanupPassed && processes.size === 0);
  }

  if (cleanupPassed) {
    assertResult("regression", "rls-component-smoke-regression", await componentSmoke("test:rls-local"));
    assertResult("regression", "storage-component-smoke-regression", await componentSmoke("test:storage-local"));
    assertResult("regression", "immutability-component-smoke-regression", await componentSmoke("test:immutability-local"));
    assertResult("regression", "audit-trail-component-smoke-regression", await componentSmoke("test:audit-trail-local"));
  }

  try {
    await writeSummary(startedAt, cleanupPassed);
    assertResult("report", "safe-machine-readable-summary-written", true);
    await writeSummary(startedAt, cleanupPassed);
  } catch {
    assertResult("report", "safe-machine-readable-summary-written", false);
  }
  const passed = assertions.filter((item) => item.passed).length;
  const failed = assertions.length - passed;
  console.log(`SUMMARY total=${assertions.length} pass=${passed} fail=${failed}`);
  process.exitCode = failed === 0 ? 0 : 1;
}

main();
