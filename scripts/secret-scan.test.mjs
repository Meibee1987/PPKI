import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  formatFindings,
  isTestOrFixtureFile,
  maskValue,
  scanText,
  scanTrackedFiles,
  trackedEnvironmentFiles,
} from "./secret-scan.mjs";

test("secret scanner accepts documented placeholders", () => {
  const findings = scanText("SUPABASE_SECRET_KEY=sb_secret_REPLACE_ME\nPassword=REPLACE_ME", { file: ".env.example" });
  assert.deepEqual(findings, []);
});

test("secret scanner accepts runtime-derived integration passwords", () => {
  const source = [
    "Password=${decodeURIComponent(parsed.password)};SSL Mode=Disable",
    "const password = `S1T06-${randomUUID()}-local`;",
  ].join("\n");

  assert.deepEqual(scanText(source, { file: "scripts/security-integration-test.mjs" }), []);
});

test("secret scanner still rejects hard-coded passwords", () => {
  const findings = scanText([
    "Password=committed-credential",
    "const password = `committed-credential`;",
  ].join("\n"), { file: "settings.mjs" });

  assert.equal(findings.length, 2);
  assert.ok(findings.every((finding) => finding.category === "connection-password"));
});

test("secret scanner rejects a synthetic secret without retaining its value", () => {
  const secret = ["sb_secret_", "synthetic", "_1234567890"].join("");
  const findings = scanText(`SUPABASE_SECRET_KEY=${secret}`, { file: "settings.txt" });

  assert.equal(findings.length, 2);
  assert.ok(findings.every((finding) => finding.preview !== secret));
  assert.doesNotMatch(JSON.stringify(findings), new RegExp(secret));
  assert.doesNotMatch(formatFindings(findings), new RegExp(secret));
});

test("masking keeps only a minimal identifier fragment", () => {
  assert.equal(maskValue("abcdefghi"), "abc…hi");
});

test("repository scan excludes tests and fixtures that exercise forbidden patterns", () => {
  assert.equal(isTestOrFixtureFile("apps/web/src/lib/environment.test.ts"), true);
  assert.equal(isTestOrFixtureFile("backend/tests/Ppki.Tests/example.cs"), true);
  assert.equal(isTestOrFixtureFile("docs/security.md"), false);
});

test("tracked environment policy accepts only the two exact root templates", () => {
  assert.deepEqual(trackedEnvironmentFiles([
    ".env.example",
    ".env.local.example",
    ".env",
    ".env.local",
    ".env.development",
    ".env.production",
    ".env.local.example.backup",
    "nested/.env.local",
    "nested/.env.example",
  ]), [
    ".env",
    ".env.local",
    ".env.development",
    ".env.production",
    ".env.local.example.backup",
    "nested/.env.local",
    "nested/.env.example",
  ]);
});

test("both exact example templates remain subject to content scanning", async () => {
  const safeFindings = await scanTrackedFiles(
    [".env.example", ".env.local.example"],
    async () => "SUPABASE_SECRET_KEY=sb_secret_REPLACE_ME\nPassword=REPLACE_ME",
  );
  assert.deepEqual(safeFindings, []);

  const syntheticSecret = ["sb_secret_", "ciLeakMarker", "_1234567890"].join("");
  const unsafeFindings = await scanTrackedFiles(
    [".env.local.example"],
    async () => `SUPABASE_SECRET_KEY=${syntheticSecret}`,
  );
  assert.ok(unsafeFindings.some((finding) => finding.category === "supabase-secret-key"));
  assert.ok(unsafeFindings.some((finding) => finding.category === "service-role-key"));
  assert.doesNotMatch(JSON.stringify(unsafeFindings), new RegExp(syntheticSecret));
});

test("secret scan CLI exits non-zero for a tracked environment file", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "ppki-secret-scan-"));
  await writeFile(path.join(root, ".env"), "SAFE=1\n");
  spawnSync("git", ["init", "--quiet"], { cwd: root });
  spawnSync("git", ["add", ".env"], { cwd: root });

  const script = fileURLToPath(new URL("./secret-scan.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script], { cwd: root, encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /tracked-env-file/);
});
