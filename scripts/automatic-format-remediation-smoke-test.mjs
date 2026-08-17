import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { getSupabaseEnvironment } from "./dev-bootstrap.mjs";
import {
  FIXTURE, DOCX_MIME, report, run, databaseContainer, sql, authenticate,
  startServices, stopServices, safeServiceDiagnostics, api, waitAudit, allFindings, download
} from "./auto-format-providers-smoke-test.mjs";

const TITLE = "S5-T03 automatic format remediation E2E v5";
const TERMINAL = new Set(["NoAction", "Completed", "Failed", "Conflict"]);
let temporary;

async function waitAutomatic(apiUrl, environment, token, auditId) {
  for (let attempt = 0; attempt < 240; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/audits/${auditId}`);
    if (result.status !== 200) throw new Error("automatic remediation status read failed");
    const state = result.body?.automaticRemediation?.state;
    if (TERMINAL.has(state)) return result.body;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("automatic remediation timed out");
}

async function main() {
  console.log("SUITE automatic-format-remediation-local-production-e2e");
  try {
    await run("docker", ["info", "--format", "{{.ServerVersion}}"], { timeoutMs: 30_000 });
    const environment = await getSupabaseEnvironment(process.cwd());
    const container = await databaseContainer();
    const users = {
      adminA: await authenticate(environment, "automatic-admin-a"),
      adminB: await authenticate(environment, "automatic-admin-b"),
      student: await authenticate(environment, "automatic-student")
    };
    await sql(container, `update public.user_profiles set role=case id when '${users.adminA.id}' then 'PPKIAdmin' when '${users.adminB.id}' then 'PPKIAdmin' when '${users.student.id}' then 'Student' else role end where id in ('${users.adminA.id}','${users.adminB.id}','${users.student.id}');`);
    const apiUrl = await startServices(environment);
    report("production-api-audit-fix-and-automatic-workers-ready", true);

    const listed = await api(apiUrl, environment, users.adminA.token, "/documents");
    if (listed.status !== 200) throw new Error("document list failed");
    const matching = listed.body.filter(value => value.title === TITLE);
    report("bounded-document-fixture-cardinality", matching.length <= 1);
    let documentId = matching[0]?.id;
    if (!documentId) {
      const form = new FormData(); form.set("title", TITLE); form.set("documentTypeCode", "SKRIPSI");
      form.set("file", new Blob([await readFile(FIXTURE)], { type: DOCX_MIME }), "auto-format-provider-mixed.docx");
      const uploaded = await api(apiUrl, environment, users.adminA.token, "/documents", { method: "POST", form });
      if (uploaded.status !== 201 || !uploaded.body?.id) throw new Error("production DOCX upload failed");
      documentId = uploaded.body.id;
    }

    let detail = await api(apiUrl, environment, users.adminA.token, `/documents/${documentId}`);
    const sourceVersion = detail.body?.versions?.find(value => value.versionNo === 1);
    if (!sourceVersion) throw new Error("source version missing");
    const sourceSha = sourceVersion.sha256;
    let sourceAuditId = sourceVersion.audits?.[0]?.id;
    if (!sourceAuditId) {
      const queued = await api(apiUrl, environment, users.adminA.token, `/document-versions/${sourceVersion.id}/audits`, { method: "POST" });
      if (queued.status !== 202 || !queued.body?.id) throw new Error(`initial audit enqueue failed (${queued.status}/${queued.body?.code ?? "no-code"})`);
      sourceAuditId = queued.body.id;
    }
    const initial = await waitAudit(apiUrl, environment, users.adminA.token, sourceAuditId);
    report("initial-audit-completed-with-durable-findings", initial.status === "Completed" && initial.persistedFindingCount > 0);

    const automatic = await waitAutomatic(apiUrl, environment, users.adminA.token, sourceAuditId);
    report("automatic-flow-completed-without-client-remediation-command", automatic.automaticRemediation?.state === "Completed"
      && automatic.automaticRemediation.eligibleFindingCount >= 8 && automatic.automaticRemediation.operationCount >= 8);
    const identity = (await sql(container, `select concat_ws('|',id,fix_execution_id,result_document_version_id,reaudit_job_id) from public.automatic_remediation_orchestrations where source_audit_job_id='${sourceAuditId}' and orchestration_type='AutoFormat' and policy_version='auto-format/1.0';`)).split("|");
    const [orchestrationId, executionId, resultVersionId, reauditId] = identity;
    report("canonical-orchestration-lineage-is-complete", identity.length === 4 && identity.every(Boolean));

    const execution = await api(apiUrl, environment, users.adminB.token, `/audits/${sourceAuditId}/fix-executions/${executionId}`);
    report("canonical-fix-execution-completed-all-operations", execution.status === 200 && execution.body?.state === "Completed"
      && execution.body?.plannedOperationCount === automatic.automaticRemediation.operationCount
      && execution.body?.completedOperationCount === automatic.automaticRemediation.operationCount
      && execution.body?.resultDocumentVersionId === resultVersionId);
    const reaudit = await waitAudit(apiUrl, environment, users.adminB.token, reauditId);
    report("automatic-canonical-reaudit-completed", reaudit.status === "Completed" && reaudit.documentVersionId === resultVersionId);
    const comparison = await api(apiUrl, environment, users.adminB.token, `/fix-executions/${executionId}/comparison`);
    report("comparison-ready-after-automatic-reconciliation", comparison.status === 200 && comparison.body?.comparisonState === "Ready");

    detail = await api(apiUrl, environment, users.adminA.token, `/documents/${documentId}`);
    const resultVersion = detail.body?.versions?.find(value => value.id === resultVersionId);
    report("exactly-one-result-version-with-correct-lineage", detail.body?.currentVersionNo === 2 && detail.body?.versions?.length === 2 && resultVersion?.versionNo === 2);
    temporary = await mkdtemp(path.join(tmpdir(), "ppki-automatic-format-e2e-"));
    const sourceDownload = await download(apiUrl, environment, users.adminA.token, sourceVersion.id, path.join(temporary, "source.docx"));
    const resultDownload = await download(apiUrl, environment, users.adminB.token, resultVersionId, path.join(temporary, "result.docx"));
    const before = sourceDownload.inspection; const after = resultDownload.inspection;
    report("source-and-result-docx-are-parseable-and-content-identical", before.packageValid && after.packageValid
      && before.textFingerprint === after.textFingerprint && before.paragraphCount === after.paragraphCount);
    report("automatic-result-formatting-is-exact", after.firstParagraph.runs[1].fontAscii === "Times New Roman"
      && after.firstParagraph.runs[1].size === "24" && after.firstParagraph.line === "240"
      && after.firstParagraph.firstLine === "567" && after.firstParagraph.hanging === null
      && after.firstParagraph.alignment === "both" && after.abstractParagraph.before === "0"
      && after.abstractParagraph.after === "0" && after.chapterHeading.alignment === "center");

    const adminB = await api(apiUrl, environment, users.adminB.token, `/audits/${sourceAuditId}`);
    const student = await api(apiUrl, environment, users.student.token, `/audits/${sourceAuditId}`);
    report("shared-admin-sees-canonical-state-and-non-admin-is-denied", adminB.status === 200
      && adminB.body?.automaticRemediation?.state === "Completed" && student.status === 403);
    const persisted = await sql(container, `select concat_ws('|',
      (select count(*) from public.documents where title='${TITLE}'),
      (select count(*) from public.audit_jobs where document_version_id='${sourceVersion.id}' and source_fix_execution_id is null),
      (select count(*) from public.automatic_remediation_orchestrations where source_audit_job_id='${sourceAuditId}'),
      (select count(*) from public.fix_execution_jobs where audit_job_id='${sourceAuditId}'),
      (select count(*) from public.document_versions where document_id='${documentId}'),
      (select count(*) from public.audit_jobs where source_fix_execution_id='${executionId}'),
      (select count(*) from public.automatic_remediation_orchestrations where source_audit_job_id='${reauditId}'),
      (select count(*) from public.finding_resolution_cases as c join public.finding_resolution_events as e on e.resolution_case_id=c.id where e.source_fix_execution_id='${executionId}' and e.event_type in ('VerificationResolvedObserved','VerificationStillDetectedObserved')),
      (select case when sha256='${sourceSha}' then 1 else 0 end from public.document_versions where id='${sourceVersion.id}'))`);
    const expectedPersistence = `1|1|1|1|2|1|0|${automatic.automaticRemediation.eligibleFindingCount}|1`;
    report("bounded-cardinality-and-persisted-loop-prevention", persisted === expectedPersistence);
    console.log(`cardinality documents=1 sourceAudits=1 autoOrchestrations=1 fixExecutions=1 resultVersions=1 reaudits=1 reAuditAutoPasses=0 verified=${automatic.automaticRemediation.eligibleFindingCount} orchestration=${orchestrationId.slice(0, 8)}`);
    console.log("automatic-format-remediation-production-e2e-completed: PASS");
  } catch (error) {
    console.log(`BLOCKER: ${error instanceof Error ? error.message : "local runtime unavailable"}`);
    const diagnostic = safeServiceDiagnostics(); if (diagnostic) console.log(`SAFE-DIAGNOSTIC: ${diagnostic}`);
    console.log("automatic-format-remediation-production-e2e-completed: FAIL");
    process.exitCode = 1;
  } finally {
    await stopServices();
    if (temporary && path.resolve(temporary).startsWith(path.resolve(tmpdir()))) await rm(temporary, { recursive: true, force: true });
  }
}

main();
