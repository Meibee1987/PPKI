import { randomUUID } from "node:crypto";
import { createServer } from "node:net";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { getSupabaseEnvironment } from "./dev-bootstrap.mjs";
import {
  DOCX_MIME, report, run, databaseContainer, sql, authenticate, startServices,
  stopServices, safeServiceDiagnostics, api, waitAudit, download
} from "./auto-format-providers-smoke-test.mjs";

const TITLE = "S5-T07 text correction batch E2E v5";
const RENDERER_IMAGE = "gotenberg/gotenberg:8.34.0-libreoffice@sha256:3c23aeb3a027a63d7c71745fc9d83724bd58cf9dfa470396ac82c0896028db2a";
const FIXTURE = path.join(process.cwd(), "backend", "tests", "fixtures", "docx", "generated", "text-correction-batch.docx");
const DECISION_KEYS = [
  "57070000-0000-0000-0000-000000000001",
  "57070000-0000-0000-0000-000000000002",
  "57070000-0000-0000-0000-000000000003"
];
const BATCH_KEY = "57070000-0000-0000-0000-000000000010";
let temporary;
let rendererId;

async function freePort() {
  return new Promise((resolve, reject) => {
    const server = createServer(); server.once("error", reject);
    server.listen(0, "127.0.0.1", () => { const address = server.address(); server.close(() => resolve(address.port)); });
  });
}

async function startRenderer() {
  const port = await freePort();
  const name = `ppki-text-correction-render-${process.pid}-${Date.now()}`;
  rendererId = await run("docker", ["run", "--rm", "-d", "--name", name,
    "-p", `127.0.0.1:${port}:3000`, RENDERER_IMAGE, "gotenberg", "--api-timeout=30s"], { timeoutMs: 60_000 });
  for (let attempt = 0; attempt < 120; attempt += 1) {
    try { if ((await fetch(`http://127.0.0.1:${port}/health`)).ok) return `http://127.0.0.1:${port}`; } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error("pinned renderer did not become healthy");
}

async function waitAutomatic(apiUrl, environment, token, auditId) {
  for (let attempt = 0; attempt < 80; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/audits/${auditId}`);
    if (result.status !== 200) throw new Error("automatic remediation status unavailable");
    if (["Completed", "NoAction", "Failed", "Conflict"].includes(result.body?.automaticRemediation?.state)) return result.body;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("automatic remediation timed out");
}

async function waitProposals(apiUrl, environment, token, auditId) {
  for (let attempt = 0; attempt < 80; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/audits/${auditId}/text-corrections?page=1&pageSize=100`);
    if (result.status === 200 && result.body?.totalCount === 4) return result.body;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("text correction analysis timed out");
}

async function waitRender(apiUrl, environment, token, versionId) {
  for (let attempt = 0; attempt < 300; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/document-versions/${versionId}/preview-state`);
    if (result.status === 200 && result.body?.state === "Completed") return result.body;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("document render timed out");
}

async function waitBatch(apiUrl, environment, token, batchId) {
  for (let attempt = 0; attempt < 360; attempt += 1) {
    const result = await api(apiUrl, environment, token, `/text-correction-batches/${batchId}`);
    if (result.status !== 200) throw new Error("correction batch status unavailable");
    if (["Completed", "Failed", "Conflict"].includes(result.body?.state)) return result.body;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("correction batch timed out");
}

async function inspectCorrection(file) {
  return JSON.parse(await run("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
    "scripts/inspect-text-correction-docx.ps1", "-Path", file], { timeoutMs: 30_000 }));
}

async function main() {
  console.log("SUITE text-correction-batch-local-production-e2e");
  try {
    await run("docker", ["info", "--format", "{{.ServerVersion}}"], { timeoutMs: 30_000 });
    const rendererUrl = await startRenderer();
    const environment = await getSupabaseEnvironment(process.cwd());
    const container = await databaseContainer();
    const users = {
      adminA: await authenticate(environment, "text-correction-admin-a"),
      adminB: await authenticate(environment, "text-correction-admin-b"),
      student: await authenticate(environment, "text-correction-student")
    };
    await sql(container, `update public.user_profiles set role=case id when '${users.adminA.id}' then 'PPKIAdmin' when '${users.adminB.id}' then 'PPKIAdmin' when '${users.student.id}' then 'Student' else role end where id in ('${users.adminA.id}','${users.adminB.id}','${users.student.id}');`);
    const apiUrl = await startServices(environment, { DOCUMENT_RENDERER_BASE_URL: rendererUrl });

    const listed = await api(apiUrl, environment, users.adminA.token, "/documents");
    const matching = listed.body?.filter(value => value.title === TITLE) ?? [];
    report("bounded-document-cardinality", matching.length <= 1);
    let documentId = matching[0]?.id;
    if (!documentId) {
      const form = new FormData(); form.set("title", TITLE); form.set("documentTypeCode", "SKRIPSI");
      form.set("file", new Blob([await readFile(FIXTURE)], { type: DOCX_MIME }), "text-correction-batch.docx");
      const uploaded = await api(apiUrl, environment, users.adminA.token, "/documents", { method: "POST", form });
      if (uploaded.status !== 201 || !uploaded.body?.id) throw new Error("production correction fixture upload failed");
      documentId = uploaded.body.id;
    }
    let detail = await api(apiUrl, environment, users.adminA.token, `/documents/${documentId}`);
    const v1 = detail.body?.versions?.find(value => value.versionNo === 1);
    if (!v1) throw new Error("v1 source missing");
    let initialAuditId = v1.audits?.find(value => !value.sourceFixExecutionId)?.id ?? v1.audits?.[0]?.id;
    if (!initialAuditId) {
      const queued = await api(apiUrl, environment, users.adminA.token, `/document-versions/${v1.id}/audits`, { method: "POST" });
      if (queued.status !== 202) throw new Error("initial audit enqueue failed");
      initialAuditId = queued.body.id;
    }
    const initial = await waitAudit(apiUrl, environment, users.adminA.token, initialAuditId);
    report("initial-audit-completed", initial.status === "Completed");
    const automatic = await waitAutomatic(apiUrl, environment, users.adminA.token, initialAuditId);
    report("formatting-pass-precedes-corrections", automatic.automaticRemediation?.state === "Completed"
      && automatic.automaticRemediation?.operationCount > 0);
    const lineage = (await sql(container, `select concat_ws('|',result_document_version_id,reaudit_job_id) from public.automatic_remediation_orchestrations where source_audit_job_id='${initialAuditId}';`)).split("|");
    const [v2Id, v2AuditId] = lineage;
    if (!v2Id || !v2AuditId) throw new Error("automatic v2 lineage missing");
    const v2Audit = await waitAudit(apiUrl, environment, users.adminA.token, v2AuditId);
    report("v2-canonical-reaudit-completed", v2Audit.status === "Completed" && v2Audit.documentVersionId === v2Id);
    const renderV2 = await waitRender(apiUrl, environment, users.adminA.token, v2Id);
    report("v2-page-map-ready-before-context", renderV2.pageCount >= 3);

    const page = await waitProposals(apiUrl, environment, users.adminA.token, v2AuditId);
    report("detector-produced-four-bounded-purpose-specific-proposals", page.totalCount === 4 && page.items.length === 4
      && page.items.every(value => value.detectorRule === "lex.di-analisa" && value.anchorStatus === "Exact"));
    const contexts = [];
    for (const proposal of page.items) {
      const result = await api(apiUrl, environment, users.adminA.token, `/text-corrections/${proposal.id}/context`);
      if (result.status !== 200 || result.body?.anchorStatus !== "Exact") throw new Error("transient context failed");
      contexts.push({ proposal, context: result.body });
    }
    const duplicateContexts = contexts.filter(value => value.context.context?.includes("Kandidat pertama"))
      .sort((left, right) => left.context.targetOffsetInContext - right.context.targetOffsetInContext);
    const suggestion = duplicateContexts[0];
    const manual = contexts.find(value => value.context.context?.includes("split"));
    const ignored = contexts.find(value => value.context.context?.includes("tautan"));
    const undecided = duplicateContexts[1];
    report("transient-context-distinguishes-exact-occurrences", [suggestion, manual, ignored, undecided].every(Boolean)
      && suggestion.context.targetOffsetInContext < undecided.context.targetOffsetInContext
      && contexts.every(value => value.context.targetText === "di analisa"));

    const decisionRequests = [
      [suggestion, { action: "UseSuggestion" }],
      [manual, { action: "EditManual", manualReplacement: "dianalisis secara manual" }],
      [ignored, { action: "Ignore" }]
    ];
    const decisions = [];
    for (let index = 0; index < decisionRequests.length; index += 1) {
      const [target, request] = decisionRequests[index];
      const result = await api(apiUrl, environment, users.adminA.token,
        `/text-corrections/${target.proposal.id}/decisions`,
        { method: "POST", json: request, idempotencyKey: DECISION_KEYS[index] });
      if (![200, 201].includes(result.status)) throw new Error("correction decision failed");
      decisions.push(result.body);
    }
    report("use-suggestion-edit-manual-ignore-are-append-only", decisions.map(value => value.action).join("|") === "UseSuggestion|EditManual|Ignore");
    const selectedDecisionIds = decisions.slice(0, 2).map(value => value.id);
    const accepted = await api(apiUrl, environment, users.adminA.token,
      `/audits/${v2AuditId}/text-correction-batches`,
      { method: "POST", json: { decisionIds: selectedDecisionIds }, idempotencyKey: BATCH_KEY });
    if (![200, 202].includes(accepted.status) || !accepted.body?.id) throw new Error("correction batch create failed");
    const batch = await waitBatch(apiUrl, environment, users.adminA.token, accepted.body.id);
    report("one-batch-completed-after-reaudit-and-verification", batch.state === "Completed"
      && batch.decisionCount === 2 && batch.verificationCounts?.VerifiedResolved === 2
      && Boolean(batch.fixExecutionId) && Boolean(batch.resultDocumentVersionId) && Boolean(batch.reauditId));
    const renderV3 = await waitRender(apiUrl, environment, users.adminA.token, batch.resultDocumentVersionId);
    report("v3-render-and-page-map-completed", renderV3.pageCount >= 3);
    const renderV1 = await waitRender(apiUrl, environment, users.adminA.token, v1.id);
    report("v1-independent-render-and-page-map-completed", renderV1.pageCount >= 3);

    detail = await api(apiUrl, environment, users.adminA.token, `/documents/${documentId}`);
    const v2 = detail.body.versions.find(value => value.id === v2Id);
    const v3 = detail.body.versions.find(value => value.id === batch.resultDocumentVersionId);
    report("exactly-one-correction-result-version", detail.body.currentVersionNo === 3
      && detail.body.versions.length === 3 && v3?.versionNo === 3 && v3?.parentVersionId === v2Id);
    temporary = await mkdtemp(path.join(tmpdir(), "ppki-text-correction-e2e-"));
    const v2Download = await download(apiUrl, environment, users.adminA.token, v2Id, path.join(temporary, "v2.docx"));
    const v3Download = await download(apiUrl, environment, users.adminB.token, v3.id, path.join(temporary, "v3.docx"));
    const before = await inspectCorrection(path.join(temporary, "v2.docx"));
    const after = await inspectCorrection(path.join(temporary, "v3.docx"));
    report("selected-only-text-changed-and-ignore-undecided-remain", before.remainingSourceCount === 4
      && after.remainingSourceCount === 2 && after.suggestionCount === 2 && after.manualCount === 1
      && after.hyperlinkSourceCount === 1);
    report("package-relationships-and-structure-preserved", v2Download.inspection.packageValid && v3Download.inspection.packageValid
      && v2Download.inspection.relationshipsHash === v3Download.inspection.relationshipsHash
      && before.paragraphCount === after.paragraphCount && before.runCount === after.runCount);

    const adminB = await api(apiUrl, environment, users.adminB.token, `/text-correction-batches/${batch.id}`);
    const student = await api(apiUrl, environment, users.student.token, `/text-correction-batches/${batch.id}`);
    report("shared-admin-visible-and-non-admin-denied", adminB.status === 200 && student.status === 403);
    const replayDecision = await api(apiUrl, environment, users.adminA.token,
      `/text-corrections/${suggestion.proposal.id}/decisions`,
      { method: "POST", json: { action: "UseSuggestion" }, idempotencyKey: DECISION_KEYS[0] });
    const replayBatch = await api(apiUrl, environment, users.adminA.token,
      `/audits/${v2AuditId}/text-correction-batches`,
      { method: "POST", json: { decisionIds: selectedDecisionIds }, idempotencyKey: BATCH_KEY });
    report("lost-response-replays-are-canonical", replayDecision.status === 200 && replayDecision.body?.replayed === true
      && replayBatch.status === 200 && replayBatch.body?.replayed === true && replayBatch.body?.id === batch.id);

    const persisted = await sql(container, `select concat_ws('|',
      (select count(*) from public.documents where title='${TITLE}'),
      (select count(*) from public.document_versions where document_id='${documentId}'),
      (select count(*) from public.text_correction_analyses where audit_job_id in ('${v2AuditId}','${batch.reauditId}')),
      (select count(*) from public.text_correction_proposals where audit_job_id='${v2AuditId}'),
      (select count(*) from public.text_correction_decision_events where proposal_id in (select id from public.text_correction_proposals where audit_job_id='${v2AuditId}')),
      (select count(*) from public.text_correction_batches where source_audit_job_id='${v2AuditId}'),
      (select count(*) from public.fix_execution_jobs where id='${batch.fixExecutionId}'),
      (select count(*) from public.audit_jobs where source_fix_execution_id='${batch.fixExecutionId}'),
      (select count(*) from public.document_render_artifacts where document_version_id in ('${v2Id}','${batch.resultDocumentVersionId}')),
      (select case when approved_plan_snapshot::text not like '%dianalisis%' then 1 else 0 end from public.fix_execution_jobs where id='${batch.fixExecutionId}'),
      (select case when sha256='${v2.sha256}' then 1 else 0 end from public.document_versions where id='${v2Id}'))`);
    report("bounded-cardinality-reference-only-plan-and-source-immutability", persisted === "1|3|2|4|3|1|1|1|2|1|1");
    const canonicalRenderLineage = await sql(container, `select concat_ws('|',
      (select count(*) from public.document_versions where document_id='${documentId}'),
      (select count(*) from public.document_render_jobs job join public.document_versions version on version.id=job.document_version_id where version.document_id='${documentId}'),
      (select count(*) from public.document_render_jobs job join public.document_versions version on version.id=job.document_version_id where version.document_id='${documentId}' and job.state='Completed'),
      (select count(*) from public.document_render_artifacts artifact join public.document_versions version on version.id=artifact.document_version_id where version.document_id='${documentId}'),
      (select count(distinct job.render_identity) from public.document_render_jobs job join public.document_versions version on version.id=job.document_version_id where version.document_id='${documentId}'),
      (select count(*) from public.document_render_artifacts artifact join public.document_render_jobs job on job.id=artifact.render_job_id where artifact.document_version_id<>job.document_version_id and artifact.document_version_id in (select id from public.document_versions where document_id='${documentId}')))`);
    report("all-three-versions-have-independent-canonical-render-lineage", canonicalRenderLineage === "3|3|3|3|3|0");
    const forbiddenColumns = await sql(container, `select count(*) from information_schema.columns where table_schema='public' and table_name like 'text_correction_%' and column_name in ('source_text','source_sentence','source_paragraph','source_excerpt','context');`);
    report("database-has-no-source-context-column", forbiddenColumns === "0");
    console.log("cardinality documents=1 versions=3 analyses=2 sourceProposals=4 decisions=3 batches=1 correctionExecutions=1 correctionReaudits=1 boundedRenderArtifactsV2V3=2 canonicalRenderArtifactsV1V2V3=3");
    console.log("text-correction-batch-production-e2e-completed: PASS");
  } catch (error) {
    console.log(`BLOCKER: ${error instanceof Error ? error.message : "local runtime unavailable"}`);
    const diagnostics = safeServiceDiagnostics(); if (diagnostics) console.log(`SAFE-DIAGNOSTIC: ${diagnostics}`);
    console.log("text-correction-batch-production-e2e-completed: FAIL");
    process.exitCode = 1;
  } finally {
    await stopServices();
    if (rendererId) {
      try { await run("docker", ["rm", "-f", rendererId], { timeoutMs: 30_000 }); } catch {}
    }
    if (temporary && path.resolve(temporary).startsWith(path.resolve(tmpdir())))
      await rm(temporary, { recursive: true, force: true });
  }
}

main();
