import { getSupabaseEnvironment } from "./dev-bootstrap.mjs";
import {
  api, authenticate, databaseContainer, report, safeServiceDiagnostics, sql, startServices, stopServices
} from "./auto-format-providers-smoke-test.mjs";

function argument(name) {
  const index = process.argv.indexOf(name);
  const value = index >= 0 ? process.argv[index + 1] : undefined;
  if (!/^[0-9a-f-]{36}$/iu.test(value ?? "")) throw new Error(`${name} UUID is required`);
  return value;
}

async function main() {
  console.log("SUITE canonical-audit-identity-local-production-read-e2e");
  try {
    const routeAuditId = argument("--route-audit");
    const expectedAuditId = argument("--canonical-audit");
    const expectedVersionId = argument("--canonical-version");
    const environment = await getSupabaseEnvironment(process.cwd());
    const container = await databaseContainer();
    const adminA = await authenticate(environment, "canonical-audit-admin-a");
    const adminB = await authenticate(environment, "canonical-audit-admin-b");
    await sql(container, `update public.user_profiles set role='PPKIAdmin' where id in ('${adminA.id}','${adminB.id}');`);
    const apiUrl = await startServices(environment);

    const routeSummary = await api(apiUrl, environment, adminA.token, `/audits/${routeAuditId}`);
    const automatic = routeSummary.body?.automaticRemediation;
    report("route-a1-summary-owns-canonical-a2-v2", routeSummary.status === 200
      && routeSummary.body?.id === routeAuditId && automatic?.state === "Completed"
      && automatic?.reauditJobId === expectedAuditId
      && automatic?.resultDocumentVersionId === expectedVersionId);

    const canonicalSummary = await api(apiUrl, environment, adminA.token, `/audits/${expectedAuditId}`);
    report("canonical-a2-summary-is-coherent", canonicalSummary.status === 200
      && canonicalSummary.body?.id === expectedAuditId
      && canonicalSummary.body?.documentVersionId === expectedVersionId
      && canonicalSummary.body?.errorCount === 197
      && canonicalSummary.body?.findingDispositions?.resolvedCount
        + canonicalSummary.body?.findingDispositions?.ignoredCount
        + canonicalSummary.body?.findingDispositions?.requiresReviewCount === 197);
    report("canonical-a2-exposes-verified-a1-auto-history", canonicalSummary.body?.automaticRemediationHistory?.sourceAuditJobId === routeAuditId
      && canonicalSummary.body?.automaticRemediationHistory?.operationCount === 2031
      && canonicalSummary.body?.automaticRemediationHistory?.verifiedResolvedCount === 2031
      && canonicalSummary.body?.automaticRemediationHistory?.stillDetectedCount === 0);

    const remainingA = await api(apiUrl, environment, adminA.token,
      `/audits/${expectedAuditId}/findings?disposition=RequiresReview&page=1&pageSize=25`);
    const remainingB = await api(apiUrl, environment, adminB.token,
      `/audits/${expectedAuditId}/findings?disposition=RequiresReview&page=1&pageSize=25`);
    report("all-197-canonical-findings-remain-visible-and-db-paginated", remainingA.status === 200
      && remainingA.body?.totalCount === 197 && remainingA.body?.items?.length === 25
      && remainingA.body?.items?.every(value => value.auditId === expectedAuditId
        && value.actionAvailability === "None" && value.presentation?.propertyLabel
        && value.presentation?.problem && value.presentation?.evidenceState));
    report("admin-a-and-b-see-the-same-canonical-remaining-set", remainingB.status === 200
      && remainingB.body?.totalCount === remainingA.body?.totalCount
      && remainingB.body?.items?.map(value => value.id).join("|")
        === remainingA.body?.items?.map(value => value.id).join("|"));
    const margin = await api(apiUrl, environment, adminA.token,
      `/audits/${expectedAuditId}/findings?disposition=RequiresReview&ruleCode=PPKI-LAY-008&page=1&pageSize=1`);
    const section = await api(apiUrl, environment, adminA.token,
      `/audits/${expectedAuditId}/findings?disposition=RequiresReview&ruleCode=PPKI-ABS-013&page=1&pageSize=1`);
    const automaticHistoryA = await api(apiUrl, environment, adminA.token,
      `/audits/${routeAuditId}/findings?disposition=Resolved&automaticallyResolved=true&page=1&pageSize=25`);
    const automaticHistoryB = await api(apiUrl, environment, adminB.token,
      `/audits/${routeAuditId}/findings?disposition=Resolved&automaticallyResolved=true&page=1&pageSize=25`);
    report("formatting-and-section-evidence-is-sanitized-and-human-readable",
      margin.body?.items?.[0]?.presentation?.propertyLabel === "Margin kiri"
      && margin.body?.items?.[0]?.presentation?.beforeValue === "3 cm"
      && margin.body?.items?.[0]?.presentation?.expectedValue === "4 cm"
      && section.body?.items?.[0]?.presentation?.beforeLabel === "Ditemukan"
      && section.body?.items?.[0]?.presentation?.beforeValue === "Belum tersedia"
      && section.body?.items?.[0]?.presentation?.expectedLabel === "Wajib");
    report("verified-auto-history-is-db-paginated-and-admin-consistent",
      automaticHistoryA.body?.totalCount === 2031 && automaticHistoryA.body?.items?.length === 25
      && automaticHistoryB.body?.items?.map(value => value.id).join("|")
        === automaticHistoryA.body?.items?.map(value => value.id).join("|"));
    console.log(`visible-summary=${canonicalSummary.body?.findingCount} masalah ditemukan | ${canonicalSummary.body?.automaticRemediationHistory?.verifiedResolvedCount} diperbaiki otomatis (riwayat terverifikasi) | 0 perlu keputusan | ${canonicalSummary.body?.findingDispositions?.ignoredCount} diabaikan | ${canonicalSummary.body?.findingDispositions?.requiresReviewCount} masih perlu pemeriksaan`);
    console.log(`visible-remaining-page=${remainingA.body?.items?.length}/${remainingA.body?.totalCount} first=${remainingA.body?.items?.[0]?.ruleCode}|${remainingA.body?.items?.[0]?.element}|${remainingA.body?.items?.[0]?.resolutionState}|${remainingA.body?.items?.[0]?.reviewState}`);
    console.log(`visible-formatting=${margin.body?.items?.[0]?.presentation?.propertyLabel}|${margin.body?.items?.[0]?.presentation?.beforeLabel}:${margin.body?.items?.[0]?.presentation?.beforeValue}|${margin.body?.items?.[0]?.presentation?.expectedLabel}:${margin.body?.items?.[0]?.presentation?.expectedValue}`);
    console.log(`visible-section=${section.body?.items?.[0]?.presentation?.propertyLabel}|${section.body?.items?.[0]?.presentation?.beforeLabel}:${section.body?.items?.[0]?.presentation?.beforeValue}|${section.body?.items?.[0]?.presentation?.expectedLabel}:${section.body?.items?.[0]?.presentation?.expectedValue}`);
    console.log(`visible-auto-history=${automaticHistoryA.body?.items?.length}/${automaticHistoryA.body?.totalCount} first=${automaticHistoryA.body?.items?.[0]?.presentation?.propertyLabel}|before:${automaticHistoryA.body?.items?.[0]?.presentation?.beforeValue}|after:${automaticHistoryA.body?.items?.[0]?.presentation?.expectedValue}|verified`);

    const stale = await api(apiUrl, environment, adminA.token,
      `/audits/${routeAuditId}/text-corrections?page=1&pageSize=25`);
    const canonicalA = await api(apiUrl, environment, adminA.token,
      `/audits/${expectedAuditId}/text-corrections?page=1&pageSize=25`);
    const canonicalB = await api(apiUrl, environment, adminB.token,
      `/audits/${expectedAuditId}/text-corrections?page=1&pageSize=25`);
    report("historical-a1-is-non-enumerating", stale.status === 404);
    report("admin-a-and-b-use-the-same-canonical-a2", canonicalA.status === 200 && canonicalB.status === 200
      && canonicalA.body?.auditId === expectedAuditId && canonicalB.body?.auditId === expectedAuditId
      && canonicalA.body?.documentVersionId === expectedVersionId
      && canonicalA.body?.totalCount === canonicalB.body?.totalCount);
    console.log(`canonical-corrections-path=/api/audits/${expectedAuditId}/text-corrections?page=1&pageSize=25 proposals=${canonicalA.body?.totalCount}`);
    console.log("canonical-audit-identity-production-read-e2e-completed: PASS");
  } catch (error) {
    console.log(`BLOCKER: ${error instanceof Error ? error.message : "canonical identity runtime unavailable"}`);
    const diagnostic = safeServiceDiagnostics();
    if (diagnostic) console.log(`SAFE-DIAGNOSTIC: ${diagnostic}`);
    console.log("canonical-audit-identity-production-read-e2e-completed: FAIL");
    process.exitCode = 1;
  } finally {
    await stopServices();
  }
}

main();
