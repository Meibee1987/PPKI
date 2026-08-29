import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import path from "node:path";

const id = Object.freeze({
  owner: "9a800000-0000-4000-8000-000000000001",
  other: "9a800000-0000-4000-8000-000000000002",
  document: "9a800000-0000-4000-8000-000000000003",
  source: "9a800000-0000-4000-8000-000000000004",
  audit: "9a800000-0000-4000-8000-000000000005",
  findingA: "9a800000-0000-4000-8000-000000000006",
  findingB: "9a800000-0000-4000-8000-000000000007",
  plan: "9a800000-0000-4000-8000-000000000008",
  itemA: "9a800000-0000-4000-8000-000000000009",
  itemB: "9a800000-0000-4000-8000-000000000010",
  snapshot: "9a800000-0000-4000-8000-000000000011",
  job: "9a800000-0000-4000-8000-000000000012",
  claim1: "9a800000-0000-4000-8000-000000000013",
  claim2: "9a800000-0000-4000-8000-000000000014",
  failedA: "9a800000-0000-4000-8000-000000000015",
  failedB: "9a800000-0000-4000-8000-000000000016",
  appliedA: "9a800000-0000-4000-8000-000000000017",
  skippedB: "9a800000-0000-4000-8000-000000000018",
});
const sha = "a".repeat(64);
const resultSha = "b".repeat(64);
const planHash = "c".repeat(64);

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), shell: false,
      stdio: ["ignore", "pipe", "pipe"] });
    let stdout = ""; let stderr = "";
    child.stdout.on("data", chunk => { stdout += chunk; });
    child.stderr.on("data", chunk => { stderr += chunk; });
    child.on("error", () => reject(new Error("local command could not start")));
    child.on("close", code => code === 0 ? resolve(stdout.trim())
      : reject(new Error(`local command failed (${code}): ${stderr.trim()}`)));
  });
}

async function container() {
  const config = await readFile(path.join(process.cwd(), "supabase", "config.toml"), "utf8");
  const project = config.match(/^project_id\s*=\s*"([a-z0-9-]+)"/m)?.[1];
  if (!project) throw new Error("local project configuration is invalid");
  const names = await run("docker", ["ps", "--filter", `name=supabase_db_${project}`,
    "--format", "{{.Names}}"]);
  const value = names.split(/\r?\n/).find(Boolean);
  if (!value) throw new Error("local database is unavailable");
  return value;
}

function operation(itemId) {
  return `{"itemId":"${itemId}","validationKey":"body.font-size","capabilityId":"body-font-size-direct","capabilityVersion":"1.0","operation":{"ordinal":1,"propertyIdentifier":"run.font-size","target":{"scope":"main-document-run","bodyElementIndex":0,"sectionIndex":0,"paragraphIndex":0,"runIndex":0}}}`;
}

const anchor = `'{"schemaVersion":"fix-structural-anchor/1.0","scope":"main-document-run","bodyElementIndex":0,"sectionIndex":0,"paragraphIndex":0,"runIndex":0}'::jsonb`;
const before = `'{"schemaVersion":"fix-item-value/1.0","property":"run.font-size","valueType":"half-points","value":"20"}'::jsonb`;
const after = `'{"schemaVersion":"fix-item-value/1.0","property":"run.font-size","valueType":"half-points","value":"24"}'::jsonb`;
const columns = `(id,fix_execution_job_id,fix_plan_id,fix_plan_item_id,source_document_version_id,
  result_document_version_id,attempt_number,claim_token,operation_ordinal,outcome,validation_key,
  fix_key,fixer_version,property_identifier,structural_anchor,before_payload,after_payload,safe_failure_code)`;

function failed(resultId, itemId) {
  return `('${resultId}','${id.job}','${id.plan}','${itemId}','${id.source}',null,1,'${id.claim1}',1,
    'Failed','body.font-size','body-font-size-direct','1.0','run.font-size',${anchor},null,null,'storage-upload-transient')`;
}

function expectError(statement, state) {
  return `do $test$ begin begin ${statement}; raise exception 'expected failure';
    exception when sqlstate '${state}' then null; end; end $test$`;
}

const sql = `
begin;
insert into auth.users(id,aud,role,email,raw_app_meta_data,raw_user_meta_data,created_at,updated_at) values
 ('${id.owner}','authenticated','authenticated','s8t08-owner@example.invalid','{}','{}',now(),now()),
 ('${id.other}','authenticated','authenticated','s8t08-other@example.invalid','{}','{}',now(),now());
update public.user_profiles set role='PPKIAdmin' where id in ('${id.owner}','${id.other}');
insert into public.documents(id,owner_user_id,document_type_id,title,current_version_no)
 values('${id.document}','${id.owner}','10000000-0000-0000-0000-000000000002','S8-T08 synthetic',1);
insert into public.document_versions(id,document_id,version_no,storage_bucket,storage_key,original_filename,
 mime_type,size_bytes,sha256,created_by_user_id,parent_version_id)
 values('${id.source}','${id.document}',1,'documents-original','s8-t08/source.docx','synthetic.docx',
 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',1,'${sha}','${id.owner}',null);
insert into public.audit_jobs(id,document_version_id,profile_version_id,requested_by_user_id,
 document_kind_snapshot,status,resolved_rule_set_hash,applicable_rule_count,total_rules,error_count,started_at,completed_at)
 values('${id.audit}','${id.source}','21000000-0000-0000-0000-000000000001','${id.owner}',
 'Skripsi','Completed','${sha}',0,0,2,now(),now());
insert into public.audit_findings(id,audit_job_id,rule_id,severity,rule_code_snapshot,fix_mode_snapshot,
 message,actual_value,expected_value,location,status)
 select '${id.findingA}','${id.audit}',id,'Error',rule_code,'Auto','Synthetic safe finding','{}','{}','{}','Open'
 from public.rules order by id limit 1;
insert into public.audit_findings(id,audit_job_id,rule_id,severity,rule_code_snapshot,fix_mode_snapshot,
 message,actual_value,expected_value,location,status)
 select '${id.findingB}','${id.audit}',id,'Error',rule_code,'Auto','Synthetic safe finding','{}','{}','{}','Open'
 from public.rules order by id limit 1;
insert into public.fix_plans(id,source_audit_job_id,source_document_version_id,owner_user_id,
 idempotency_key,request_hash,state,created_at,updated_at)
 values('${id.plan}','${id.audit}','${id.source}','${id.owner}','${id.plan}','${sha}','Draft',now(),now());
insert into public.fix_plan_items(id,fix_plan_id,finding_id) values
 ('${id.itemA}','${id.plan}','${id.findingA}'),('${id.itemB}','${id.plan}','${id.findingB}');
insert into public.fix_plan_approval_snapshots(id,fix_plan_id,schema_version,plan_hash,approval_request_hash,
 source_version_sha256,snapshot,approved_by_user_id,approved_at,created_at)
 values('${id.snapshot}','${id.plan}','fix-plan-approved-snapshot/1.0','${planHash}','${sha}','${sha}',
 '{"items":[${operation(id.itemA)},${operation(id.itemB)}]}'::jsonb,'${id.owner}',now(),now());
update public.fix_plans set state='Approved',approver_user_id='${id.owner}',approved_at=now(),
 updated_at=now()+interval '1 millisecond' where id='${id.plan}';
insert into public.fix_execution_jobs(id,fix_plan_id,audit_job_id,source_document_version_id,requested_by_user_id,
 idempotency_key,plan_hash,planner_version,selected_finding_ids,approved_plan_snapshot,state,planned_operation_count)
 values('${id.job}','${id.plan}','${id.audit}','${id.source}','${id.owner}','${id.plan}','${planHash}',
 'fix-plan-approved-snapshot/1.0','["${id.findingA}","${id.findingB}"]','{"schemaVersion":"fix-execution-plan/1.1"}', 'Queued',1);
update public.fix_plans set state='Applying',applying_at=now()+interval '2 milliseconds',
 updated_at=now()+interval '2 milliseconds' where id='${id.plan}';
update public.fix_execution_jobs set state='Processing',claim_token='${id.claim1}',attempt_count=1,
 started_at=now(),lease_expires_at=now()+interval '10 minutes' where id='${id.job}';
insert into public.fix_item_results ${columns} values ${failed(id.failedA,id.itemA)},${failed(id.failedB,id.itemB)};
${expectError(`insert into public.fix_item_results ${columns} values ${failed(id.failedA,id.itemA)}`, "23505")};
${expectError(`insert into public.fix_item_results ${columns} values ${failed("9a800000-0000-4000-8000-000000000019","9a800000-0000-4000-8000-000000000099")}`, "23514")};
update public.fix_execution_jobs set state='Queued',claim_token=null,lease_expires_at=null,
 failure_category='TransientInfrastructure',safe_failure_code='storage-upload-transient',
 next_attempt_at=now() where id='${id.job}';
update public.fix_execution_jobs set state='Processing',claim_token='${id.claim2}',attempt_count=2,
 lease_expires_at=now()+interval '10 minutes',next_attempt_at=null,failure_category=null,safe_failure_code=null
 where id='${id.job}';
insert into public.document_versions(id,document_id,version_no,storage_bucket,storage_key,original_filename,
 mime_type,size_bytes,sha256,created_by_user_id,parent_version_id)
 values('${id.job}','${id.document}',2,'documents-versions','s8-t08/result.docx','synthetic.docx',
 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',1,'${resultSha}','${id.owner}','${id.source}');
update public.documents set current_version_no=2 where id='${id.document}';
insert into public.fix_item_results ${columns} values
 ('${id.appliedA}','${id.job}','${id.plan}','${id.itemA}','${id.source}','${id.job}',2,'${id.claim2}',1,
  'Applied','body.font-size','body-font-size-direct','1.0','run.font-size',${anchor},${before},${after},null),
 ('${id.skippedB}','${id.job}','${id.plan}','${id.itemB}','${id.source}','${id.job}',2,'${id.claim2}',1,
  'Skipped','body.font-size','body-font-size-direct','1.0','run.font-size',${anchor},${after},${after},null);
update public.fix_execution_jobs set state='Completed',result_document_version_id='${id.job}',
 result_sha256='${resultSha}',result_object_size=1,completed_operation_count=1,claim_token=null,
 lease_expires_at=null,completed_at=now() where id='${id.job}';
update public.fix_plans set state='Completed',completed_at=now()+interval '3 milliseconds',
 updated_at=now()+interval '3 milliseconds' where id='${id.plan}';
set constraints trg_fix_execution_jobs_item_result_aggregate immediate;
set constraints trg_fix_execution_jobs_item_result_aggregate deferred;
${expectError(`update public.fix_item_results set fixer_version='2.0' where id='${id.appliedA}'`, "55000")};
${expectError(`delete from public.fix_item_results where id='${id.appliedA}'`, "55000")};
select 'OUTCOMES=' || string_agg(outcome || ':' || attempt_number, ',' order by attempt_number,outcome)
 from public.fix_item_results where fix_execution_job_id='${id.job}';
select 'HISTORY=' || (count(*)=4)::text from public.fix_item_results where fix_execution_job_id='${id.job}';
select 'AGGREGATE=' || (job.state='Completed' and plan.state='Completed'
  and job.result_document_version_id='${id.job}' and count(result.id)=4)::text
from public.fix_execution_jobs job join public.fix_plans plan on plan.id=job.fix_plan_id
join public.fix_item_results result on result.fix_execution_job_id=job.id
where job.id='${id.job}' group by job.state,plan.state,job.result_document_version_id;
set local role authenticated;
set local request.jwt.claims='{"sub":"${id.owner}","role":"authenticated"}';
select 'OWNER=' || (count(*)=4)::text from public.fix_item_results where fix_execution_job_id='${id.job}';
set local request.jwt.claims='{"sub":"${id.other}","role":"authenticated"}';
select 'OTHER=' || (count(*)=0)::text from public.fix_item_results where fix_execution_job_id='${id.job}';
reset role;
rollback;
`;

const output = await run("docker", ["exec", await container(), "psql", "-X", "-q", "-A", "-t",
  "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", sql]);
const lines = output.split(/\r?\n/).filter(Boolean);
for (const name of ["OUTCOMES=Failed:1,Failed:1,Applied:2,Skipped:2", "HISTORY=true", "AGGREGATE=true", "OWNER=true", "OTHER=true"])
  console.log(`${name}: ${lines.includes(name) ? "PASS" : "FAIL"}`);
if (!["OUTCOMES=Failed:1,Failed:1,Applied:2,Skipped:2", "HISTORY=true", "AGGREGATE=true", "OWNER=true", "OTHER=true"]
  .every(value => lines.includes(value))) process.exitCode = 1;
