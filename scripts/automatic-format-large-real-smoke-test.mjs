import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { getSupabaseEnvironment } from "./dev-bootstrap.mjs";
import {
  DOCX_MIME, report, databaseContainer, sql, authenticate, startServices,
  stopServices, safeServiceDiagnostics, api, waitAudit, download
} from "./auto-format-providers-smoke-test.mjs";

const titleArgument = process.argv.indexOf("--title");
const TITLE = titleArgument >= 0 ? process.argv[titleArgument + 1] : "S5 hotfix large automatic real-document proof";
const sourceArgument = process.argv.indexOf("--source-version");
const sourceVersionId = sourceArgument >= 0 ? process.argv[sourceArgument + 1] : undefined;
let temporary;

async function waitAutomatic(apiUrl, environment, token, auditId) {
  for (let attempt = 0; attempt < 1_200; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/audits/${auditId}`);
    if (result.status !== 200) throw new Error("automatic remediation status read failed");
    if (["NoAction", "Completed", "Failed", "Conflict"].includes(result.body?.automaticRemediation?.state)) return result.body;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("large automatic remediation timed out");
}

async function waitRenders(container, documentId) {
  for (let attempt = 0; attempt < 600; attempt += 1) {
    const value = await sql(container, `select concat_ws('|',count(*),count(*) filter(where state='Completed'),
      count(*) filter(where attempt_count>max_attempts),count(*) filter(where state='Failed'))
      from public.document_render_jobs where document_version_id in
      (select id from public.document_versions where document_id='${documentId}');`);
    const [jobs, completed, overMax, failed] = value.split("|").map(Number);
    if (jobs === 2 && completed === 2) return { jobs, completed, overMax, failed };
    if (failed > 0 || overMax > 0) return { jobs, completed, overMax, failed };
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("large document renders timed out");
}

async function main() {
  console.log("SUITE automatic-format-large-real-local-production-e2e");
  try {
    if (!/^[0-9a-f-]{36}$/iu.test(sourceVersionId ?? "")) throw new Error("--source-version UUID is required");
    if (!TITLE || TITLE.length > 200 || /['\r\n]/u.test(TITLE)) throw new Error("--title is invalid");
    const environment = await getSupabaseEnvironment(process.cwd());
    const container = await databaseContainer();
    const metadata = (await sql(container, `select concat_ws('|',v.storage_bucket,v.storage_key,t.code)
      from public.document_versions v join public.documents d on d.id=v.document_id
      join public.document_types t on t.id=d.document_type_id where v.id='${sourceVersionId}';`)).split("|");
    if (metadata.length !== 3 || metadata.some(value => !value)) throw new Error("source version metadata unavailable");
    const storagePath = metadata[1].split("/").map(encodeURIComponent).join("/");
    const sourceResponse = await fetch(`${environment.API_URL}/storage/v1/object/authenticated/${metadata[0]}/${storagePath}`, {
      headers: { apikey: environment.SERVICE_ROLE_KEY, authorization: `Bearer ${environment.SERVICE_ROLE_KEY}` }
    });
    if (!sourceResponse.ok) throw new Error("local source object download failed");
    const sourceBytes = Buffer.from(await sourceResponse.arrayBuffer());

    const admin = await authenticate(environment, "large-real-hotfix-admin");
    await sql(container, `update public.user_profiles set role='PPKIAdmin' where id='${admin.id}';`);
    const apiUrl = await startServices(environment);
    const listed = await api(apiUrl, environment, admin.token, "/documents");
    let documentId = listed.body?.find(value => value.title === TITLE)?.id;
    if (!documentId) {
      const form = new FormData();
      form.set("title", TITLE); form.set("documentTypeCode", metadata[2]);
      form.set("file", new Blob([sourceBytes], { type: DOCX_MIME }), "large-real-proof.docx");
      const uploaded = await api(apiUrl, environment, admin.token, "/documents", { method: "POST", form });
      if (uploaded.status !== 201 || !uploaded.body?.id) throw new Error("new real document upload failed");
      documentId = uploaded.body.id;
    }
    let detail = await api(apiUrl, environment, admin.token, `/documents/${documentId}`);
    const v1 = detail.body?.versions?.find(value => value.versionNo === 1);
    if (!v1) throw new Error("new source version missing");
    let auditId = v1.audits?.[0]?.id;
    if (!auditId) {
      const queued = await api(apiUrl, environment, admin.token, `/document-versions/${v1.id}/audits`, { method: "POST" });
      if (queued.status !== 202 || !queued.body?.id) throw new Error("new real audit enqueue failed");
      auditId = queued.body.id;
    }
    const audit = await waitAudit(apiUrl, environment, admin.token, auditId);
    report("new-real-document-audit-completed", audit.status === "Completed");
    const automatic = await waitAutomatic(apiUrl, environment, admin.token, auditId);
    report("large-automatic-selection-completed", automatic.automaticRemediation?.state === "Completed"
      && automatic.automaticRemediation.eligibleFindingCount > 2_000
      && automatic.automaticRemediation.operationCount > 2_000);

    const lineage = (await sql(container, `select concat_ws('|',o.fix_execution_id,o.result_document_version_id,o.reaudit_job_id,
      e.state,e.planned_operation_count,e.completed_operation_count,coalesce(e.safe_failure_code,''),
      coalesce(e.approved_plan_snapshot::jsonb #>> '{selectionScope}',''))
      from public.automatic_remediation_orchestrations o join public.fix_execution_jobs e on e.id=o.fix_execution_id
      where o.source_audit_job_id='${auditId}';`)).split("|");
    const [executionId, resultVersionId, reauditId, executionState, planned, completed, failureCode, scope] = lineage;
    report("one-large-fix-execution-completed", executionState === "Completed" && Number(planned) > 2_000
      && planned === completed && !failureCode && scope === "Automatic");
    const reaudit = await waitAudit(apiUrl, environment, admin.token, reauditId);
    report("large-automatic-reaudit-completed", reaudit.status === "Completed" && reaudit.documentVersionId === resultVersionId);

    detail = await api(apiUrl, environment, admin.token, `/documents/${documentId}`);
    const v2 = detail.body?.versions?.find(value => value.id === resultVersionId);
    const childCount = await sql(container, `select count(*) from public.document_versions
      where document_id='${documentId}' and parent_version_id='${v1.id}';`);
    report("exactly-one-child-version", detail.body?.versions?.length === 2 && detail.body?.currentVersionNo === 2
      && Boolean(v2) && childCount === "1");
    const renders = await waitRenders(container, documentId);
    report("v1-v2-independent-renders-completed", renders.jobs === 2 && renders.completed === 2
      && renders.failed === 0 && renders.overMax === 0);

    temporary = await mkdtemp(path.join(tmpdir(), "ppki-large-real-hotfix-"));
    const before = await download(apiUrl, environment, admin.token, v1.id, path.join(temporary, "v1.docx"));
    const after = await download(apiUrl, environment, admin.token, v2.id, path.join(temporary, "v2.docx"));
    report("text-unchanged-and-source-immutable", before.inspection?.textFingerprint === after.inspection?.textFingerprint
      && v1.sha256 === await sql(container, `select sha256 from public.document_versions where id='${v1.id}';`));
    const cardinality = await sql(container, `select concat_ws('|',
      (select count(*) from public.document_versions where document_id='${documentId}'),
      (select count(*) from public.fix_execution_jobs where audit_job_id='${auditId}'),
      (select count(*) from public.audit_jobs where source_fix_execution_id='${executionId}'),
      (select count(*) from public.document_render_artifacts where document_version_id in
        (select id from public.document_versions where document_id='${documentId}')),
      (select count(*) from public.document_render_jobs where attempt_count>max_attempts));`);
    report("large-real-cardinality-and-attempt-bound", cardinality === "2|1|1|2|0");
    console.log(`cardinality versions=2 fixExecutions=1 reaudits=1 renderArtifacts=2 operations=${planned}`);
    console.log("automatic-format-large-real-production-e2e-completed: PASS");
  } catch (error) {
    console.log(`BLOCKER: ${error instanceof Error ? error.message : "large real runtime unavailable"}`);
    const diagnostic = safeServiceDiagnostics(); if (diagnostic) console.log(`SAFE-DIAGNOSTIC: ${diagnostic}`);
    console.log("automatic-format-large-real-production-e2e-completed: FAIL");
    process.exitCode = 1;
  } finally {
    await stopServices();
    if (temporary?.startsWith(tmpdir())) await rm(temporary, { recursive: true, force: true });
  }
}

main();
