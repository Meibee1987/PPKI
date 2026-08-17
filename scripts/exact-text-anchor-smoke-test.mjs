import { spawnSync } from "node:child_process";

const result = spawnSync("dotnet", [
  "test",
  "backend/tests/Ppki.RuleEngine.Tests/Ppki.RuleEngine.Tests.csproj",
  "--no-restore",
  "--filter",
  "FullyQualifiedName~ExactTextAnchorTests"
], { cwd: process.cwd(), encoding: "utf8", stdio: "inherit" });

if (result.error) throw result.error;
if (result.status !== 0) process.exit(result.status ?? 1);

console.log("exact-text-anchor-local: PASS");
console.log("proof=real-golden-docx,parser-4.0,duplicates,split-runs,hyperlink,page-map-location,read-only,deterministic-hashes");
