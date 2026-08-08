import { spawnSync } from "node:child_process";

const commands = [
  ["remediation-hardening", "scripts/remediation-hardening-smoke-test.mjs"],
  ["re-audit", "scripts/reaudit-smoke-test.mjs"],
  ["comparison", "scripts/audit-comparison-smoke-test.mjs"],
  ["resolution", "scripts/finding-resolution-smoke-test.mjs"],
  ["review", "scripts/finding-review-smoke-test.mjs"],
];
console.log("SUITE remediation-ui-local-api-backed");
for (const [name, script] of commands) {
  const result = spawnSync(process.execPath, [script], { cwd: process.cwd(), encoding: "utf8", timeout: 180_000 });
  if (result.status !== 0) {
    console.log(`${name}: FAIL`);
    const safe = `${result.stdout ?? ""}\n${result.stderr ?? ""}`.split(/\r?\n/).filter(line => /BLOCKER|FAIL|error/i.test(line)).slice(-8).join(" | ").slice(0, 1600);
    if (safe) console.log(safe.replace(/[0-9a-f]{8}-[0-9a-f-]{27}/gi, "[uuid]"));
    process.exit(1);
  }
  console.log(`${name}: PASS`);
}
console.log("remediation-ui-local-api-backed: PASS");
