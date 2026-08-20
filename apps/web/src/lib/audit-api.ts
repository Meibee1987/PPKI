import { apiFetch } from "./api";
import {
  auditFindingDetailPath,
  auditFindingsPath,
  auditSummaryPath,
  parseAuditFindingDetail,
  parseAuditFindingPage,
  parseStructuralFindingExcerpt,
  structuralFindingExcerptPath,
  parseAuditSummary,
  type AuditFindingDetail,
  type AuditFindingPage,
  type AuditSummary,
  type FindingFilters,
  type StructuralFindingExcerpt,
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

export async function getStructuralFindingExcerpt(auditId: string, findingId: string, signal?: AbortSignal): Promise<StructuralFindingExcerpt> {
  return parseStructuralFindingExcerpt(await apiFetch<unknown>(structuralFindingExcerptPath(auditId, findingId), { signal }));
}
