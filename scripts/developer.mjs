import { spawn } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const npmCliPath = path.join(path.dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");

function npmStage(args) {
  return process.platform === "win32"
    ? { command: process.execPath, args: [npmCliPath, ...args] }
    : { command: "npm", args };
}

const verificationPublicEnvironment = Object.freeze({
  NEXT_PUBLIC_API_BASE_URL: "http://localhost:8080",
  NEXT_PUBLIC_SUPABASE_URL: "https://verification.supabase.co",
  NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY: "sb_publishable_verification",
});

export const verificationStages = Object.freeze([
  { label: "Restoring backend", command: "dotnet", args: ["restore", "backend/PpkiSmartFormatter.slnx"] },
  { label: "Building backend", command: "dotnet", args: ["build", "backend/PpkiSmartFormatter.slnx", "--no-restore"] },
  { label: "Testing backend", command: "dotnet", args: ["test", "backend/PpkiSmartFormatter.slnx", "--no-build"] },
  { label: "Installing web dependencies", ...npmStage(["--prefix", "apps/web", "ci"]) },
  { label: "Testing web configuration", ...npmStage(["--prefix", "apps/web", "run", "test:config"]) },
  { label: "Type-checking web", ...npmStage(["--prefix", "apps/web", "run", "typecheck"]) },
  { label: "Building web", ...npmStage(["--prefix", "apps/web", "run", "build"]), environment: verificationPublicEnvironment },
  { label: "Validating Compose configuration", command: "docker", args: ["compose", "--env-file", ".env.example", "config", "--quiet"] },
]);

export function execute(command, args, { stdio = "inherit", environment } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: process.cwd(),
      env: environment ? { ...process.env, ...environment } : process.env,
      stdio,
      shell: false,
    });

    child.on("error", (error) => reject(new Error(`Could not start ${command}: ${error.message}`)));
    child.on("close", (code, signal) => {
      if (code === 0) {
        resolve();
        return;
      }

      reject(new Error(`${command} exited with ${signal ? `signal ${signal}` : `code ${code}`}.`));
    });
  });
}

export function capture(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), stdio: ["ignore", "pipe", "pipe"], shell: false });
    let output = "";

    child.stdout.on("data", (chunk) => { output += chunk; });
    child.stderr.resume();
    child.on("error", (error) => reject(new Error(`Could not start ${command}: ${error.message}`)));
    child.on("close", (code) => {
      if (code === 0) {
        resolve(output.trim());
        return;
      }

      reject(new Error(`${command} exited with code ${code}.`));
    });
  });
}

export async function runVerification({ executeCommand = execute, write = console.log } = {}) {
  for (const [index, stage] of verificationStages.entries()) {
    write(`[${index + 1}/${verificationStages.length}] ${stage.label}`);
    await executeCommand(stage.command, stage.args, { environment: stage.environment });
  }
}

export async function checkPrerequisites({ captureCommand = capture, nodeVersion = process.versions.node, write = console.log } = {}) {
  const checks = [
    { label: "Checking Git", command: "git", args: ["--version"] },
    { label: "Checking Docker daemon", command: "docker", args: ["version", "--format", "{{.Server.Version}}"] },
    { label: "Checking Docker Compose", command: "docker", args: ["compose", "version", "--short"] },
    { label: "Checking .NET SDK 10", command: "dotnet", args: ["--version"], versionPrefix: "10." },
  ];

  for (const [index, check] of checks.entries()) {
    write(`[${index + 1}/5] ${check.label}`);
    const version = await captureCommand(check.command, check.args);
    if (check.versionPrefix && !version.startsWith(check.versionPrefix)) {
      throw new Error(`${check.label} requires version ${check.versionPrefix.slice(0, -1)}.`);
    }
  }

  write("[5/5] Checking Node.js 24");
  if (!nodeVersion.startsWith("24.")) {
    throw new Error("Checking Node.js 24 requires version 24.");
  }
}

async function main() {
  const [command] = process.argv.slice(2);
  if (command === "verify") {
    await runVerification();
    return;
  }
  if (command === "prerequisites") {
    await checkPrerequisites();
    return;
  }

  throw new Error("Usage: node scripts/developer.mjs <verify|prerequisites>");
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main().catch((error) => {
    console.error(`Developer command failed: ${error.message}`);
    process.exitCode = 1;
  });
}
