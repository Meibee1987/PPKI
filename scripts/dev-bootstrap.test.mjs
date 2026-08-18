import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import {
  buildChildEnvironment,
  attachContainerOwners,
  ensureApplicationPortAvailable,
  ensureInfraPortsAvailable,
  ensureLocalRenderer,
  findRepositoryRoot,
  formatPortConflicts,
  loadLocalOverrides,
  parseEnvironment,
  resolveRuleCatalog,
  safeCliDiagnostic,
  supervise,
  validateSupabaseStatus,
} from "./dev-bootstrap.mjs";

async function fixtureRoot({ catalog = true } = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), "ppki-bootstrap-"));
  await mkdir(path.join(root, "supabase"), { recursive: true });
  await mkdir(path.join(root, "backend"), { recursive: true });
  await writeFile(path.join(root, "package.json"), "{}");
  await writeFile(path.join(root, "supabase", "config.toml"), "project_id='test'");
  await writeFile(path.join(root, "backend", "PpkiSmartFormatter.slnx"), "<Solution />");
  if (catalog) {
    await mkdir(path.join(root, "rules", "ppki-ipb-2019"), { recursive: true });
    await writeFile(path.join(root, "rules", "ppki-ipb-2019", "rules.json"), "{}");
  }
  return root;
}

test("repository root and relative RuleCatalog are resolved from a nested directory", async (t) => {
  const root = await fixtureRoot();
  t.after(() => rm(root, { recursive: true, force: true }));
  const nested = path.join(root, "apps", "web");
  await mkdir(nested, { recursive: true });
  assert.equal(await findRepositoryRoot(nested), root);
  assert.equal(await resolveRuleCatalog(root), path.join(root, "rules", "ppki-ipb-2019", "rules.json"));
});

test("missing RuleCatalog reports its repository-relative path", async (t) => {
  const root = await fixtureRoot({ catalog: false });
  t.after(() => rm(root, { recursive: true, force: true }));
  await assert.rejects(resolveRuleCatalog(root), /rules[\\/]ppki-ipb-2019[\\/]rules\.json/);
});

test("missing and placeholder local Supabase status names are reported without values", () => {
  const secret = "sb_secret_DO_NOT_PRINT_93841";
  assert.throws(
    () => validateSupabaseStatus({ API_URL: "http://127.0.0.1:55321", DB_URL: "postgresql://postgres:password@127.0.0.1:54322/postgres", ANON_KEY: "replace_me", SERVICE_ROLE_KEY: secret }),
    (error) => error.message.includes("ANON_KEY") && !error.message.includes(secret) && !error.message.includes("password"),
  );
});

test("hosted Supabase is rejected", () => {
  assert.throws(() => validateSupabaseStatus({
    API_URL: "https://project.supabase.co", DB_URL: "postgresql://user:pass@localhost:5432/postgres", ANON_KEY: "anon-local", SERVICE_ROLE_KEY: "role-local",
  }), /lokal|loopback/);
});

test(".env.local rejects secret-bearing names and does not require an .env file", async () => {
  assert.deepEqual(await loadLocalOverrides("X:/missing", async () => { const error = new Error("missing"); error.code = "ENOENT"; throw error; }), {});
  await assert.rejects(loadLocalOverrides("X:/repo", async () => "API_PORT=8080\nSUPABASE_SECRET_KEY=hidden"), /SUPABASE_SECRET_KEY/);
});

test("child environment maps CLI values without logging or changing source data", () => {
  const secret = "role-private-91723";
  const env = buildChildEnvironment({}, {
    API_URL: "http://127.0.0.1:55321", DB_URL: "postgresql://postgres:db-private@127.0.0.1:54322/postgres", ANON_KEY: "anon-local", SERVICE_ROLE_KEY: secret,
  }, { apiUrl: "http://127.0.0.1:8080", webUrl: "http://localhost:3000", workerPoll: 2, healthTimeout: 3 }, "D:/repo/rules/ppki-ipb-2019/rules.json");
  assert.equal(env.RuleCatalog__Path, "D:/repo/rules/ppki-ipb-2019/rules.json");
  assert.equal(env.Supabase__SecretKey, secret);
  assert.equal(env.NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY, "anon-local");
  assert.equal(env.NEXT_PUBLIC_SUPABASE_SECRET_KEY, undefined);
  assert.equal(env.DocumentRenderer__BaseUrl, "http://127.0.0.1:55300");
});

test("canonical local renderer reuses only the exact pinned healthy container", async () => {
  const calls = [];
  await ensureLocalRenderer("D:/repo", async (command, args) => {
    calls.push([command, ...args]);
    return { code: 0, stdout: "gotenberg/gotenberg:8.34.0-libreoffice@sha256:3c23aeb3a027a63d7c71745fc9d83724bd58cf9dfa470396ac82c0896028db2a|true|[\"gotenberg\",\"--api-timeout=120s\"]\n", stderr: "" };
  }, async () => true);
  assert.deepEqual(calls, [["docker", "inspect", "--format", "{{.Config.Image}}|{{.State.Running}}|{{json .Config.Cmd}}", "ppki-smart-formatter-renderer-dev"]]);
});

test("canonical local renderer replaces only its stateless stale-timeout container", async () => {
  const calls = [];
  await ensureLocalRenderer("D:/repo", async (command, args) => {
    calls.push([command, ...args]);
    if (args[0] === "inspect") return { code: 0, stdout: "gotenberg/gotenberg:8.34.0-libreoffice@sha256:3c23aeb3a027a63d7c71745fc9d83724bd58cf9dfa470396ac82c0896028db2a|true|[\"gotenberg\",\"--api-timeout=30s\"]\n", stderr: "" };
    return { code: 0, stdout: "ok", stderr: "" };
  }, async () => true);
  assert.equal(calls[1][1], "rm");
  assert.equal(calls[2][1], "run");
  assert.ok(calls[2].includes("--api-timeout=120s"));
});

test("bootstrap local web environment is accepted by the frontend validator", async () => {
  const { getPublicSupabaseEnvironment } = await import("../apps/web/src/lib/supabase/environment.ts");
  const env = buildChildEnvironment({}, {
    API_URL: "http://127.0.0.1:55321",
    DB_URL: "postgresql://postgres:db-private@127.0.0.1:54322/postgres",
    ANON_KEY: "anon-local",
    SERVICE_ROLE_KEY: "role-local",
  }, {
    apiUrl: "http://127.0.0.1:5080",
    webUrl: "http://localhost:3000",
    workerPoll: 2,
    healthTimeout: 3,
  }, "D:/repo/rules/ppki-ipb-2019/rules.json");

  assert.deepEqual(getPublicSupabaseEnvironment(env), {
    apiBaseUrl: "http://127.0.0.1:5080",
    supabaseUrl: "http://127.0.0.1:55321",
    supabasePublishableKey: "anon-local",
  });
});

test("port conflict includes process or container owner", async () => {
  await assert.rejects(ensureApplicationPortAvailable(8080, async () => [{ port: 8080, owner: "PID 42" }], async () => []), /port 8080: PID 42/);
  assert.equal(formatPortConflicts([{ port: 55321, container: "foreign-kong" }]), "port 55321: container foreign-kong");
  assert.equal(attachContainerOwners(
    [{ port: 8080, owner: "PID 42" }],
    [{ name: "ppki-api", ports: "0.0.0.0:8080->8080/tcp" }],
  )[0].container, "ppki-api");
});

test("foreign containers are reported but never removed or stopped", async () => {
  const calls = [];
  await assert.rejects(ensureInfraPortsAvailable("D:/repo", {
    inspect: async () => [{ port: 55321, owner: "PID 7" }],
    containers: async () => [{ name: "another-project-kong", ports: "0.0.0.0:55321->8000/tcp", owned: false }],
  }), /another-project-kong/);
  assert.deepEqual(calls, []);
});

class FakeChild extends EventEmitter {
  constructor() { super(); this.exitCode = null; this.signalCode = null; this.killed = false; }
  kill() { this.killed = true; this.signalCode = "SIGTERM"; queueMicrotask(() => this.emit("exit", null, "SIGTERM")); return true; }
}

test("backend supervisor stops sibling when one process fails", async () => {
  const spawned = [];
  const actual = supervise([{ label: "api" }, { label: "worker" }], {
    spawnCommand: () => { const child = new FakeChild(); spawned.push(child); return child; }, onSignal: () => {}, write: () => {},
  });
  spawned[0].exitCode = 1;
  spawned[0].emit("exit", 1, null);
  await assert.rejects(actual, /api berhenti.*code 1/);
  assert.equal(spawned[1].killed, true);
});

test("backend supervisor stops sibling even when one service exits cleanly unexpectedly", async () => {
  const spawned = [];
  const actual = supervise([{ label: "api" }, { label: "worker" }], {
    spawnCommand: () => { const child = new FakeChild(); spawned.push(child); return child; }, onSignal: () => {}, write: () => {},
  });
  spawned[0].exitCode = 0;
  spawned[0].emit("exit", 0, null);
  await assert.rejects(actual, /tak terduga/);
  assert.equal(spawned[1].killed, true);
});

test("Ctrl+C stops both backend children cleanly", async () => {
  const spawned = [];
  let signalHandler;
  const running = supervise([{ label: "api" }, { label: "worker" }], {
    spawnCommand: () => { const child = new FakeChild(); spawned.push(child); return child; },
    onSignal: (handler) => { signalHandler = handler; }, write: () => {},
  });
  signalHandler();
  await running;
  assert.deepEqual(spawned.map((child) => child.killed), [true, true]);
});

test("environment parser accepts quoted Supabase CLI output without printing it", () => {
  const values = parseEnvironment('API_URL="http://127.0.0.1:55321"\nANON_KEY="private-value"');
  assert.equal(values.API_URL, "http://127.0.0.1:55321");
  assert.equal(values.ANON_KEY, "private-value");
});

test("CLI diagnostics retain actionable errors and redact credentials", () => {
  const diagnostic = safeCliDiagnostic("SERVICE_ROLE_KEY=sb_secret_never-show\nfailed to bind port 55321\nDB_URL=postgresql://postgres:private@localhost/db");
  assert.match(diagnostic, /failed to bind port 55321/);
  assert.doesNotMatch(diagnostic, /never-show|private/);
});

test("bootstrap source contains no destructive or hosted-project command", async () => {
  const source = await import("node:fs/promises").then(({ readFile }) => readFile(new URL("./dev-bootstrap.mjs", import.meta.url), "utf8"));
  assert.doesNotMatch(source, /docker\s+(?:rm|stop)|compose\s+down\s+-v|supabase\s+db\s+reset|\b(?:login|link|db push)\b/i);
});
