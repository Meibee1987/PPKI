import { ApiContractError } from "./audit-contract.ts";

export const fixExecutionStates = ["Queued", "Processing", "Completed", "Failed", "NoChange"] as const;
export const fixFailureCategories = ["Conflict", "InvalidInput", "InvalidSource", "InvalidPlan", "CapabilityUnavailable", "TransientInfrastructure", "TerminalInfrastructure"] as const;
export const fixPlanStates = ["Ready", "PartiallyReady", "NotAvailable", "InvalidSelection", "InvalidSnapshot", "Conflict", "AuditIncomplete", "InvalidConfiguration"] as const;
export const fixPlanDispositions = ["Planned", "Unsupported", "Conflict", "InvalidSnapshot"] as const;
export const comparisonStatuses = ["StillDetected", "Changed", "NoLongerDetected", "NewlyDetected"] as const;
export const resolutionStates = ["Open", "Applied", "ReauditPending", "VerifiedResolved", "VerifiedStillDetected"] as const;
export const reviewStates = ["NoReview", "PendingReview", "NeedsRevision", "ManualRemediationApproved", "ManualRemediationReported", "Rejected", "Ignored", "AcceptedRisk"] as const;
export const reviewRequestTypes = ["ManualRemediation", "Ignore", "AcceptedRisk"] as const;
export const reviewDecisions = ["ApproveManualRemediation", "Ignore", "AcceptRisk", "NeedsRevision", "Reject"] as const;

export type FixExecutionState = typeof fixExecutionStates[number];
export type FixFailureCategory = typeof fixFailureCategories[number];
export type FixPlanState = typeof fixPlanStates[number];
export type FixPlanItemDisposition = typeof fixPlanDispositions[number];
export type AuditComparisonStatus = typeof comparisonStatuses[number];
export type FindingResolutionState = typeof resolutionStates[number];
export type FindingReviewState = typeof reviewStates[number];
export type FindingReviewRequestType = typeof reviewRequestTypes[number];
export type FindingReviewDecision = typeof reviewDecisions[number];

export type FixPlanItem = { findingId: string; ruleCode: string; validationKey: string; ruleOrdinal: number; disposition: FixPlanItemDisposition; diagnosticCode: string };
export type FixPlanPreview = {
  auditId: string; sourceDocumentVersionId: string; plannerVersion: string;
  selectedFindingCount: number; plannedFindingCount: number; unsupportedFindingCount: number;
  conflictFindingCount: number; invalidFindingCount: number; operationCount: number;
  items: FixPlanItem[]; planHash: string; state: FixPlanState; diagnostics: string[];
};
export type FixExecutionAccepted = { id: string; auditId: string; sourceDocumentVersionId: string; planHash: string; plannerVersion: string; state: FixExecutionState; selectedFindingCount: number; plannedOperationCount: number; queuedAt: string; replayed: boolean };
export type FixExecutionStatus = {
  id: string; auditId: string; sourceDocumentVersionId: string; resultDocumentVersionId: string | null;
  state: FixExecutionState; plannedOperationCount: number; completedOperationCount: number; failedOperationCount: number;
  failureCategory: FixFailureCategory | null; failureCode: string | null; attemptCount: number; maxAttempts: number;
  retryPending: boolean; leaseState: string; queuedAt: string; startedAt: string | null; completedAt: string | null;
};
export type ReauditAccepted = { auditId: string; status: string; sourceAuditId: string; sourceFixExecutionId: string; documentVersionId: string; queuedAt: string; replayed: boolean };
export type AuditComparisonItem = { status: AuditComparisonStatus; ruleCode: string; validationKey: string; domain: string; element: string; severity: string; beforeFindingId: string | null; afterFindingId: string | null };
export type AuditComparison = { sourceAuditId: string; resultAuditId: string; fixExecutionId: string; comparisonState: string; totalCount: number; items: AuditComparisonItem[]; counts: Record<AuditComparisonStatus, number> };
export type FindingResolution = { findingId: string; auditId: string; currentState: FindingResolutionState; fixExecutionId: string | null; reAuditId: string | null; comparisonStatus: AuditComparisonStatus | null; eventCount: number };
export type FindingReviewEvent = { sequence: number; eventType: string; requestedDisposition: FindingReviewRequestType | null; decision: FindingReviewDecision | null; note: string | null; createdAt: string };
export type FindingReview = {
  reviewCaseId: string | null; findingId: string; auditId: string; sourceDocumentVersionId: string; resolutionState: FindingResolutionState;
  reviewState: FindingReviewState; requestedDisposition: FindingReviewRequestType | null;
  permissions: { canRequestReview: boolean; canReportManualRemediation: boolean; canDecide: boolean };
  allowedDecisions: FindingReviewDecision[]; events: FindingReviewEvent[];
};

type R = Record<string, unknown>;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const shaPattern = /^[0-9a-f]{64}$/;
function rec(value: unknown): R { if (!value || typeof value !== "object" || Array.isArray(value)) throw new ApiContractError(); return value as R; }
function str(value: unknown): string { if (typeof value !== "string") throw new ApiContractError(); return value; }
function nullableStr(value: unknown): string | null { return value === null ? null : str(value); }
function uuid(value: unknown): string { const result = str(value); if (!uuidPattern.test(result)) throw new ApiContractError(); return result; }
function nullableUuid(value: unknown): string | null { return value === null ? null : uuid(value); }
function integer(value: unknown): number { if (typeof value !== "number" || !Number.isInteger(value) || value < 0) throw new ApiContractError(); return value; }
function bool(value: unknown): boolean { if (typeof value !== "boolean") throw new ApiContractError(); return value; }
function enm<const T extends readonly string[]>(value: unknown, values: T): T[number] { if (typeof value !== "string" || !values.includes(value)) throw new ApiContractError(); return value as T[number]; }
function nullableEnum<const T extends readonly string[]>(value: unknown, values: T): T[number] | null { return value === null ? null : enm(value, values); }
function strings(value: unknown): string[] { if (!Array.isArray(value)) throw new ApiContractError(); return value.map(str); }
function timestamp(value: unknown): string { const result = str(value); if (Number.isNaN(Date.parse(result))) throw new ApiContractError(); return result; }
function nullableTimestamp(value: unknown): string | null { return value === null ? null : timestamp(value); }

export function parseFixPlanPreview(value: unknown): FixPlanPreview {
  const data = rec(value); if (!Array.isArray(data.items) || !Array.isArray(data.operations)) throw new ApiContractError();
  const planHash = str(data.planHash); if (!shaPattern.test(planHash)) throw new ApiContractError();
  return {
    auditId: uuid(data.auditId), sourceDocumentVersionId: uuid(data.sourceDocumentVersionId), plannerVersion: str(data.plannerVersion),
    selectedFindingCount: integer(data.selectedFindingCount), plannedFindingCount: integer(data.plannedFindingCount),
    unsupportedFindingCount: integer(data.unsupportedFindingCount), conflictFindingCount: integer(data.conflictFindingCount), invalidFindingCount: integer(data.invalidFindingCount),
    operationCount: data.operations.length,
    items: data.items.map(item => { const row = rec(item); return { findingId: uuid(row.findingId), ruleCode: str(row.ruleCode), validationKey: str(row.validationKey), ruleOrdinal: integer(row.ruleOrdinal), disposition: enm(row.disposition, fixPlanDispositions), diagnosticCode: str(row.diagnosticCode) }; }),
    planHash, state: enm(data.state, fixPlanStates), diagnostics: strings(data.diagnostics),
  };
}

export function parseFixExecutionAccepted(value: unknown): FixExecutionAccepted {
  const data = rec(value); return { id: uuid(data.id), auditId: uuid(data.auditId), sourceDocumentVersionId: uuid(data.sourceDocumentVersionId), planHash: str(data.planHash), plannerVersion: str(data.plannerVersion), state: enm(data.state, fixExecutionStates), selectedFindingCount: integer(data.selectedFindingCount), plannedOperationCount: integer(data.plannedOperationCount), queuedAt: timestamp(data.queuedAt), replayed: bool(data.replayed) };
}

export function parseFixExecutionStatus(value: unknown): FixExecutionStatus {
  const data = rec(value); const safeFailureCode = data.safeFailureCode ?? data.failureCode ?? null;
  return { id: uuid(data.id), auditId: uuid(data.auditId), sourceDocumentVersionId: uuid(data.sourceDocumentVersionId), resultDocumentVersionId: nullableUuid(data.resultDocumentVersionId), state: enm(data.state, fixExecutionStates), plannedOperationCount: integer(data.plannedOperationCount), completedOperationCount: integer(data.completedOperationCount), failedOperationCount: integer(data.failedOperationCount), failureCategory: nullableEnum(data.failureCategory, fixFailureCategories), failureCode: nullableStr(safeFailureCode), attemptCount: integer(data.attemptCount), maxAttempts: integer(data.maxAttempts), retryPending: bool(data.retryPending), leaseState: str(data.leaseState), queuedAt: timestamp(data.queuedAt), startedAt: nullableTimestamp(data.startedAt), completedAt: nullableTimestamp(data.completedAt) };
}

export function parseReauditAccepted(value: unknown): ReauditAccepted { const data = rec(value); return { auditId: uuid(data.auditId), status: str(data.status), sourceAuditId: uuid(data.sourceAuditId), sourceFixExecutionId: uuid(data.sourceFixExecutionId), documentVersionId: uuid(data.documentVersionId), queuedAt: timestamp(data.queuedAt), replayed: bool(data.replayed) }; }

export function parseAuditComparison(value: unknown): AuditComparison {
  const data = rec(value), summary = rec(data.summary); if (!Array.isArray(data.items)) throw new ApiContractError();
  return { sourceAuditId: uuid(data.sourceAuditId), resultAuditId: uuid(data.resultAuditId), fixExecutionId: uuid(data.fixExecutionId), comparisonState: str(data.comparisonState), totalCount: integer(data.totalCount), counts: { StillDetected: integer(summary.stillDetectedCount), Changed: integer(summary.changedCount), NoLongerDetected: integer(summary.noLongerDetectedCount), NewlyDetected: integer(summary.newlyDetectedCount) }, items: data.items.map(value => { const item = rec(value), before = item.before === null ? null : rec(item.before), after = item.after === null ? null : rec(item.after); return { status: enm(item.status, comparisonStatuses), ruleCode: str(item.ruleCode), validationKey: str(item.validationKey), domain: str(item.domain), element: str(item.element), severity: str(item.severity), beforeFindingId: before ? uuid(before.id) : null, afterFindingId: after ? uuid(after.id) : null }; }) };
}

export function parseFindingResolution(value: unknown): FindingResolution { const data = rec(value); return { findingId: uuid(data.findingId), auditId: uuid(data.auditId), currentState: enm(data.currentState, resolutionStates), fixExecutionId: nullableUuid(data.fixExecutionId), reAuditId: nullableUuid(data.reAuditId), comparisonStatus: nullableEnum(data.comparisonStatus, comparisonStatuses), eventCount: integer(data.eventCount) }; }

export function parseFindingReview(value: unknown): FindingReview {
  const data = rec(value), permissions = rec(data.permissions); if (!Array.isArray(data.allowedDecisions) || !Array.isArray(data.events)) throw new ApiContractError();
  const events = data.events.map(value => { const event = rec(value); return { sequence: integer(event.sequence), eventType: str(event.eventType), requestedDisposition: nullableEnum(event.requestedDisposition, reviewRequestTypes), decision: nullableEnum(event.decision, reviewDecisions), note: nullableStr(event.note), createdAt: timestamp(event.createdAt) }; });
  if (events.some((event, index) => index > 0 && event.sequence <= events[index - 1].sequence)) throw new ApiContractError();
  return { reviewCaseId: nullableUuid(data.reviewCaseId), findingId: uuid(data.findingId), auditId: uuid(data.auditId), sourceDocumentVersionId: uuid(data.sourceDocumentVersionId), resolutionState: enm(data.resolutionState, resolutionStates), reviewState: enm(data.reviewState, reviewStates), requestedDisposition: nullableEnum(data.requestedDisposition, reviewRequestTypes), permissions: { canRequestReview: bool(permissions.canRequestReview), canReportManualRemediation: bool(permissions.canReportManualRemediation), canDecide: bool(permissions.canDecide) }, allowedDecisions: data.allowedDecisions.map(value => enm(value, reviewDecisions)), events };
}

export function parseFindingReviewCommand(value: unknown): FindingReview { const data = rec(value); return parseFindingReview(data.review); }
