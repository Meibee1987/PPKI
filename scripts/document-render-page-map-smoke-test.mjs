import { randomUUID } from "node:crypto";
import { createServer } from "node:net";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { getSupabaseEnvironment } from "./dev-bootstrap.mjs";
import {
  DOCX_MIME, report, run, databaseContainer, sql, authenticate, startServices,
  stopServices, safeServiceDiagnostics, api, apiBytes, waitAudit, allFindings
} from "./auto-format-providers-smoke-test.mjs";

const TITLE = "S5-T04 canonical document render page map E2E v3";
const FIXTURE = path.join(process.cwd(), "backend", "tests", "fixtures", "docx", "generated", "document-page-map-multipage.docx");
const IMAGE = "gotenberg/gotenberg:8.34.0-libreoffice@sha256:3c23aeb3a027a63d7c71745fc9d83724bd58cf9dfa470396ac82c0896028db2a";
const TERMINAL = new Set(["NoAction", "Completed", "Failed", "Conflict"]);
let rendererId; let temporary;

async function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer(); server.once("error", reject);
    server.listen(0, "127.0.0.1", () => { const address = server.address(); server.close(() => resolve(address.port)); });
  });
}

async function startRenderer() {
  const port = await freePort();
  const name = `ppki-document-render-e2e-${process.pid}-${Date.now()}`;
  rendererId = await run("docker", ["run", "--rm", "-d", "--name", name, "-p", `127.0.0.1:${port}:3000`, IMAGE,
    "gotenberg", "--api-timeout=30s"], { timeoutMs: 60_000 });
  for (let attempt = 0; attempt < 120; attempt += 1) {
    try { if ((await fetch(`http://127.0.0.1:${port}/health`)).ok) return `http://127.0.0.1:${port}`; } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error("pinned renderer did not become healthy");
}

async function waitRender(apiUrl, environment, token, versionId) {
  for (let attempt = 0; attempt < 480; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/document-versions/${versionId}/preview-state`);
    if (result.status !== 200) throw new Error("render state read failed");
    if (["Completed", "Failed"].includes(result.body?.state)) return result.body;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("document render timed out");
}

async function waitAutomatic(apiUrl, environment, token, auditId) {
  for (let attempt = 0; attempt < 360; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/audits/${auditId}`);
    if (result.status !== 200) throw new Error("automatic remediation state read failed");
    if (TERMINAL.has(result.body?.automaticRemediation?.state)) return result.body;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("automatic remediation timed out");
}

async function main() {
  console.log("SUITE document-render-page-map-local-production-e2e");
  try {
    await run("docker", ["info", "--format", "{{.ServerVersion}}"], { timeoutMs: 30_000 });
    const rendererUrl = await startRenderer();
    const environment = await getSupabaseEnvironment(process.cwd());
    const container = await databaseContainer();
    const users = {
      adminA: await authenticate(environment, "document-render-admin-a"),
      adminB: await authenticate(environment, "document-render-admin-b"),
      student: await authenticate(environment, "document-render-student")
    };
    await sql(container, `update public.user_profiles set role=case id when '${users.adminA.id}' then 'PPKIAdmin' when '${users.adminB.id}' then 'PPKIAdmin' when '${users.student.id}' then 'Student' else role end where id in ('${users.adminA.id}','${users.adminB.id}','${users.student.id}');`);
    const apiUrl = await startServices(environment, { DOCUMENT_RENDERER_BASE_URL: rendererUrl });
    report("pinned-renderer-and-production-workers-ready", true);

    const listed = await api(apiUrl, environment, users.adminA.token, "/documents");
    if (listed.status !== 200) throw new Error("document list failed");
    const matching = listed.body.filter(value => value.title === TITLE);
    report("bounded-document-cardinality", matching.length <= 1);
    let documentId = matching[0]?.id;
    if (!documentId) {
      const form = new FormData(); form.set("title", TITLE); form.set("documentTypeCode", "SKRIPSI");
      form.set("file", new Blob([await readFile(FIXTURE)], { type: DOCX_MIME }), "document-page-map-multipage.docx");
      const uploaded = await api(apiUrl, environment, users.adminA.token, "/documents", { method: "POST", form });
      if (uploaded.status !== 201 || !uploaded.body?.id) throw new Error("production DOCX upload failed");
      documentId = uploaded.body.id;
    }

    let detail = await api(apiUrl, environment, users.adminA.token, `/documents/${documentId}`);
    const v1 = detail.body?.versions?.find(value => value.versionNo === 1);
    if (!v1) throw new Error("version one missing");
    const sourceSha = v1.sha256;
    const v1Render = await waitRender(apiUrl, environment, users.adminA.token, v1.id);
    report("version-one-render-completed-with-versioned-contract", v1Render.state === "Completed" && v1Render.previewAvailable
      && v1Render.pageCount >= 7 && v1Render.pageMapVersion === "page-map/1.0"
      && v1Render.rendererVersion === "8.34.0+libreoffice-26.2.4.2");

    temporary = await mkdtemp(path.join(tmpdir(), "ppki-document-render-e2e-"));
    const previewA = await apiBytes(apiUrl, environment, users.adminA.token, `/document-versions/${v1.id}/preview`);
    const previewB = await apiBytes(apiUrl, environment, users.adminB.token, `/document-versions/${v1.id}/preview`);
    const denied = await apiBytes(apiUrl, environment, users.student.token, `/document-versions/${v1.id}/preview`);
    const missing = await apiBytes(apiUrl, environment, users.adminA.token, `/document-versions/${randomUUID()}/preview`);
    const pdfPath = path.join(temporary, "v1.pdf"); await writeFile(pdfPath, previewA.bytes);
    report("secure-preview-is-valid-pdf-with-shared-admin-access", previewA.status === 200 && previewB.status === 200
      && previewA.contentType === "application/pdf" && previewA.bytes.subarray(0, 5).toString("ascii") === "%PDF-"
      && previewA.bytes.equals(previewB.bytes) && denied.status === 403 && missing.status === 404 && v1Render.pageCount >= 7);

    let auditId = v1.audits?.[0]?.id;
    if (!auditId) {
      const queued = await api(apiUrl, environment, users.adminA.token, `/document-versions/${v1.id}/audits`, { method: "POST" });
      if (queued.status !== 202 || !queued.body?.id) throw new Error("initial audit enqueue failed");
      auditId = queued.body.id;
    }
    const audit = await waitAudit(apiUrl, environment, users.adminA.token, auditId);
    report("version-one-audit-completed-independently-of-render", audit.status === "Completed");
    const automatic = await waitAutomatic(apiUrl, environment, users.adminA.token, auditId);
    report("s5-t03-automatic-formatting-remains-completed", automatic.automaticRemediation?.state === "Completed");
    const lineage = (await sql(container, `select concat_ws('|',fix_execution_id,result_document_version_id,reaudit_job_id) from public.automatic_remediation_orchestrations where source_audit_job_id='${auditId}' and orchestration_type='AutoFormat' and policy_version='auto-format/1.0';`)).split("|");
    const [executionId, v2Id, reauditId] = lineage;
    if (lineage.length !== 3 || lineage.some(value => !value)) throw new Error("automatic remediation lineage missing");

    const v2Render = await waitRender(apiUrl, environment, users.adminA.token, v2Id);
    const reaudit = await waitAudit(apiUrl, environment, users.adminB.token, reauditId);
    report("version-two-render-and-reaudit-completed", v2Render.state === "Completed" && v2Render.previewAvailable
      && reaudit.status === "Completed" && reaudit.documentVersionId === v2Id);
    const v1Findings = await allFindings(apiUrl, environment, users.adminA.token, auditId);
    const v2Findings = await allFindings(apiUrl, environment, users.adminB.token, reauditId);
    report("audit-read-model-serves-structural-page-locations", v1Findings.some(value => value.pageLocation?.confidence === "Exact" && value.pageLocation.pageNumber >= 1)
      && v2Findings.some(value => value.pageLocation?.confidence === "Exact" && value.pageLocation.pageNumber >= 1));

    const duplicatePages = await sql(container, `select string_agg(concat(paragraph_index,':',page_number),',' order by paragraph_index) from public.document_page_map_entries entry join public.document_render_artifacts artifact on artifact.id=entry.render_artifact_id where artifact.document_version_id='${v1.id}' and entry.run_index is null and entry.paragraph_index in (3,14) and entry.confidence='Exact';`);
    const duplicateValues = duplicatePages.split(",").map(value => value.split(":").map(Number));
    const runBoundary = await sql(container, `select concat(confidence,'|',page_number) from public.document_page_map_entries entry join public.document_render_artifacts artifact on artifact.id=entry.render_artifact_id where artifact.document_version_id='${v1.id}' and entry.paragraph_index=12 and entry.run_index=1;`);
    report("duplicate-text-and-run-boundary-use-exact-structural-anchors", duplicateValues.length === 2
      && duplicateValues[0][0] === 3 && duplicateValues[1][0] === 14 && duplicateValues[0][1] !== duplicateValues[1][1]
      && duplicateValues.every(value => value[1] >= 1) && runBoundary.startsWith("Exact|"));

    const isolation = await sql(container, `select concat_ws('|',
      count(distinct artifact.document_version_id),
      count(distinct artifact.id),
      count(distinct job.render_identity),
      count(*) filter (where artifact.document_version_id='${v1.id}'),
      count(*) filter (where artifact.document_version_id='${v2Id}'))
      from public.document_render_artifacts artifact join public.document_render_jobs job on job.id=artifact.render_job_id
      where artifact.document_version_id in ('${v1.id}','${v2Id}');`);
    report("v1-v2-render-identities-and-artifacts-are-isolated", isolation === "2|2|2|1|1");

    await api(apiUrl, environment, users.adminA.token, `/document-versions/${v1.id}/preview-state`);
    await api(apiUrl, environment, users.adminB.token, `/document-versions/${v2Id}/preview-state`);
    detail = await api(apiUrl, environment, users.adminA.token, `/documents/${documentId}`);
    const cardinality = await sql(container, `select concat_ws('|',
      (select count(*) from public.documents where title='${TITLE}'),
      (select count(*) from public.document_versions where document_id='${documentId}'),
      (select count(*) from public.document_render_jobs job join public.document_versions version on version.id=job.document_version_id where version.document_id='${documentId}'),
      (select count(*) from public.document_render_artifacts artifact join public.document_versions version on version.id=artifact.document_version_id where version.document_id='${documentId}'),
      (select count(*) from public.automatic_remediation_orchestrations where source_audit_job_id='${reauditId}'),
      (select case when sha256='${sourceSha}' then 1 else 0 end from public.document_versions where id='${v1.id}'))`);
    report("replay-is-idempotent-source-is-immutable-and-no-second-auto-pass", cardinality === "1|2|2|2|0|1"
      && detail.body?.versions?.length === 2);
    console.log("cardinality documents=1 version1=1 version2=1 renderArtifactV1=1 renderArtifactV2=1 renderJobs=2");
    console.log(`lineage sourceAudit=${auditId.slice(0,8)} execution=${executionId.slice(0,8)} reaudit=${reauditId.slice(0,8)}`);
    console.log("document-render-page-map-production-e2e-completed: PASS");
  } catch (error) {
    console.log(`BLOCKER: ${error instanceof Error ? error.message : "local runtime unavailable"}`);
    const diagnostic = safeServiceDiagnostics(); if (diagnostic) console.log(`SAFE-DIAGNOSTIC: ${diagnostic}`);
    console.log("document-render-page-map-production-e2e-completed: FAIL");
    process.exitCode = 1;
  } finally {
    await stopServices();
    if (rendererId) { try { await run("docker", ["stop", rendererId], { timeoutMs: 30_000 }); } catch {} }
    if (temporary && path.resolve(temporary).startsWith(path.resolve(tmpdir()))) await rm(temporary, { recursive: true, force: true });
  }
}

main();
