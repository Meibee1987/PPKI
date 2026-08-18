import { ApiContractError, type DocumentRenderState, type PageLocationConfidence } from "./audit-contract.ts";

export const correctionActions = ["UseSuggestion", "EditManual", "Ignore"] as const;
export const correctionBatchStates = ["Pending", "Queued", "Processing", "ReauditPending", "VerificationPending", "Completed", "Failed", "Conflict"] as const;
export const correctionAnchorStates = ["Exact", "Stale", "Unsupported"] as const;
export type CorrectionAction = typeof correctionActions[number];
export type CorrectionBatchState = typeof correctionBatchStates[number];
export type CorrectionAnchorState = typeof correctionAnchorStates[number];
export type CorrectionPageLocation = { pageNumber: number | null; confidence: PageLocationConfidence } | null;
export type EffectiveCorrectionDecision = { id: string; sequence: number; action: CorrectionAction; actorUserId: string };
export type TextCorrectionProposal = {
  id: string; detectorRule: string; category: string; state: string; suggestionAvailable: boolean;
  pageLocation: CorrectionPageLocation; anchorStatus: CorrectionAnchorState;
  effectiveDecision: EffectiveCorrectionDecision | null;
};
export type TextCorrectionSummary = {
  undecidedCount: number; useSuggestionCount: number; editManualCount: number; ignoredCount: number;
  eligibleDecisionCount: number; historicalCount: number;
};
export type TextCorrectionContext = {
  proposalId: string; documentVersionId: string; anchorStatus: CorrectionAnchorState; safeFailureCode: string | null;
  targetText: string | null; context: string | null; suggestedReplacement: string;
  targetOffsetInContext: number | null; prefixTruncated: boolean; suffixTruncated: boolean;
  pageLocation: CorrectionPageLocation;
};
export type CorrectionBatchStatus = {
  id: string; sourceAuditId: string; sourceDocumentVersionId: string; fixExecutionId: string | null;
  resultDocumentVersionId: string | null; reauditId: string | null; state: CorrectionBatchState;
  decisionCount: number; safeFailureCode: string | null; verificationCounts: Record<string, number>;
};
export type TextCorrectionPage = {
  auditId: string; documentVersionId: string; page: number; pageSize: number; totalCount: number;
  items: TextCorrectionProposal[]; summary: TextCorrectionSummary; activeBatch: CorrectionBatchStatus | null;
};
export type CorrectionDecisionAccepted = {
  id: string; proposalId: string; sequence: number; action: CorrectionAction; actorUserId: string;
  createdAt: string; replayed: boolean;
};
export type CorrectionBatchAccepted = {
  id: string; sourceAuditId: string; sourceDocumentVersionId: string; fixExecutionId: string | null;
  state: CorrectionBatchState; decisionCount: number; replayed: boolean;
};
export type DocumentPreviewState = {
  state: DocumentRenderState; pageCount: number | null; previewAvailable: boolean;
};

type Row = Record<string, unknown>;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function row(value: unknown): Row { if (!value || typeof value !== "object" || Array.isArray(value)) throw new ApiContractError(); return value as Row; }
function str(value: unknown): string { if (typeof value !== "string") throw new ApiContractError(); return value; }
function nullableStr(value: unknown): string | null { return value === null ? null : str(value); }
function uuid(value: unknown): string { const result = str(value); if (!uuidPattern.test(result)) throw new ApiContractError(); return result; }
function nullableUuid(value: unknown): string | null { return value === null ? null : uuid(value); }
function integer(value: unknown): number { if (typeof value !== "number" || !Number.isInteger(value) || value < 0) throw new ApiContractError(); return value; }
function positive(value: unknown): number { const result = integer(value); if (result < 1) throw new ApiContractError(); return result; }
function bool(value: unknown): boolean { if (typeof value !== "boolean") throw new ApiContractError(); return value; }
function enm<const T extends readonly string[]>(value: unknown, allowed: T): T[number] { if (typeof value !== "string" || !allowed.includes(value)) throw new ApiContractError(); return value as T[number]; }
function location(value: unknown): CorrectionPageLocation {
  if (value === null) return null;
  const data = row(value);
  return { pageNumber: data.pageNumber === null ? null : positive(data.pageNumber), confidence: enm(data.confidence, ["Exact", "Estimated", "Unavailable"] as const) };
}
function decision(value: unknown): EffectiveCorrectionDecision | null {
  if (value === null) return null; const data = row(value);
  return { id: uuid(data.id), sequence: positive(data.sequence), action: enm(data.action, correctionActions), actorUserId: uuid(data.actorUserId) };
}
function verificationCounts(value: unknown): Record<string, number> {
  return Object.fromEntries(Object.entries(row(value)).map(([key, count]) => [key, integer(count)]));
}
export function parseCorrectionBatchStatus(value: unknown): CorrectionBatchStatus {
  const data = row(value); return {
    id: uuid(data.id), sourceAuditId: uuid(data.sourceAuditId), sourceDocumentVersionId: uuid(data.sourceDocumentVersionId),
    fixExecutionId: nullableUuid(data.fixExecutionId), resultDocumentVersionId: nullableUuid(data.resultDocumentVersionId),
    reauditId: nullableUuid(data.reauditId), state: enm(data.state, correctionBatchStates), decisionCount: integer(data.decisionCount),
    safeFailureCode: nullableStr(data.safeFailureCode), verificationCounts: verificationCounts(data.verificationCounts),
  };
}
export function parseTextCorrectionPage(value: unknown): TextCorrectionPage {
  const data = row(value), summary = row(data.summary);
  if (!Array.isArray(data.items)) throw new ApiContractError();
  return {
    auditId: uuid(data.auditId), documentVersionId: uuid(data.documentVersionId), page: positive(data.page), pageSize: positive(data.pageSize),
    totalCount: integer(data.totalCount), items: data.items.map(value => { const item = row(value); return {
      id: uuid(item.id), detectorRule: str(item.detectorRule), category: str(item.category), state: str(item.state),
      suggestionAvailable: bool(item.suggestionAvailable), pageLocation: location(item.pageLocation),
      anchorStatus: enm(item.anchorStatus, correctionAnchorStates), effectiveDecision: decision(item.effectiveDecision),
    }; }),
    summary: {
      undecidedCount: integer(summary.undecidedCount), useSuggestionCount: integer(summary.useSuggestionCount),
      editManualCount: integer(summary.editManualCount), ignoredCount: integer(summary.ignoredCount),
      eligibleDecisionCount: integer(summary.eligibleDecisionCount), historicalCount: integer(summary.historicalCount),
    },
    activeBatch: data.activeBatch === null ? null : parseCorrectionBatchStatus(data.activeBatch),
  };
}
export function parseTextCorrectionContext(value: unknown): TextCorrectionContext {
  const data = row(value); return {
    proposalId: uuid(data.proposalId), documentVersionId: uuid(data.documentVersionId), anchorStatus: enm(data.anchorStatus, correctionAnchorStates),
    safeFailureCode: nullableStr(data.safeFailureCode), targetText: nullableStr(data.targetText), context: nullableStr(data.context),
    suggestedReplacement: str(data.suggestedReplacement), targetOffsetInContext: data.targetOffsetInContext === null ? null : integer(data.targetOffsetInContext),
    prefixTruncated: bool(data.prefixTruncated), suffixTruncated: bool(data.suffixTruncated), pageLocation: location(data.pageLocation),
  };
}
export function parseCorrectionDecisionAccepted(value: unknown): CorrectionDecisionAccepted {
  const data = row(value); return { id: uuid(data.id), proposalId: uuid(data.proposalId), sequence: positive(data.sequence),
    action: enm(data.action, correctionActions), actorUserId: uuid(data.actorUserId), createdAt: str(data.createdAt), replayed: bool(data.replayed) };
}
export function parseCorrectionBatchAccepted(value: unknown): CorrectionBatchAccepted {
  const data = row(value); return { id: uuid(data.id), sourceAuditId: uuid(data.sourceAuditId), sourceDocumentVersionId: uuid(data.sourceDocumentVersionId),
    fixExecutionId: nullableUuid(data.fixExecutionId), state: enm(data.state, correctionBatchStates), decisionCount: integer(data.decisionCount), replayed: bool(data.replayed) };
}
export function parseDocumentPreviewState(value: unknown): DocumentPreviewState {
  const data = row(value); return { state: enm(data.state, ["Pending", "Processing", "Completed", "Failed"] as const),
    pageCount: data.pageCount === null ? null : integer(data.pageCount), previewAvailable: bool(data.previewAvailable) };
}
