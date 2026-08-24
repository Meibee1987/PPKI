import { apiFetch } from "./api";
import { parseAuditSummary, type AuditSummary } from "./audit-contract";
import {
  parseAuditAccepted,
  parseDocumentCreated,
  parseDocumentDetail,
  parseDocumentList,
  type AuditAccepted,
  type DocumentCreated,
  type DocumentDetail,
  type DocumentListItem,
} from "./document-contract";

export async function listDocuments(signal?: AbortSignal): Promise<DocumentListItem[]> {
  return parseDocumentList(await apiFetch<unknown>("/api/documents", { signal }));
}

export async function getDocument(id: string, signal?: AbortSignal): Promise<DocumentDetail> {
  return parseDocumentDetail(await apiFetch<unknown>(`/api/documents/${encodeURIComponent(id)}`, { signal }));
}

export async function createDocument(form: FormData, signal?: AbortSignal): Promise<DocumentCreated> {
  return parseDocumentCreated(await apiFetch<unknown>("/api/documents", { method: "POST", body: form, signal }));
}

export async function startAudit(versionId: string, signal?: AbortSignal): Promise<AuditAccepted> {
  return parseAuditAccepted(await apiFetch<unknown>(`/api/document-versions/${encodeURIComponent(versionId)}/audits`, { method: "POST", signal }));
}

export async function getAuditStatus(auditId: string, signal?: AbortSignal): Promise<AuditSummary> {
  return parseAuditSummary(await apiFetch<unknown>(`/api/audits/${encodeURIComponent(auditId)}`, { signal }));
}
