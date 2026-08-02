import { spawn } from "node:child_process";
import path from "node:path";
import { randomUUID } from "node:crypto";

const originalBucket = "documents-original";
const docxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
const syntheticUsers = [
  { email: "user-a@example.invalid", password: "Synthetic-passphrase-01" },
  { email: "user-b@example.invalid", password: "Synthetic-passphrase-01" },
];

function report(name, passed) {
  console.log(`${name}: ${passed ? "PASS" : "FAIL"}`);
  return passed;
}

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.resume();
    child.on("error", () => reject(new Error("local command could not start")));
    child.on("close", (code) => code === 0 ? resolve(stdout) : reject(new Error("local command failed")));
  });
}

async function environment() {
  const args = process.platform === "win32"
    ? [path.join(path.dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js"), "exec", "--", "supabase", "status", "-o", "env"]
    : ["supabase", "status", "-o", "env"];
  const command = process.platform === "win32" ? process.execPath : "npx";
  const output = await run(command, args);
  const values = new Map(output.split(/\r?\n/).flatMap((line) => {
    const separator = line.indexOf("=");
    return separator > 0 ? [[line.slice(0, separator), line.slice(separator + 1).replace(/^"|"$/g, "")]] : [];
  }));
  const required = ["API_URL", "PUBLISHABLE_KEY", "SECRET_KEY", "SERVICE_ROLE_KEY"];
  if (required.some((name) => !values.get(name))) throw new Error("local stack unavailable");
  return Object.fromEntries(required.map((name) => [name, values.get(name)]));
}

function headers(apiKey, token) {
  return { apikey: apiKey, ...(token ? { authorization: `Bearer ${token}` } : {}) };
}

async function fetchLocal(url, options) {
  try { return await fetch(url, options); } catch { throw new Error("local Storage API unavailable"); }
}

async function requiredJson(response) {
  if (!response.ok) throw new Error("local admin request failed");
  return response.json();
}

async function removeExistingUser(env, email) {
  const list = await requiredJson(await fetchLocal(`${env.API_URL}/auth/v1/admin/users?page=1&per_page=1000`, {
    headers: headers(env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY),
  }));
  const match = list.users?.find((user) => user.email === email);
  if (!match) return;
  const response = await fetchLocal(`${env.API_URL}/auth/v1/admin/users/${match.id}`, {
    method: "DELETE",
    headers: headers(env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY),
  });
  if (!response.ok) throw new Error("synthetic user cleanup failed");
}

async function createUser(env, user) {
  const response = await fetchLocal(`${env.API_URL}/auth/v1/admin/users`, {
    method: "POST",
    headers: { ...headers(env.SERVICE_ROLE_KEY, env.SERVICE_ROLE_KEY), "content-type": "application/json" },
    body: JSON.stringify({ email: user.email, password: user.password, email_confirm: true }),
  });
  return requiredJson(response);
}

async function tokenFor(env, user) {
  const response = await fetchLocal(`${env.API_URL}/auth/v1/token?grant_type=password`, {
    method: "POST",
    headers: { ...headers(env.PUBLISHABLE_KEY), "content-type": "application/json" },
    body: JSON.stringify({ email: user.email, password: user.password }),
  });
  const session = await requiredJson(response);
  if (!session.access_token) throw new Error("local sign-in failed");
  return session.access_token;
}

async function main() {
  let env;
  let objectPath;
  let createdUsers = [];
  let passed = true;
  try {
    env = await environment();
    passed = report("local-storage-stack-ready", true) && passed;
  } catch {
    report("local-storage-stack-ready", false);
    report("synthetic-storage-cleanup", true);
    process.exitCode = 1;
    return;
  }

  try {
    for (const user of syntheticUsers) await removeExistingUser(env, user.email);
    createdUsers = await Promise.all(syntheticUsers.map((user) => createUser(env, user)));
    const [tokenA, tokenB] = await Promise.all(syntheticUsers.map((user) => tokenFor(env, user)));
    objectPath = `${randomUUID().toLowerCase()}/${randomUUID().toLowerCase()}/${randomUUID().toLowerCase()}/original.docx`;

    const bucket = await fetchLocal(`${env.API_URL}/storage/v1/bucket/${originalBucket}`, { headers: headers(env.SECRET_KEY) });
    const bucketConfig = bucket.ok ? await bucket.json() : null;
    passed = report("original-bucket-private", bucketConfig?.public === false) && passed;

    const upload = await fetchLocal(`${env.API_URL}/storage/v1/object/${originalBucket}/${objectPath}`, {
      method: "POST",
      headers: { ...headers(env.SECRET_KEY), "content-type": docxMime, "x-upsert": "false" },
      body: new Uint8Array([0x50, 0x4b, 0x03, 0x04]),
    });
    passed = report("service-fixture-upload", upload.ok) && passed;

    const directUrl = `${env.API_URL}/storage/v1/object/authenticated/${originalBucket}/${objectPath}`;
    const anonRead = await fetchLocal(directUrl, { headers: headers(env.PUBLISHABLE_KEY) });
    passed = report("anon-storage-read-denied", !anonRead.ok) && passed;
    const userARead = await fetchLocal(directUrl, { headers: headers(env.PUBLISHABLE_KEY, tokenA) });
    const userBRead = await fetchLocal(directUrl, { headers: headers(env.PUBLISHABLE_KEY, tokenB) });
    passed = report("user-a-storage-read-denied", !userARead.ok) && passed;
    passed = report("user-b-storage-read-denied", !userBRead.ok) && passed;

    const directUpload = await fetchLocal(`${env.API_URL}/storage/v1/object/${originalBucket}/${objectPath}.copy`, {
      method: "POST",
      headers: { ...headers(env.PUBLISHABLE_KEY, tokenA), "content-type": docxMime, "x-upsert": "false" },
      body: new Uint8Array([0x50, 0x4b, 0x03, 0x04]),
    });
    passed = report("authenticated-storage-upload-denied", !directUpload.ok) && passed;

    const directDelete = await fetchLocal(`${env.API_URL}/storage/v1/object/${originalBucket}/${objectPath}`, {
      method: "DELETE",
      headers: headers(env.PUBLISHABLE_KEY, tokenA),
    });
    passed = report("authenticated-storage-delete-denied", !directDelete.ok) && passed;

    const signed = await fetchLocal(`${env.API_URL}/storage/v1/object/sign/${originalBucket}/${objectPath}`, {
      method: "POST",
      headers: { ...headers(env.SECRET_KEY), "content-type": "application/json" },
      body: JSON.stringify({ expiresIn: 120 }),
    });
    const signedBody = signed.ok ? await signed.json() : null;
    const relativeUrl = signedBody?.signedURL ?? signedBody?.signedUrl;
    passed = report("server-signed-url-created", typeof relativeUrl === "string" && relativeUrl.length > 0) && passed;
    if (relativeUrl) {
      const signedRead = await fetchLocal(`${env.API_URL}/storage/v1${relativeUrl}`, {});
      passed = report("signed-url-read", signedRead.ok) && passed;
    } else {
      passed = report("signed-url-read", false) && passed;
    }
  } catch {
    passed = report("storage-smoke-execution", false) && passed;
  } finally {
    let cleaned = true;
    if (env && objectPath) {
      try {
        const response = await fetchLocal(`${env.API_URL}/storage/v1/object/${originalBucket}/${objectPath}`, {
          method: "DELETE",
          headers: headers(env.SECRET_KEY),
        });
        if (!response.ok) cleaned = false;
      } catch { cleaned = false; }
    }
    if (env) {
      for (const user of syntheticUsers) {
        try { await removeExistingUser(env, user.email); } catch { cleaned = false; }
      }
    }
    report("synthetic-storage-cleanup", cleaned);
    passed = cleaned && passed;
  }
  process.exitCode = passed ? 0 : 1;
}

main();
