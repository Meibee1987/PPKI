import { spawn } from "node:child_process";

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), stdio: "inherit", shell: false });
    child.once("error", reject);
    child.once("exit", code => code === 0 ? resolve() : reject(new Error(`${command} exited with code ${code}`)));
  });
}

console.log("SUITE streamlined-audit-ui-local-production-contract-e2e");
try {
  await run(process.execPath, ["--test", "--experimental-strip-types", "apps/web/src/lib/streamlined-audit-ui.test.ts"]);
  for (let attempt = 1; attempt <= 2; attempt += 1) {
    console.log(`streamlined-audit-ui-local-run-${attempt}: START`);
    await run(process.execPath, ["scripts/text-correction-batch-smoke-test.mjs"]);
    console.log(`streamlined-audit-ui-local-run-${attempt}: PASS`);
  }
  console.log("API + production frontend contract/component integration smoke PASS; browser verification is reported separately.");
} catch (error) {
  console.log(`BLOCKER: ${error instanceof Error ? error.message : "local runtime unavailable"}`);
  process.exitCode = 1;
}
