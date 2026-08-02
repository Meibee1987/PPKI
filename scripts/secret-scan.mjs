import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const safePlaceholder = /(?:^|[_-])(?:replace(?:[_-]?me)?|your(?:[_-]?(?:key|token|project(?:[_-]?ref)?))?|example|dummy|fixture|verification|test)(?:$|[_-])/i;
const safeDocumentation = /\b(?:placeholder|dummy|example|fixture|synthetic)\b/i;
const secretKeyPattern = /\bsb_secret_([A-Za-z0-9_-]+)/g;
const serviceRoleAssignmentPattern = /\b(?:SUPABASE_(?:SECRET|SERVICE_ROLE)_KEY|SERVICE_ROLE_KEY)\s*[:=]\s*["']?([^\s;"']+)/gi;
const bearerTokenPattern = /\bBearer\s+(eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)/g;
const passwordAssignmentPattern = /\bpassword\s*=\s*([^;\r\n]+)/gi;
const connectionUrlPattern = /\b(?:postgres(?:ql)?|mysql):\/\/[^\s:@/]+:([^\s@/]+)@/gi;
const privateKeyPattern = /-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----/g;

function lineNumber(text, index) {
  return text.slice(0, index).split("\n").length;
}

function isSafePlaceholder(value, text, index) {
  const line = text.slice(text.lastIndexOf("\n", index) + 1, text.indexOf("\n", index) === -1 ? text.length : text.indexOf("\n", index));
  return safePlaceholder.test(value) || safeDocumentation.test(line);
}

export function maskValue(value) {
  if (value.length <= 6) return "***";
  return `${value.slice(0, 3)}…${value.slice(-2)}`;
}

function finding({ category, file, text, index, value = "" }) {
  return {
    category,
    file,
    line: lineNumber(text, index),
    preview: value ? maskValue(value) : undefined,
  };
}

function scanPattern(text, file, pattern, category, valueFromMatch) {
  const findings = [];
  for (const match of text.matchAll(pattern)) {
    const value = valueFromMatch(match).trim().replace(/^["']|["']$/g, "");
    if (!isSafePlaceholder(value, text, match.index)) {
      findings.push(finding({ category, file, text, index: match.index, value }));
    }
  }
  return findings;
}

export function scanText(text, { file = "<memory>" } = {}) {
  return [
    ...scanPattern(text, file, secretKeyPattern, "supabase-secret-key", (match) => match[1]),
    ...scanPattern(text, file, serviceRoleAssignmentPattern, "service-role-key", (match) => match[1]),
    ...scanPattern(text, file, bearerTokenPattern, "bearer-jwt", (match) => match[1]),
    ...scanPattern(text, file, passwordAssignmentPattern, "connection-password", (match) => match[1]),
    ...scanPattern(text, file, connectionUrlPattern, "connection-password", (match) => match[1]),
    ...[...text.matchAll(privateKeyPattern)].map((match) => finding({ category: "private-key-pem", file, text, index: match.index })),
  ];
}

export function trackedEnvironmentFiles(files) {
  return files.filter((file) => {
    const baseName = path.posix.basename(file.replaceAll("\\", "/"));
    return (baseName === ".env" || baseName.startsWith(".env.")) && baseName !== ".env.example";
  });
}

export function isTestOrFixtureFile(file) {
  const normalized = file.replaceAll("\\", "/");
  return /(?:^|\/)(?:tests?|__tests__|fixtures?)(?:\/|$)|\.(?:test|spec)\.[^/]+$/i.test(normalized);
}

export function scanTrackedFiles(files, readText) {
  const findings = trackedEnvironmentFiles(files).map((file) => ({ category: "tracked-env-file", file }));
  return Promise.all(files.filter((file) => !isTestOrFixtureFile(file)).map(async (file) => scanText(await readText(file), { file }))).then((scans) => [...findings, ...scans.flat()]);
}

export function gitTrackedFiles(root) {
  return new Promise((resolve, reject) => {
    const child = spawn("git", ["ls-files", "-z"], { cwd: root, shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    let errorOutput = "";
    child.stdout.on("data", (chunk) => { output += chunk; });
    child.stderr.on("data", (chunk) => { errorOutput += chunk; });
    child.on("error", (error) => reject(new Error(`Could not list tracked files: ${error.message}`)));
    child.on("close", (code) => {
      if (code === 0) resolve(output.split("\0").filter(Boolean));
      else reject(new Error(`Could not list tracked files${errorOutput ? ": Git returned an error." : "."}`));
    });
  });
}

export async function checkSecretHygiene({ root = process.cwd(), files = undefined } = {}) {
  const trackedFiles = files ?? await gitTrackedFiles(root);
  return scanTrackedFiles(trackedFiles, async (file) => readFile(path.join(root, file), "utf8"));
}

export function formatFindings(findings) {
  return findings.map(({ category, file, line }) => `${file}${line ? `:${line}` : ""} [${category}]`).join("\n");
}

export async function main({ root = process.cwd(), writeError = console.error } = {}) {
  const findings = await checkSecretHygiene({ root });
  if (findings.length === 0) return 0;

  writeError(`Secret hygiene check failed:\n${formatFindings(findings)}`);
  return 1;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main().then((exitCode) => { process.exitCode = exitCode; }).catch((error) => {
    console.error(`Secret hygiene check failed: ${error.message}`);
    process.exitCode = 1;
  });
}
