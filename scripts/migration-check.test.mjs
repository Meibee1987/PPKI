import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { checkMigrations } from "./migration-check.mjs";

async function migrationDirectory() {
  return mkdtemp(path.join(os.tmpdir(), "ppki-migration-check-"));
}

test("migration checker accepts a valid offline migration", async () => {
  const directory = await migrationDirectory();
  await writeFile(path.join(directory, "202608010001_create_notes.sql"), "create table public.notes (id uuid primary key);\n");

  assert.deepEqual(await checkMigrations({ directory }), []);
});

test("migration checker rejects duplicate timestamps", async () => {
  const directory = await migrationDirectory();
  await writeFile(path.join(directory, "202608010001_first.sql"), "select 1;\n");
  await writeFile(path.join(directory, "202608010001_second.sql"), "select 2;\n");

  const findings = await checkMigrations({ directory });
  assert.equal(findings.filter((finding) => finding.category === "duplicate-migration-timestamp").length, 2);
});

test("migration checker rejects empty SQL files", async () => {
  const directory = await migrationDirectory();
  await writeFile(path.join(directory, "202608010001_blank.sql"), "-- intentionally blank\n");

  assert.ok((await checkMigrations({ directory })).some((finding) => finding.category === "empty-sql"));
});

test("migration checker CLI exits non-zero for a violation", async () => {
  const directory = await migrationDirectory();
  await writeFile(path.join(directory, "202608010001_blank.sql"), "\n");

  const script = fileURLToPath(new URL("./migration-check.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script, "--dir", directory], { encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /empty-sql/);
});
