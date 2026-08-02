import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { scanText } from "./secret-scan.mjs";

const migrationFileName = /^(\d{12}|\d{14})_([a-z0-9][a-z0-9_]*)\.sql$/;
const destructiveSql = /\b(?:drop\s+(?:table|schema|database|type|function|extension)|truncate(?:\s+table)?|delete\s+from|alter\s+table\s+\S+\s+drop)\b/gi;
const destructiveMarker = /--\s*migration-hygiene:\s*allow-destructive\s+\S+/i;
const hostedProjectUrl = /https?:\/\/[a-z0-9-]+\.supabase\.co\b/gi;
const decoder = new TextDecoder("utf-8", { fatal: true });

function hasValidTimestamp(value) {
  const year = Number(value.slice(0, 4));
  const month = Number(value.slice(4, 6));
  const day = Number(value.slice(6, 8));
  const hour = Number(value.slice(8, 10));
  const minute = Number(value.slice(10, 12));
  const second = value.length === 14 ? Number(value.slice(12, 14)) : 0;
  const date = new Date(Date.UTC(year, month - 1, day, hour, minute, second));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day && date.getUTCHours() === hour && date.getUTCMinutes() === minute && date.getUTCSeconds() === second;
}

function withoutComments(sql) {
  return sql.replace(/--[^\n]*/g, "").replace(/\/\*[\s\S]*?\*\//g, "").trim();
}

function lineNumber(text, index) {
  return text.slice(0, index).split("\n").length;
}

function issue(file, category, line = undefined) {
  return { file, category, line };
}

export async function checkMigrations({ directory = path.join(process.cwd(), "supabase", "migrations") } = {}) {
  const entries = (await readdir(directory, { withFileTypes: true }))
    .filter((entry) => entry.isFile() && entry.name.endsWith(".sql"))
    .map((entry) => entry.name)
    .sort();
  const findings = [];
  const timestamps = new Map();

  if (entries.length === 0) findings.push(issue(path.relative(process.cwd(), directory) || directory, "missing-migrations"));

  for (const name of entries) {
    const file = path.join(directory, name);
    const relativeFile = path.relative(process.cwd(), file).replaceAll("\\", "/");
    const nameMatch = name.match(migrationFileName);
    if (!nameMatch) {
      findings.push(issue(relativeFile, "invalid-migration-name"));
    } else {
      const timestamp = nameMatch[1];
      if (!hasValidTimestamp(timestamp)) findings.push(issue(relativeFile, "invalid-migration-timestamp"));
      const names = timestamps.get(timestamp) ?? [];
      names.push(relativeFile);
      timestamps.set(timestamp, names);
    }

    let sql;
    try {
      sql = decoder.decode(await readFile(file));
    } catch {
      findings.push(issue(relativeFile, "non-utf8-sql"));
      continue;
    }

    if (!withoutComments(sql)) findings.push(issue(relativeFile, "empty-sql"));
    if (!destructiveMarker.test(sql)) {
      for (const match of sql.matchAll(destructiveSql)) findings.push(issue(relativeFile, "unexplained-destructive-sql", lineNumber(sql, match.index)));
    }
    for (const match of sql.matchAll(hostedProjectUrl)) findings.push(issue(relativeFile, "hosted-project-url", lineNumber(sql, match.index)));
    findings.push(...scanText(sql, { file: relativeFile }).map(({ category, line }) => issue(relativeFile, category, line)));
  }

  for (const files of timestamps.values()) {
    if (files.length > 1) files.forEach((file) => findings.push(issue(file, "duplicate-migration-timestamp")));
  }

  return findings;
}

export function formatFindings(findings) {
  return findings.map(({ file, category, line }) => `${file}${line ? `:${line}` : ""} [${category}]`).join("\n");
}

export async function main({ directory, writeError = console.error } = {}) {
  const findings = await checkMigrations({ directory });
  if (findings.length === 0) return 0;

  writeError(`Migration hygiene check failed:\n${formatFindings(findings)}`);
  return 1;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const directoryIndex = process.argv.indexOf("--dir");
  const directory = directoryIndex === -1 ? undefined : process.argv[directoryIndex + 1];
  main({ directory }).then((exitCode) => { process.exitCode = exitCode; }).catch((error) => {
    console.error(`Migration hygiene check failed: ${error.message}`);
    process.exitCode = 1;
  });
}
