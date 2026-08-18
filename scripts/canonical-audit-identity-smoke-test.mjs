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
      && canonicalSummary.body?.errorCount === 197);

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
