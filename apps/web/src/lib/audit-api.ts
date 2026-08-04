import { apiFetch } from "./api";
import {
  auditFindingDetailPath,
  auditFindingsPath,
  auditSummaryPath,
  parseAuditFindingDetail,
  parseAuditFindingPage,
  parseAuditSummary,
  type AuditFindingDetail,
  type AuditFindingPage,
  type AuditSummary,
  type FindingFilters,
} from "./audit-contract";

export async function getAuditSummary(auditId: string, signal?: AbortSignal): Promise<AuditSummary> {
  return parseAuditSummary(await apiFetch<unknown>(auditSummaryPath(auditId), { signal }));
}

export async function listAuditFindings(auditId: string, filters: FindingFilters, signal?: AbortSignal): Promise<AuditFindingPage> {
  return parseAuditFindingPage(await apiFetch<unknown>(auditFindingsPath(auditId, filters), { signal }));
}

export async function getAuditFinding(auditId: string, findingId: string, signal?: AbortSignal): Promise<AuditFindingDetail> {
  return parseAuditFindingDetail(await apiFetch<unknown>(auditFindingDetailPath(auditId, findingId), { signal }));
}
