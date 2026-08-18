import { apiFetch } from "./api";
import { parseCorrectionBatchAccepted, parseCorrectionBatchStatus, parseCorrectionDecisionAccepted, parseDocumentPreviewState, parseTextCorrectionContext, parseTextCorrectionPage, type CorrectionAction, type CorrectionBatchAccepted, type CorrectionBatchStatus, type CorrectionDecisionAccepted, type DocumentPreviewState, type TextCorrectionContext, type TextCorrectionPage } from "./text-correction-contract";
import { textCorrectionBatchPath, textCorrectionsPath } from "./text-correction-paths";

export async function listTextCorrections(auditId: string, page: number, pageSize: number, signal?: AbortSignal): Promise<TextCorrectionPage> {
  return parseTextCorrectionPage(await apiFetch(textCorrectionsPath(auditId, page, pageSize), { signal }));
}
export async function getTextCorrectionContext(proposalId: string, signal?: AbortSignal): Promise<TextCorrectionContext> {
  return parseTextCorrectionContext(await apiFetch(`/api/text-corrections/${encodeURIComponent(proposalId)}/context`, { signal }));
}
export async function submitTextCorrectionDecision(proposalId: string, action: CorrectionAction, idempotencyKey: string, manualReplacement?: string): Promise<CorrectionDecisionAccepted> {
  const body = action === "EditManual" ? { action, manualReplacement } : { action };
  return parseCorrectionDecisionAccepted(await apiFetch(`/api/text-corrections/${encodeURIComponent(proposalId)}/decisions`, {
    method: "POST", headers: { "Idempotency-Key": idempotencyKey }, body: JSON.stringify(body),
  }));
}
export async function createTextCorrectionBatch(auditId: string, idempotencyKey: string): Promise<CorrectionBatchAccepted> {
  return parseCorrectionBatchAccepted(await apiFetch(textCorrectionBatchPath(auditId), {
    method: "POST", headers: { "Idempotency-Key": idempotencyKey }, body: JSON.stringify({}),
  }));
}
export async function getTextCorrectionBatch(batchId: string, signal?: AbortSignal): Promise<CorrectionBatchStatus> {
  return parseCorrectionBatchStatus(await apiFetch(`/api/text-correction-batches/${encodeURIComponent(batchId)}`, { signal }));
}
export async function getDocumentPreviewState(versionId: string, signal?: AbortSignal): Promise<DocumentPreviewState> {
  return parseDocumentPreviewState(await apiFetch(`/api/document-versions/${encodeURIComponent(versionId)}/preview-state`, { signal }));
}
