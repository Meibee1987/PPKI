import { spawn } from "node:child_process";
import { access, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const DEFAULT_ROOT = path.resolve(SCRIPT_DIR, "..");
const PROJECT_ID = "ppki-smart-formatter";
const RENDERER_CONTAINER = "ppki-smart-formatter-renderer-dev";
const RENDERER_IMAGE = "gotenberg/gotenberg:8.34.0-libreoffice@sha256:3c23aeb3a027a63d7c71745fc9d83724bd58cf9dfa470396ac82c0896028db2a";
const DEFAULT_RENDERER_PORT = 55300;
const INFRA_PORTS = Object.freeze([54320, 54322, 54323, 54324, 54327, 55321, DEFAULT_RENDERER_PORT]);
const REQUIRED_STATUS_NAMES = Object.freeze(["API_URL", "DB_URL", "ANON_KEY", "SERVICE_ROLE_KEY"]);
const SENSITIVE_NAME = /(?:KEY|SECRET|PASSWORD|TOKEN|JWT|DB_URL|CONNECTION)/i;
const PLACEHOLDER = /(?:replace_me|project_ref|change-me|your[-_]|example)/i;

export async function findRepositoryRoot(start = process.cwd(), fileAccess = access) {
  let current = path.resolve(start);
  while (true) {
    try {
      await Promise.all([
        fileAccess(path.join(current, "package.json")),
        fileAccess(path.join(current, "supabase", "config.toml")),
        fileAccess(path.join(current, "backend", "PpkiSmartFormatter.slnx")),
      ]);
      return current;
    } catch {
      const parent = path.dirname(current);
      if (parent === current) throw new Error("Repository root tidak ditemukan. Jalankan command dari checkout PPKI.");
      current = parent;
    }
  }
}

export async function resolveRuleCatalog(root, fileAccess = access) {
  const catalog = path.join(root, "rules", "ppki-ipb-2019", "rules.json");
  try {
    await fileAccess(catalog);
    return catalog;
  } catch {
    throw new Error(`Rule catalog tidak ditemukan: ${path.relative(root, catalog)}.`);
  }
}

export function parseEnvironment(text) {
  const result = {};
  for (const rawLine of text.split(/\r?\n/u)) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#")) continue;
    const match = /^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=(.*)$/u.exec(line);
    if (!match) continue;
    let value = match[2].trim();
    if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1);
    }
    result[match[1]] = value;
  }
  return result;
}

export async function loadLocalOverrides(root, read = readFile) {
  try {
    const values = parseEnvironment(await read(path.join(root, ".env.local"), "utf8"));
    const forbidden = Object.keys(values).filter((name) => SENSITIVE_NAME.test(name));
    if (forbidden.length) {
      throw new Error(`.env.local hanya untuk opsi non-secret; hapus: ${forbidden.join(", ")}.`);
    }
    for (const [name, value] of Object.entries(values)) {
      if (PLACEHOLDER.test(value)) throw new Error(`Placeholder ditolak untuk ${name}.`);
    }
    return values;
  } catch (error) {
    if (error?.code === "ENOENT") return {};
    throw error;
  }
}

function parsePort(name, value, fallback) {
  const port = value === undefined || value === "" ? fallback : Number(value);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error(`${name} harus berupa port 1-65535.`);
  return port;
}

export function localSettings(overrides = {}) {
  const apiPort = parsePort("API_PORT", overrides.API_PORT, 5080);
  const webPort = parsePort("WEB_PORT", overrides.WEB_PORT, 3000);
  const workerPoll = parsePort("WORKER_POLL_SECONDS", overrides.WORKER_POLL_SECONDS, 2);
  const healthTimeout = parsePort("HEALTHCHECKS_TIMEOUT_SECONDS", overrides.HEALTHCHECKS_TIMEOUT_SECONDS, 3);
  const rendererPort = parsePort("DOCUMENT_RENDERER_PORT", overrides.DOCUMENT_RENDERER_PORT, DEFAULT_RENDERER_PORT);
  const rendererTimeout = parsePort("DOCUMENT_RENDERER_TIMEOUT_SECONDS", overrides.DOCUMENT_RENDERER_TIMEOUT_SECONDS, 120);
  return {
    apiPort,
    webPort,
    workerPoll,
    healthTimeout,
    rendererPort,
    rendererUrl: `http://127.0.0.1:${rendererPort}`,
    rendererTimeout,
    apiUrl: `http://127.0.0.1:${apiPort}`,
    webUrl: `http://localhost:${webPort}`,
  };
}

export function postgresUrlToConnectionString(value) {
  let url;
  try { url = new URL(value); } catch { throw new Error("DB_URL dari Supabase CLI tidak valid."); }
  if (!/^postgres(?:ql)?:$/u.test(url.protocol) || !url.hostname || !url.pathname.slice(1)) {
    throw new Error("DB_URL dari Supabase CLI tidak valid.");
  }
  const pairs = [
    ["Host", url.hostname], ["Port", url.port || "5432"], ["Database", decodeURIComponent(url.pathname.slice(1))],
    ["Username", decodeURIComponent(url.username)], ["Password", decodeURIComponent(url.password)],
  ];
  return pairs.map(([name, item]) => `${name}=${item.replaceAll(";", "\\;")}`).join(";");
}

export function validateSupabaseStatus(values) {
  const missing = REQUIRED_STATUS_NAMES.filter((name) => !values[name]);
  const placeholders = REQUIRED_STATUS_NAMES.filter((name) => values[name] && PLACEHOLDER.test(values[name]));
  if (missing.length || placeholders.length) {
    const names = [...new Set([...missing, ...placeholders])];
    throw new Error(`Konfigurasi Supabase lokal belum siap. Periksa variable: ${names.join(", ")}. Jalankan npm run dev:infra.`);
  }
  const api = new URL(values.API_URL);
  if (api.protocol !== "http:" || !["localhost", "127.0.0.1", "::1"].includes(api.hostname)) {
    throw new Error("API_URL harus menunjuk Supabase lokal (HTTP loopback); hosted Supabase ditolak.");
  }
  postgresUrlToConnectionString(values.DB_URL);
  return values;
}

function spawnResult(command, args, { cwd, env = process.env, spawnCommand = spawn } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawnCommand(command, args, { cwd, env, stdio: ["ignore", "pipe", "pipe"], shell: false });
    let stdout = "";
    let stderr = "";
    child.stdout?.on("data", (chunk) => { stdout += chunk; });
    child.stderr?.on("data", (chunk) => { stderr += chunk; });
    child.once("error", reject);
    child.once("close", (code) => resolve({ code, stdout, stderr }));
  });
}

export function safeCliDiagnostic(stderr) {
  const redacted = stderr
    .replace(/\b((?:ANON_KEY|SERVICE_ROLE_KEY|JWT_SECRET|DB_URL|PASSWORD|SECRET_KEY))\s*=\s*\S+/giu, "$1=[redacted]")
    .replace(/postgres(?:ql)?:\/\/[^\s@]+@/giu, "postgresql://[redacted]@")
    .replace(/\b(?:sb_(?:secret|publishable)_[A-Za-z0-9._-]+|eyJ[A-Za-z0-9._-]{20,})\b/gu, "[redacted]");
  const patterns = [
    /failed to start docker container "[A-Za-z0-9_.-]+"/giu,
    /failed to bind (?:host )?port [A-Za-z0-9_.:[\]-]+(?:\/tcp)?/giu,
    /address already in use/giu,
    /docker daemon (?:is not running|is unavailable)/giu,
    /permission denied[^\r\n"]*(?:docker|docker_engine)/giu,
    /container "?[A-Za-z0-9_.-]+"? (?:is )?unhealthy/giu,
  ];
  const fragments = patterns.flatMap((pattern) => [...redacted.matchAll(pattern)].map((match) => match[0]));
  return [...new Set(fragments)].join("; ") || "Tidak ada diagnostic aman dari CLI.";
}

function supabaseInvocation(root, args) {
  return { command: process.execPath, args: [path.join(root, "node_modules", "supabase", "dist", "supabase.js"), ...args] };
}

function npmInvocation(args) {
  return process.platform === "win32"
    ? { command: process.execPath, args: [path.join(path.dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js"), ...args] }
    : { command: "npm", args };
}

export async function getSupabaseEnvironment(root, run = spawnResult) {
  const invocation = supabaseInvocation(root, ["status", "--output", "env"]);
  let result;
  try { result = await run(invocation.command, invocation.args, { cwd: root }); }
  catch { throw new Error("Supabase CLI tidak dapat dijalankan. Jalankan npm ci lalu npm run dev:infra."); }
  if (result.code !== 0) throw new Error("Supabase lokal tidak aktif atau tidak lengkap. Jalankan npm run dev:infra.");
  return validateSupabaseStatus(parseEnvironment(result.stdout));
}

export function buildChildEnvironment(base, supabase, settings, catalog) {
  return {
    ...base,
    ASPNETCORE_ENVIRONMENT: "Development",
    DOTNET_ENVIRONMENT: "Development",
    ASPNETCORE_URLS: settings.apiUrl,
    ConnectionStrings__Database: postgresUrlToConnectionString(supabase.DB_URL),
    Supabase__Url: supabase.API_URL,
    Supabase__PublishableKey: supabase.ANON_KEY,
    Supabase__SecretKey: supabase.SERVICE_ROLE_KEY,
    Supabase__Storage__OriginalBucket: "documents-original",
    Supabase__Storage__VersionBucket: "documents-versions",
    Supabase__Storage__ReportBucket: "audit-reports",
    RuleCatalog__Path: catalog,
    Cors__AllowedOrigins__0: settings.webUrl,
    Worker__PollSeconds: String(settings.workerPoll),
    HealthChecks__TimeoutSeconds: String(settings.healthTimeout),
    DocumentRenderer__BaseUrl: settings.rendererUrl ?? `http://127.0.0.1:${DEFAULT_RENDERER_PORT}`,
    DocumentRenderer__TimeoutSeconds: String(settings.rendererTimeout ?? 120),
    NEXT_PUBLIC_API_BASE_URL: settings.apiUrl,
    NEXT_PUBLIC_SUPABASE_URL: supabase.API_URL,
    NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY: supabase.ANON_KEY,
  };
}

export async function inspectPorts(ports, { run = spawnResult, platform = process.platform } = {}) {
  const wanted = new Set(ports.map(Number));
  const command = platform === "win32" ? "netstat" : "sh";
  const args = platform === "win32" ? ["-ano", "-p", "tcp"] : ["-c", "command -v ss >/dev/null && ss -ltnp || netstat -ltnp"];
  const result = await run(command, args, {});
  if (result.code !== 0) return [];
  const owners = [];
  for (const line of result.stdout.split(/\r?\n/u)) {
    const portMatch = /(?:\]|:)(\d+)\s+/u.exec(line);
    if (!portMatch || !wanted.has(Number(portMatch[1])) || !/(LISTEN|LISTENING)/iu.test(line)) continue;
    const pid = platform === "win32" ? line.trim().split(/\s+/u).at(-1) : undefined;
    owners.push({ port: Number(portMatch[1]), owner: pid ? `PID ${pid}` : line.trim().slice(0, 160) });
  }
  return owners;
}

export function formatPortConflicts(conflicts) {
  return conflicts.map(({ port, owner, container }) => `port ${port}: ${container ? `container ${container}` : owner}`).join("; ");
}

export function attachContainerOwners(listeners, containers) {
  return listeners.map((listener) => {
    const match = containers.find((container) => container.ports.includes(`:${listener.port}->`));
    return match ? { ...listener, container: match.name } : listener;
  });
}

export async function checkDocker(run = spawnResult) {
  const result = await run("docker", ["version", "--format", "{{.Server.Version}}"], {});
  if (result.code !== 0) throw new Error("Docker daemon tidak aktif. Buka Docker Desktop dan tunggu engine siap.");
}

export async function listProjectContainers(root, run = spawnResult) {
  const result = await run("docker", ["ps", "-a", "--format", "{{.Names}}|{{.Ports}}"], { cwd: root });
  if (result.code !== 0) return [];
  return result.stdout.split(/\r?\n/u).filter(Boolean).map((line) => {
    const [name, ports = ""] = line.split("|");
    return { name, ports, owned: name === RENDERER_CONTAINER
      || name.includes(PROJECT_ID) && name.startsWith("supabase_") };
  });
}

export async function ensureLocalRenderer(root, run = spawnResult, probe = async (url) => {
  try { return (await fetch(url, { signal: AbortSignal.timeout(2_000) })).ok; } catch { return false; }
}, port = DEFAULT_RENDERER_PORT, timeoutSeconds = 120) {
  let inspected = await run("docker", ["inspect", "--format", "{{.Config.Image}}|{{.State.Running}}|{{json .Config.Cmd}}", RENDERER_CONTAINER], { cwd: root });
  let create = inspected.code !== 0;
  if (!create) {
    const [image, running, command] = inspected.stdout.trim().split("|");
    if (image !== RENDERER_IMAGE) throw new Error(`Container ${RENDERER_CONTAINER} tidak memakai image renderer pinned.`);
    if (command !== JSON.stringify(["gotenberg", `--api-timeout=${timeoutSeconds}s`])) {
      const removed = await run("docker", ["rm", "-f", RENDERER_CONTAINER], { cwd: root });
      if (removed.code !== 0) throw new Error("Renderer lokal stale gagal diganti.");
      create = true;
    } else if (running !== "true") {
      const started = await run("docker", ["start", RENDERER_CONTAINER], { cwd: root });
      if (started.code !== 0) throw new Error("Renderer lokal gagal dimulai.");
    }
  }
  if (create) {
    const started = await run("docker", ["run", "-d", "--name", RENDERER_CONTAINER,
      "-p", `127.0.0.1:${port}:3000`, RENDERER_IMAGE,
      "gotenberg", `--api-timeout=${timeoutSeconds}s`], { cwd: root });
    if (started.code !== 0) throw new Error("Renderer lokal gagal dibuat dari image pinned.");
  }
  for (let attempt = 0; attempt < 30; attempt++) {
    if (await probe(`http://127.0.0.1:${port}/health`)) return;
    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }
  throw new Error("Renderer lokal tidak healthy pada endpoint loopback canonical.");
}

export async function ensureInfraPortsAvailable(root, { inspect = inspectPorts, containers = listProjectContainers } = {}) {
  const [listeners, knownContainers] = await Promise.all([inspect(INFRA_PORTS), containers(root)]);
  const enriched = attachContainerOwners(listeners, knownContainers);
  const conflicts = enriched.filter((listener) => !knownContainers.find((container) => container.name === listener.container)?.owned);
  if (conflicts.length) throw new Error(`Port Supabase lokal sedang dipakai: ${formatPortConflicts(conflicts)}. Hentikan hanya process/container tersebut.`);
}

export async function ensureApplicationPortAvailable(port, inspect = inspectPorts, containers = listProjectContainers, root = process.cwd()) {
  const [listeners, knownContainers] = await Promise.all([inspect([port]), containers(root)]);
  const conflicts = attachContainerOwners(listeners, knownContainers);
  if (conflicts.length) throw new Error(`Port aplikasi sedang dipakai: ${formatPortConflicts(conflicts)}. Hentikan process/container pemilik atau ubah port di .env.local.`);
}

export function supervise(specs, { spawnCommand = spawn, onSignal, write = console.log } = {}) {
  return new Promise((resolve, reject) => {
    const children = new Set();
    let stopping = false;
    let firstFailure;
    const stopAll = () => {
      if (stopping) return;
      stopping = true;
      for (const child of children) if (child.exitCode === null && child.signalCode === null) child.kill();
    };
    const signalHandler = () => { stopAll(); };
    (onSignal ?? ((handler) => { process.once("SIGINT", handler); process.once("SIGTERM", handler); }))(signalHandler);
    for (const spec of specs) {
      write(`Starting ${spec.label}...`);
      const child = spawnCommand(spec.command, spec.args, { cwd: spec.cwd, env: spec.env, stdio: "inherit", shell: false });
      children.add(child);
      child.once("error", (error) => { firstFailure ??= new Error(`${spec.label} gagal dimulai: ${error.message}`); stopAll(); });
      child.once("exit", (code, signal) => {
        children.delete(child);
        if (!stopping && specs.length > 1) {
          firstFailure ??= new Error(`${spec.label} berhenti ${code === 0 && !signal ? "secara tak terduga dengan code 0" : `dengan ${signal ? `signal ${signal}` : `code ${code}`}`}.`);
          stopAll();
        } else if (!stopping && (code !== 0 || signal)) {
          firstFailure ??= new Error(`${spec.label} berhenti dengan ${signal ? `signal ${signal}` : `code ${code}`}.`);
        }
        if (children.size === 0) firstFailure ? reject(firstFailure) : resolve();
      });
    }
  });
}

async function buildProjects(root, kinds) {
  for (const kind of kinds) {
    const project = `backend/services/Ppki.${kind}/Ppki.${kind}.csproj`;
    const result = await new Promise((resolve, reject) => {
      const child = spawn("dotnet", ["build", project, "--nologo"], { cwd: root, env: process.env, stdio: "inherit", shell: false });
      child.once("error", reject);
      child.once("close", (code) => resolve(code));
    });
    if (result !== 0) throw new Error(`Build Ppki.${kind} gagal dengan code ${result}.`);
  }
}

async function prepare(command) {
  const root = await findRepositoryRoot();
  const catalog = await resolveRuleCatalog(root);
  const overrides = await loadLocalOverrides(root);
  const settings = localSettings(overrides);
  await checkDocker();
  if (command === "infra") return { root, catalog, settings };
  const supabase = await getSupabaseEnvironment(root);
  if (command === "backend" || command === "worker")
    await ensureLocalRenderer(root, spawnResult, undefined, settings.rendererPort, settings.rendererTimeout);
  return { root, catalog, settings, env: buildChildEnvironment(process.env, supabase, settings, catalog) };
}

async function runInfra() {
  const { root, settings } = await prepare("infra");
  await ensureInfraPortsAvailable(root);
  const invocation = supabaseInvocation(root, ["start"]);
  const result = await spawnResult(invocation.command, invocation.args, { cwd: root });
  if (result.code !== 0) throw new Error(`Supabase lokal gagal dimulai. ${safeCliDiagnostic(`${result.stderr}\n${result.stdout}`)} Jalankan npm run dev:status; untuk stack parsial gunakan npm run dev:stop lalu npm run dev:infra.`);
  await getSupabaseEnvironment(root);
  await ensureLocalRenderer(root, spawnResult, undefined, settings.rendererPort, settings.rendererTimeout);
  console.log(`Supabase lokal dan renderer pinned siap (API 55321, PostgreSQL 54322, renderer ${settings.rendererPort}). Kredensial dimuat secara internal dan tidak dicetak.`);
}

async function runOne(kind) {
  const { root, env, settings } = await prepare(kind);
  if (kind === "api" || kind === "backend") await ensureApplicationPortAvailable(settings.apiPort);
  if (kind === "web") await ensureApplicationPortAvailable(settings.webPort);
  const backendKinds = kind === "backend" ? ["Api", "Worker"] : kind === "api" ? ["Api"] : kind === "worker" ? ["Worker"] : [];
  if (backendKinds.length) await buildProjects(root, backendKinds);
  const npm = npmInvocation(["--prefix", "apps/web", "run", "dev", "--", "--port", String(settings.webPort)]);
  const specs = {
    api: { label: "Ppki.Api", command: "dotnet", args: ["backend/services/Ppki.Api/bin/Debug/net10.0/Ppki.Api.dll"], cwd: root, env },
    worker: { label: "Ppki.Worker", command: "dotnet", args: ["backend/services/Ppki.Worker/bin/Debug/net10.0/Ppki.Worker.dll"], cwd: root, env },
    web: { label: "web", command: npm.command, args: npm.args, cwd: root, env },
  };
  await supervise(kind === "backend" ? [specs.api, specs.worker] : [specs[kind]]);
}

async function runStatus() {
  const root = await findRepositoryRoot();
  await resolveRuleCatalog(root);
  const settings = localSettings(await loadLocalOverrides(root));
  await checkDocker();
  const containers = await listProjectContainers(root);
  let ready = false;
  try { await getSupabaseEnvironment(root); ready = true; } catch { /* safe status below */ }
  const listeners = attachContainerOwners(await inspectPorts([...INFRA_PORTS, settings.webPort, settings.apiPort]), containers);
  let rendererReady = false;
  try { rendererReady = (await fetch(`${settings.rendererUrl}/health`, { signal: AbortSignal.timeout(2_000) })).ok; } catch { }
  console.log(`Repository: OK; RuleCatalog: OK; Docker: OK; Supabase lokal: ${ready ? "ready" : "not ready"}.`);
  console.log(`Container Supabase project: ${containers.filter((item) => item.owned).map((item) => item.name).join(", ") || "tidak ada"}.`);
  console.log(`Renderer pinned lokal: ${rendererReady ? "ready" : "not ready"}; endpoint ${settings.rendererUrl}.`);
  console.log(`Port listener: ${listeners.length ? formatPortConflicts(listeners) : "tidak ada pada port development default"}.`);
  if (!ready) process.exitCode = 1;
}

async function stopInfra() {
  const root = await findRepositoryRoot();
  await checkDocker();
  const invocation = supabaseInvocation(root, ["stop"]);
  const result = await spawnResult(invocation.command, invocation.args, { cwd: root });
  if (result.code !== 0) throw new Error("Supabase lokal gagal dihentikan. Tidak ada volume yang dihapus oleh script ini.");
  console.log("Supabase lokal project ini dihentikan tanpa db reset atau penghapusan volume eksplisit.");
}

async function main() {
  const command = process.argv[2];
  if (command === "infra") return runInfra();
  if (["api", "worker", "backend", "web"].includes(command)) return runOne(command);
  if (command === "status") return runStatus();
  if (command === "stop") return stopInfra();
  throw new Error("Usage: node scripts/dev-bootstrap.mjs <infra|api|worker|backend|web|status|stop>");
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(`Development bootstrap gagal: ${error.message}`);
    process.exitCode = 1;
  });
}
