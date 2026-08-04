import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import path from "node:path";

const ids = Object.freeze({
  ownerA: "94000000-0000-0000-0000-000000000001",
  ownerB: "94000000-0000-0000-0000-000000000002",
  documentA: "94000000-0000-0000-0000-000000000003",
  versionA: "94000000-0000-0000-0000-000000000004",
  auditA: "94000000-0000-0000-0000-000000000005",
  documentB: "94000000-0000-0000-0000-000000000006",
  versionB: "94000000-0000-0000-0000-000000000007",
  auditB: "94000000-0000-0000-0000-000000000008",
  jobA: "94000000-0000-0000-0000-000000000009",
  jobB: "94000000-0000-0000-0000-000000000010",
  documentC: "94000000-0000-0000-0000-000000000011",
  versionC: "94000000-0000-0000-0000-000000000012",
  auditC: "94000000-0000-0000-0000-000000000013",
  transientJob: "94000000-0000-0000-0000-000000000014",
  transientResult: "94000000-0000-0000-0000-000000000015",
  foreignResult: "94000000-0000-0000-0000-000000000016",
  documentType: "10000000-0000-0000-0000-000000000002",
  profileVersion: "21000000-0000-0000-0000-000000000001",
});

const hashA = "a".repeat(64);
const hashB = "b".repeat(64);
const hashC = "c".repeat(64);
const resultHash = "d".repeat(64);
const planA = "1".repeat(64);
const planB = "2".repeat(64);
const planC = "3".repeat(64);

function run(command, args, { capture = false, allowFailure = false } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: process.cwd(), shell: false, stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.resume();
    child.on("error", () => reject(new Error("local command could not start")));
    child.on("close", (code) => {
      if (code === 0 || allowFailure) resolve({ code, stdout: capture ? stdout : "" });
      else reject(new Error("local command failed"));
    });
  });
}

async function projectId() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const match = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m);
  if (!match) throw new Error("local project configuration is invalid");
  return match[1];
}

async function databaseContainer() {
  const result = await run("docker", ["ps", "--filter", `name=supabase_db_${await projectId()}`, "--format", "{{.Names}}"], { capture: true });
  const container = result.stdout.split(/\r?\n/).find(Boolean);
  if (!container) throw new Error("local database is unavailable");
  return container;
}

async function sql(container, statement, { capture = false } = {}) {
  const result = await run("docker", ["exec", container, "psql", "-X", "-q", "-A", "-t", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", statement], { capture });
  return result.stdout.trim();
}

function report(name, passed) {
  console.log(`${name}: ${passed ? "PASS" : "FAIL"}`);
  return passed;
}

function jobValues({ id = ids.transientJob, audit = ids.auditC, source = ids.versionC,
  owner = ids.ownerA, idempotency = "94000000-0000-0000-0000-000000000099", plan = planC } = {}) {
  return `('${id}', '${audit}', '${source}', '${owner}', '${idempotency}', '${plan}', 'fix-plan-v1', '["94000000-0000-0000-0000-000000000098"]', '{"schemaVersion":1}', 'Queued', 1)`;
}

const jobColumns = `(id, audit_job_id, source_document_version_id, requested_by_user_id,
  idempotency_key, plan_hash, planner_version, selected_finding_ids, approved_plan_snapshot, state,
  planned_operation_count)`;

function setupSql() {
  return `
insert into auth.users (id, aud, role, email, raw_app_meta_data, raw_user_meta_data, created_at, updated_at) values
  ('${ids.ownerA}', 'authenticated', 'authenticated', 'fix-smoke-a@example.invalid', '{}', '{}', now(), now()),
  ('${ids.ownerB}', 'authenticated', 'authenticated', 'fix-smoke-b@example.invalid', '{}', '{}', now(), now())
on conflict (id) do nothing;

insert into public.documents (id, owner_user_id, document_type_id, title, current_version_no) values
  ('${ids.documentA}', '${ids.ownerA}', '${ids.documentType}', 'Fix execution smoke A', 1),
  ('${ids.documentB}', '${ids.ownerB}', '${ids.documentType}', 'Fix execution smoke B', 1),
  ('${ids.documentC}', '${ids.ownerA}', '${ids.documentType}', 'Fix execution smoke transient', 1)
on conflict (id) do nothing;

insert into public.document_versions
  (id, document_id, version_no, storage_bucket, storage_key, original_filename, mime_type, size_bytes, sha256, created_by_user_id) values
  ('${ids.versionA}', '${ids.documentA}', 1, 'documents-original', 'fix-smoke/a/source.docx', 'synthetic-a.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1, '${hashA}', '${ids.ownerA}'),
  ('${ids.versionB}', '${ids.documentB}', 1, 'documents-original', 'fix-smoke/b/source.docx', 'synthetic-b.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1, '${hashB}', '${ids.ownerB}'),
  ('${ids.versionC}', '${ids.documentC}', 1, 'documents-original', 'fix-smoke/c/source.docx', 'synthetic-c.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1, '${hashC}', '${ids.ownerA}')
on conflict (id) do nothing;

insert into public.audit_jobs
  (id, document_version_id, profile_version_id, requested_by_user_id, document_kind_snapshot, status,
   resolved_rule_set_hash, applicable_rule_count, started_at, completed_at) values
  ('${ids.auditA}', '${ids.versionA}', '${ids.profileVersion}', '${ids.ownerA}', 'Skripsi', 'Completed', '${hashA}', 0, now(), now()),
  ('${ids.auditB}', '${ids.versionB}', '${ids.profileVersion}', '${ids.ownerB}', 'Skripsi', 'Completed', '${hashB}', 0, now(), now()),
  ('${ids.auditC}', '${ids.versionC}', '${ids.profileVersion}', '${ids.ownerA}', 'Skripsi', 'Completed', '${hashC}', 0, now(), now())
on conflict (id) do nothing;

insert into public.fix_execution_jobs ${jobColumns} values
  ${jobValues({ id: ids.jobA, audit: ids.auditA, source: ids.versionA, owner: ids.ownerA, idempotency: "94000000-0000-0000-0000-000000000091", plan: planA })},
  ${jobValues({ id: ids.jobB, audit: ids.auditB, source: ids.versionB, owner: ids.ownerB, idempotency: "94000000-0000-0000-0000-000000000092", plan: planB })}
on conflict (id) do nothing;

update public.fix_execution_jobs
set state = 'Processing', started_at = coalesce(started_at, now()), lease_expires_at = now() + interval '100 years'
where id in ('${ids.jobA}', '${ids.jobB}') and state = 'Queued';

update public.fix_execution_jobs
set lease_expires_at = now() + interval '100 years'
where id in ('${ids.jobA}', '${ids.jobB}') and state = 'Processing';

do $setup$
begin
  if (select count(*) <> 2 from public.fix_execution_jobs
      where id in ('${ids.jobA}', '${ids.jobB}') and state = 'Processing') then
    raise exception 'bounded smoke fixtures are not reusable';
  end if;
end $setup$;`;
}

function expectError(statement, condition) {
  return `do $expected$
begin
  begin
    ${statement};
    raise exception 'expected database rejection';
  exception when ${condition} then null;
  end;
end $expected$;`;
}

async function assertion(container, name, statement) {
  try {
    const result = await sql(container, statement, { capture: true });
    return report(name, result.split(/\r?\n/).filter(Boolean).at(-1) === "t");
  } catch {
    return report(name, false);
  }
}

async function concurrentClaim(container) {
  await sql(container, `update public.fix_execution_jobs set lease_expires_at = now() - interval '1 second'
    where id in ('${ids.jobA}', '${ids.jobB}') and state = 'Processing';`);
  const claim = `begin;
select id from public.fix_execution_jobs
where (state = 'Queued' or (state = 'Processing' and lease_expires_at < now()))
  and id in ('${ids.jobA}', '${ids.jobB}')
order by created_at, id
for update skip locked
limit 1;
select pg_sleep(2);
rollback;`;
  try {
    const results = await Promise.all([
      sql(container, claim, { capture: true }),
      sql(container, claim, { capture: true }),
    ]);
    const claimed = results.map((value) => value.match(/94000000-0000-0000-0000-0000000000(?:09|10)/)?.[0]);
    return claimed.every(Boolean) && new Set(claimed).size === 2;
  } finally {
    await sql(container, `update public.fix_execution_jobs set lease_expires_at = now() + interval '100 years'
      where id in ('${ids.jobA}', '${ids.jobB}') and state = 'Processing';`);
  }
}

async function main() {
  console.log("SUITE fix-execution-local");
  let container;
  let passed = true;
  try {
    container = await databaseContainer();
    passed = report("local-database-ready", true) && passed;
    await sql(container, setupSql());
    passed = report("bounded-synthetic-fixture-ready", true) && passed;
  } catch {
    report("local-database-ready", false);
    report("bounded-synthetic-fixture-ready", false);
    process.exitCode = 1;
    return;
  }

  passed = await assertion(container, "table-constraints-and-clean-queued-insert", `begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    ${expectError(`insert into public.fix_execution_jobs ${jobColumns} values ${jobValues({ id: "94000000-0000-0000-0000-000000000017", plan: "not-a-hash" })}`, "check_violation")}
    ${expectError(`insert into public.fix_execution_jobs ${jobColumns.slice(0, -1)}, started_at) values (${jobValues({ id: "94000000-0000-0000-0000-000000000018", plan: "4".repeat(64) }).slice(1, -1)}, now())`, "check_violation")}
    ${expectError(`insert into public.fix_execution_jobs ${jobColumns} values ${jobValues({ id: "94000000-0000-0000-0000-000000000021", plan: "7".repeat(64) }).replace("'Queued', 1", "'Unknown', 1")}`, "check_violation")}
    ${expectError(`insert into public.fix_execution_jobs ${jobColumns} values ${jobValues({ id: "94000000-0000-0000-0000-000000000022", plan: "8".repeat(64) }).replace("'Queued', 1", "'Queued', 0")}`, "check_violation")}
    select count(*) = 1 from public.fix_execution_jobs where id = '${ids.transientJob}' and state = 'Queued';
    rollback;`) && passed;

  passed = await assertion(container, "unique-idempotency-and-source-plan", `begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    ${expectError(`insert into public.fix_execution_jobs ${jobColumns} values ${jobValues({ id: "94000000-0000-0000-0000-000000000019", plan: "5".repeat(64) })}`, "unique_violation")}
    ${expectError(`insert into public.fix_execution_jobs ${jobColumns} values ${jobValues({ id: "94000000-0000-0000-0000-000000000020", idempotency: "94000000-0000-0000-0000-000000000093" })}`, "unique_violation")}
    select true;
    rollback;`) && passed;

  passed = await assertion(container, "identity-immutability-and-invalid-transition", `begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    ${expectError(`update public.fix_execution_jobs set plan_hash = '${"6".repeat(64)}' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    ${expectError(`update public.fix_execution_jobs set audit_job_id = '${ids.auditA}' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    ${expectError(`update public.fix_execution_jobs set source_document_version_id = '${ids.versionA}' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    ${expectError(`update public.fix_execution_jobs set requested_by_user_id = '${ids.ownerB}' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    ${expectError(`update public.fix_execution_jobs set idempotency_key = '94000000-0000-0000-0000-000000000094' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    ${expectError(`update public.fix_execution_jobs set planner_version = 'changed' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    ${expectError(`update public.fix_execution_jobs set selected_finding_ids = '["94000000-0000-0000-0000-000000000097"]' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    ${expectError(`update public.fix_execution_jobs set approved_plan_snapshot = '{"schemaVersion":2}' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    ${expectError(`update public.fix_execution_jobs set state = 'Completed', started_at = now(), completed_at = now(), completed_operation_count = 1 where id = '${ids.transientJob}'`, "check_violation")}
    select true;
    rollback;`) && passed;

  passed = await assertion(container, "api-database-role-queued-insert", `begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    select state = 'Queued' from public.fix_execution_jobs where id = '${ids.transientJob}';
    rollback;`) && passed;

  passed = await assertion(container, "worker-completed-lifecycle-and-terminal-guard", `begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    update public.fix_execution_jobs set state = 'Processing', started_at = now(), lease_expires_at = now() + interval '10 minutes' where id = '${ids.transientJob}';
    insert into public.document_versions
      (id, document_id, version_no, storage_bucket, storage_key, original_filename, mime_type, size_bytes, sha256, created_by_user_id, parent_version_id)
    values ('${ids.transientResult}', '${ids.documentC}', 2, 'documents-versions', 'fix-smoke/c/result.docx', 'synthetic-c.docx',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1, '${resultHash}', '${ids.ownerA}', '${ids.versionC}');
    update public.fix_execution_jobs set state = 'Completed', result_document_version_id = '${ids.transientResult}',
      result_sha256 = '${resultHash}', completed_operation_count = 1, lease_expires_at = null, completed_at = now()
    where id = '${ids.transientJob}';
    ${expectError(`update public.fix_execution_jobs set safe_failure_code = 'changed' where id = '${ids.transientJob}'`, "sqlstate '55000'")}
    select state = 'Completed' and result_document_version_id = '${ids.transientResult}'
      from public.fix_execution_jobs where id = '${ids.transientJob}';
    rollback;`) && passed;

  passed = await assertion(container, "nochange-and-failed-lifecycles", `begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    update public.fix_execution_jobs set state = 'Processing', started_at = now(), lease_expires_at = now() + interval '10 minutes' where id = '${ids.transientJob}';
    update public.fix_execution_jobs set state = 'NoChange', completed_operation_count = 1, lease_expires_at = null, completed_at = now() where id = '${ids.transientJob}';
    select state = 'NoChange' from public.fix_execution_jobs where id = '${ids.transientJob}';
    rollback;
    begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    update public.fix_execution_jobs set state = 'Processing', started_at = now(), lease_expires_at = now() + interval '10 minutes' where id = '${ids.transientJob}';
    update public.fix_execution_jobs set state = 'Failed', failed_operation_count = 1, safe_failure_code = 'synthetic-failure', lease_expires_at = null, completed_at = now() where id = '${ids.transientJob}';
    select state = 'Failed' from public.fix_execution_jobs where id = '${ids.transientJob}';
    rollback;`) && passed;

  passed = await assertion(container, "lease-recovery-renews-only-lease", `begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    update public.fix_execution_jobs set state = 'Processing', started_at = now() - interval '20 minutes', lease_expires_at = now() - interval '10 minutes' where id = '${ids.transientJob}';
    update public.fix_execution_jobs set lease_expires_at = now() + interval '10 minutes' where id = '${ids.transientJob}';
    select state = 'Processing' and lease_expires_at > now() from public.fix_execution_jobs where id = '${ids.transientJob}';
    rollback;`) && passed;

  passed = await assertion(container, "result-ownership-chain-rejects-foreign-version", `begin;
    insert into public.fix_execution_jobs ${jobColumns} values ${jobValues()};
    update public.fix_execution_jobs set state = 'Processing', started_at = now(), lease_expires_at = now() + interval '10 minutes' where id = '${ids.transientJob}';
    insert into public.document_versions
      (id, document_id, version_no, storage_bucket, storage_key, original_filename, mime_type, size_bytes, sha256, created_by_user_id, parent_version_id)
    values ('${ids.foreignResult}', '${ids.documentB}', 2, 'documents-versions', 'fix-smoke/b/foreign-result.docx', 'synthetic-b.docx',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 1, '${resultHash}', '${ids.ownerB}', '${ids.versionB}');
    ${expectError(`update public.fix_execution_jobs set state = 'Completed', result_document_version_id = '${ids.foreignResult}', result_sha256 = '${resultHash}', completed_operation_count = 1, lease_expires_at = null, completed_at = now() where id = '${ids.transientJob}'`, "check_violation")}
    select state = 'Processing' from public.fix_execution_jobs where id = '${ids.transientJob}';
    rollback;`) && passed;

  passed = await assertion(container, "rls-own-visible-foreign-hidden", `begin;
    set local role authenticated;
    set local request.jwt.claim.sub = '${ids.ownerA}';
    select count(*) = 1 and bool_and(requested_by_user_id = '${ids.ownerA}')
      from public.fix_execution_jobs where id in ('${ids.jobA}', '${ids.jobB}');
    rollback;`) && passed;

  passed = await assertion(container, "authenticated-writes-denied", `begin;
    set local role authenticated;
    set local request.jwt.claim.sub = '${ids.ownerA}';
    ${expectError(`update public.fix_execution_jobs set lease_expires_at = now() where id = '${ids.jobA}'`, "insufficient_privilege")}
    select not has_table_privilege('authenticated', 'public.fix_execution_jobs', 'INSERT')
      and not has_table_privilege('authenticated', 'public.fix_execution_jobs', 'UPDATE')
      and not has_table_privilege('authenticated', 'public.fix_execution_jobs', 'DELETE');
    rollback;`) && passed;

  try {
    passed = report("concurrent-skip-locked-claims-distinct-jobs", await concurrentClaim(container)) && passed;
  } catch {
    passed = report("concurrent-skip-locked-claims-distinct-jobs", false) && passed;
  }

  passed = await assertion(container, "bounded-fixtures-left-nonclaimable", `select count(*) = 2
    from public.fix_execution_jobs where id in ('${ids.jobA}', '${ids.jobB}')
      and state = 'Processing' and lease_expires_at > now() + interval '50 years';`) && passed;

  process.exitCode = passed ? 0 : 1;
}

main();
