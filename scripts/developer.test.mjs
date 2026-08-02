import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { checkPrerequisites, runVerification, verificationStages } from "./developer.mjs";

test("verification runs every canonical stage in order", async () => {
  const calls = [];
  const messages = [];

  await runVerification({
    executeCommand: async (command, args) => calls.push([command, args]),
    write: (message) => messages.push(message),
  });

  assert.deepEqual(calls, verificationStages.map(({ command, args }) => [command, args]));
  assert.equal(messages[0], "[1/8] Restoring backend");
  assert.equal(messages.at(-1), "[8/8] Validating Compose configuration");
});

test("verification stops and rejects when a stage fails", async () => {
  const calls = [];

  await assert.rejects(
    runVerification({
      executeCommand: async (command) => {
        calls.push(command);
        if (command === "dotnet" && calls.length === 2) throw new Error("simulated failure");
      },
      write: () => {},
    }),
    /simulated failure/,
  );

  assert.deepEqual(calls, ["dotnet", "dotnet"]);
});

test("prerequisite checks accept the required tool versions without reading environment values", async () => {
  const calls = [];

  await checkPrerequisites({
    nodeVersion: "24.10.0",
    captureCommand: async (command, args) => {
      calls.push([command, args]);
      return command === "dotnet" ? "10.0.100" : "available";
    },
    write: () => {},
  });

  assert.equal(calls.length, 4);
  assert.ok(!verificationStages.flatMap((stage) => stage.args).includes(".env"));
});

test("developer script does not load local environment files or print process environment", async () => {
  const source = await readFile(new URL("./developer.mjs", import.meta.url), "utf8");

  assert.doesNotMatch(source, /(?:dotenv|loadEnvConfig)\b/);
  assert.doesNotMatch(source, /console\.(?:log|error)\(.*process\.env/);
});
