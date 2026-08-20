export const auditStatuses = ["Queued", "Processing", "Completed", "Failed", "Cancelled"] as const;
export const severities = ["Error", "Warning", "Info"] as const;
export const fixModes = ["Auto", "Confirm", "Manual", "Report"] as const;
export const scoreStates = ["Calculated", "NotConfigured", "InvalidConfiguration", "NotApplicable", "AuditIncomplete"] as const;
export const actionAvailabilities = ["None", "Automatic"] as const;
export const automaticRemediationStates = ["Pending", "NoAction", "Queued", "Processing", "ReauditPending", "Completed", "Failed", "Conflict"] as const;
export const textCorrectionAnalysisStates = ["AwaitingAnalysis", "Pending", "Processing", "Completed", "Failed", "Skipped"] as const;
export const documentRenderStates = ["Pending", "Processing", "Completed", "Failed"] as const;
export const pageLocationConfidences = ["Exact", "Estimated", "Unavailable"] as const;
export const findingDispositions = ["Resolved", "Ignored", "RequiresReview"] as const;

export type AuditStatus = (typeof auditStatuses)[number];
export type Severity = (typeof severities)[number];
export type FixMode = (typeof fixModes)[number];
export type ScoreState = (typeof scoreStates)[number];
export type ActionAvailability = (typeof actionAvailabilities)[number];
export type AutomaticRemediationState = (typeof automaticRemediationStates)[number];
export type TextCorrectionAnalysisState = (typeof textCorrectionAnalysisStates)[number];
export type DocumentRenderState = (typeof documentRenderStates)[number];
export type PageLocationConfidence = (typeof pageLocationConfidences)[number];
export type FindingDisposition = (typeof findingDispositions)[number];
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };

export function isTextCorrectionAnalysisTransitional(state: TextCorrectionAnalysisState): boolean {
  return state === "AwaitingAnalysis" || state === "Pending" || state === "Processing";
}

export type AuditSource = {
  sourceSection: string | null;
  pdfPage: number | null;
  printedPage: string | null;
};

export type AuditFinding = {
  id: string;
  auditId: string;
  ruleOrdinal: number;
  ruleCode: string;
  domain: string;
  validationKey: string;
  element: string;
  severity: Severity;
  fixMode: FixMode;
  findingState: string;
  resolutionState: string;
  reviewState: string;
  reasonCode: string;
  message: string;
  presentation: {
    kind: string; propertyLabel: string; problem: string;
    beforeLabel: string; beforeValue: string | null;
    expectedLabel: string; expectedValue: string | null;
    evidenceState: "Complete" | "Partial" | "Unavailable";
  };
  actual: JsonValue;
  expected: JsonValue;
  location: JsonValue;
  confidence: number | null;
  source: AuditSource;
  actionAvailability: ActionAvailability;
  pageLocation: { pageNumber: number | null; confidence: PageLocationConfidence; state: DocumentRenderState | null };
};

export type AuditFindingDetail = AuditFinding & { documentVersionId: string };

export type StructuralFindingExcerpt = {
  findingId: string;
  documentVersionId: string;
  status: "Exact" | "Unavailable";
  targetType: "Heading" | "Paragraph" | "Section" | "Other";
  excerpt: string | null;
  targetText: string | null;
  pageLocation: { pageNumber: number | null; confidence: PageLocationConfidence; state: DocumentRenderState | null };
};

export type AuditFindingPage = {
  page: number;
  pageSize: number;
  totalCount: number;
  items: AuditFinding[];
};

export type AuditSummary = {
  id: string;
  status: AuditStatus;
  documentVersionId: string;
  profileVersionId: string;
  documentKindSnapshot: string | null;
  resolvedRuleSetHash: string | null;
  applicableRuleCount: number;
  totalRules: number;
  persistedFindingCount: number;
  findingCount: number;
  errorCount: number;
  warningCount: number;
  infoCount: number;
  severity: { error: number; warning: number; info: number };
  domains: { domain: string; findingCount: number }[];
  fixModes: { auto: number; confirm: number; manual: number; report: number };
  scoreState: ScoreState;
  score: number | null;
  scorePolicyVersion: string | null;
  scoreBreakdown: JsonValue;
  scoreDiagnosticCode: string | null;
  startedAt: string | null;
  completedAt: string | null;
  failureCode: string | null;
  errorMessage: string | null;
  findingDispositions: { resolvedCount: number; automaticallyResolvedCount: number; ignoredCount: number; requiresReviewCount: number };
  automaticRemediationHistory: { sourceAuditJobId: string; operationCount: number; verifiedResolvedCount: number; stillDetectedCount: number } | null;
  correctionAnalysis: { state: TextCorrectionAnalysisState };
  automaticRemediation: { state: AutomaticRemediationState; policyVersion: string; eligibleFindingCount: number; operationCount: number; verifiedResolvedCount: number; stillDetectedCount: number; failureCode: string | null; resultDocumentVersionId: string | null; reauditJobId: string | null } | null;
  documentRender: { state: DocumentRenderState; pageCount: number | null; rendererVersion: string; rendererContractVersion: string; fontProfileVersion: string; pageMapVersion: string; safeFailureCode: string | null; previewAvailable: boolean };
};

export type FindingFilters = {
  severity?: Severity;
  fixMode?: FixMode;
  disposition?: FindingDisposition;
  automaticallyResolved?: boolean;
  domain?: string;
  ruleCode?: string;
  validationKey?: string;
  page: number;
  pageSize: number;
};

export class ApiContractError extends Error {
  constructor(message = "Respons layanan tidak sesuai kontrak.") {
    super(message);
    this.name = "ApiContractError";
  }
}

type UnknownRecord = Record<string, unknown>;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function record(value: unknown): UnknownRecord {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new ApiContractError();
  return value as UnknownRecord;
}

function string(value: unknown): string {
  if (typeof value !== "string") throw new ApiContractError();
  return value;
}

function nullableString(value: unknown): string | null {
  return value === null ? null : string(value);
}

function uuid(value: unknown): string {
  const parsed = string(value);
  if (!uuidPattern.test(parsed)) throw new ApiContractError();
  return parsed;
}

function nullableUuid(value: unknown): string | null {
  return value === null ? null : uuid(value);
}

function finiteNumber(value: unknown): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new ApiContractError();
  return value;
}

function nonNegativeInteger(value: unknown): number {
  const parsed = finiteNumber(value);
  if (!Number.isInteger(parsed) || parsed < 0) throw new ApiContractError();
  return parsed;
}

function positiveInteger(value: unknown): number {
  const parsed = nonNegativeInteger(value);
  if (parsed < 1) throw new ApiContractError();
  return parsed;
}

function nullableNumber(value: unknown): number | null {
  return value === null ? null : finiteNumber(value);
}

function enumValue<const T extends readonly string[]>(value: unknown, allowed: T): T[number] {
  if (typeof value !== "string" || !allowed.includes(value)) throw new ApiContractError();
  return value as T[number];
}

function jsonValue(value: unknown, depth = 0): JsonValue {
  if (depth > 12) throw new ApiContractError();
  if (value === null || typeof value === "string" || typeof value === "boolean") return value;
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (Array.isArray(value)) return value.map(item => jsonValue(item, depth + 1));
  const source = record(value);
  return Object.fromEntries(Object.entries(source).map(([key, item]) => [key, jsonValue(item, depth + 1)]));
}

function source(value: unknown): AuditSource {
  const data = record(value);
  return {
    sourceSection: nullableString(data.sourceSection),
    pdfPage: data.pdfPage === null ? null : nonNegativeInteger(data.pdfPage),
    printedPage: nullableString(data.printedPage),
  };
}

function finding(value: unknown): AuditFinding {
  const data = record(value);
  const pageLocation = record(data.pageLocation);
  const presentation = record(data.presentation);
  return {
    id: uuid(data.id),
    auditId: uuid(data.auditId),
    ruleOrdinal: nonNegativeInteger(data.ruleOrdinal),
    ruleCode: string(data.ruleCode),
    domain: string(data.domain),
    validationKey: string(data.validationKey),
    element: string(data.element),
    severity: enumValue(data.severity, severities),
    fixMode: enumValue(data.fixMode, fixModes),
    findingState: string(data.findingState), resolutionState: string(data.resolutionState), reviewState: string(data.reviewState),
    reasonCode: string(data.reasonCode),
    message: string(data.message),
    presentation: {
      kind: string(presentation.kind), propertyLabel: string(presentation.propertyLabel), problem: string(presentation.problem),
      beforeLabel: string(presentation.beforeLabel), beforeValue: nullableString(presentation.beforeValue),
      expectedLabel: string(presentation.expectedLabel), expectedValue: nullableString(presentation.expectedValue),
      evidenceState: enumValue(presentation.evidenceState, ["Complete", "Partial", "Unavailable"] as const),
    },
    actual: jsonValue(data.actual),
    expected: jsonValue(data.expected),
    location: jsonValue(data.location),
    confidence: nullableNumber(data.confidence),
    source: source(data.source),
    actionAvailability: enumValue(data.actionAvailability, actionAvailabilities),
    pageLocation: {
      pageNumber: pageLocation.pageNumber === null ? null : positiveInteger(pageLocation.pageNumber),
      confidence: enumValue(pageLocation.confidence, pageLocationConfidences),
      state: pageLocation.state === null || pageLocation.state === undefined ? null : enumValue(pageLocation.state, documentRenderStates),
    },
  };
}

export function parseAuditSummary(value: unknown): AuditSummary {
  const data = record(value);
  const severity = record(data.severity);
  const fixModeCounts = record(data.fixModes);
  if (!Array.isArray(data.domains)) throw new ApiContractError();
  const automatic = data.automaticRemediation === null ? null : record(data.automaticRemediation);
  const automaticHistory = data.automaticRemediationHistory === null ? null : record(data.automaticRemediationHistory);
  const findingDispositionCounts = record(data.findingDispositions);
  const correctionAnalysis = record(data.correctionAnalysis);
  const documentRender = record(data.documentRender);
  return {
    id: uuid(data.id), status: enumValue(data.status, auditStatuses),
    documentVersionId: uuid(data.documentVersionId), profileVersionId: uuid(data.profileVersionId),
    documentKindSnapshot: nullableString(data.documentKindSnapshot),
    resolvedRuleSetHash: nullableString(data.resolvedRuleSetHash),
    applicableRuleCount: nonNegativeInteger(data.applicableRuleCount), totalRules: nonNegativeInteger(data.totalRules),
    persistedFindingCount: nonNegativeInteger(data.persistedFindingCount), findingCount: nonNegativeInteger(data.findingCount),
    errorCount: nonNegativeInteger(data.errorCount), warningCount: nonNegativeInteger(data.warningCount), infoCount: nonNegativeInteger(data.infoCount),
    severity: { error: nonNegativeInteger(severity.error), warning: nonNegativeInteger(severity.warning), info: nonNegativeInteger(severity.info) },
    domains: data.domains.map(item => { const entry = record(item); return { domain: string(entry.domain), findingCount: nonNegativeInteger(entry.findingCount) }; }),
    fixModes: { auto: nonNegativeInteger(fixModeCounts.auto), confirm: nonNegativeInteger(fixModeCounts.confirm), manual: nonNegativeInteger(fixModeCounts.manual), report: nonNegativeInteger(fixModeCounts.report) },
    scoreState: enumValue(data.scoreState, scoreStates), score: nullableNumber(data.score),
    scorePolicyVersion: nullableString(data.scorePolicyVersion), scoreBreakdown: jsonValue(data.scoreBreakdown),
    scoreDiagnosticCode: nullableString(data.scoreDiagnosticCode), startedAt: nullableString(data.startedAt), completedAt: nullableString(data.completedAt),
    failureCode: nullableString(data.failureCode), errorMessage: nullableString(data.errorMessage),
    findingDispositions: {
      resolvedCount: nonNegativeInteger(findingDispositionCounts.resolvedCount),
      automaticallyResolvedCount: nonNegativeInteger(findingDispositionCounts.automaticallyResolvedCount),
      ignoredCount: nonNegativeInteger(findingDispositionCounts.ignoredCount),
      requiresReviewCount: nonNegativeInteger(findingDispositionCounts.requiresReviewCount),
    },
    automaticRemediationHistory: automaticHistory === null ? null : {
      sourceAuditJobId: uuid(automaticHistory.sourceAuditJobId), operationCount: nonNegativeInteger(automaticHistory.operationCount),
      verifiedResolvedCount: nonNegativeInteger(automaticHistory.verifiedResolvedCount), stillDetectedCount: nonNegativeInteger(automaticHistory.stillDetectedCount),
    },
    correctionAnalysis: { state: enumValue(correctionAnalysis.state, textCorrectionAnalysisStates) },
    automaticRemediation: automatic === null ? null : {
      state: enumValue(automatic.state, automaticRemediationStates), policyVersion: string(automatic.policyVersion),
      eligibleFindingCount: nonNegativeInteger(automatic.eligibleFindingCount), operationCount: nonNegativeInteger(automatic.operationCount),
      verifiedResolvedCount: nonNegativeInteger(automatic.verifiedResolvedCount), stillDetectedCount: nonNegativeInteger(automatic.stillDetectedCount),
      failureCode: nullableString(automatic.failureCode), resultDocumentVersionId: nullableUuid(automatic.resultDocumentVersionId),
      reauditJobId: nullableUuid(automatic.reauditJobId),
    },
    documentRender: {
      state: enumValue(documentRender.state, documentRenderStates),
      pageCount: documentRender.pageCount === null ? null : nonNegativeInteger(documentRender.pageCount),
      rendererVersion: string(documentRender.rendererVersion), rendererContractVersion: string(documentRender.rendererContractVersion),
      fontProfileVersion: string(documentRender.fontProfileVersion), pageMapVersion: string(documentRender.pageMapVersion),
      safeFailureCode: nullableString(documentRender.safeFailureCode), previewAvailable: documentRender.previewAvailable === true,
    },
  };
}

export function parseAuditFindingPage(value: unknown): AuditFindingPage {
  const data = record(value);
  if (!Array.isArray(data.items)) throw new ApiContractError();
  const page = nonNegativeInteger(data.page);
  const pageSize = nonNegativeInteger(data.pageSize);
  if (page < 1 || pageSize < 1 || pageSize > 100) throw new ApiContractError();
  return { page, pageSize, totalCount: nonNegativeInteger(data.totalCount), items: data.items.map(finding) };
}

export function parseAuditFindingDetail(value: unknown): AuditFindingDetail {
  const data = record(value);
  return { ...finding(data), documentVersionId: uuid(data.documentVersionId) };
}

export function parseStructuralFindingExcerpt(value: unknown): StructuralFindingExcerpt {
  const data = record(value);
  const pageLocation = record(data.pageLocation);
  const status = enumValue(data.status, ["Exact", "Unavailable"] as const);
  const excerpt = nullableString(data.excerpt);
  const targetText = nullableString(data.targetText);
  if (excerpt !== null && Array.from(excerpt).length > 240) throw new ApiContractError();
  if (targetText !== null && Array.from(targetText).length > 240) throw new ApiContractError();
  if (status === "Exact" && !excerpt) throw new ApiContractError();
  if (status === "Unavailable" && (excerpt !== null || targetText !== null)) throw new ApiContractError();
  return {
    findingId: uuid(data.findingId), documentVersionId: uuid(data.documentVersionId), status,
    targetType: enumValue(data.targetType, ["Heading", "Paragraph", "Section", "Other"] as const),
    excerpt, targetText,
    pageLocation: {
      pageNumber: pageLocation.pageNumber === null ? null : positiveInteger(pageLocation.pageNumber),
      confidence: enumValue(pageLocation.confidence, pageLocationConfidences),
      state: pageLocation.state === null || pageLocation.state === undefined ? null : enumValue(pageLocation.state, documentRenderStates),
    },
  };
}

export function normalizeFindingFilters(input: URLSearchParams): FindingFilters {
  const severity = enumValueOrUndefined(input.get("severity"), severities);
  const fixMode = enumValueOrUndefined(input.get("fixMode"), fixModes);
  const disposition = enumValueOrUndefined(input.get("disposition"), findingDispositions);
  const pageSize = boundedInteger(input.get("pageSize"), 1, 100, 25);
  const requestedPage = boundedInteger(input.get("page"), 1, 10_000, 1);
  const page = (requestedPage - 1) * pageSize < 10_000 ? requestedPage : 1;
  return {
    ...(severity && { severity }), ...(fixMode && { fixMode }), ...(disposition && { disposition }),
    ...(input.get("automaticallyResolved") === "true" && { automaticallyResolved: true }),
    ...optionalBoundedFilter("domain", input.get("domain"), 128),
    ...optionalBoundedFilter("ruleCode", input.get("ruleCode"), 128),
    ...optionalBoundedFilter("validationKey", input.get("validationKey"), 256),
    page,
    pageSize,
  };
}

function enumValueOrUndefined<const T extends readonly string[]>(value: string | null, allowed: T): T[number] | undefined {
  if (!value) return undefined;
  return allowed.find(candidate => candidate.toLowerCase() === value.trim().toLowerCase());
}

function optionalBoundedFilter(key: "domain" | "ruleCode" | "validationKey", value: string | null, max: number): Partial<FindingFilters> {
  const normalized = value?.trim();
  return normalized && normalized.length <= max ? { [key]: normalized } : {};
}

function boundedInteger(value: string | null, minimum: number, maximum: number, fallback: number): number {
  if (!value || !/^\d+$/.test(value)) return fallback;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= minimum && parsed <= maximum ? parsed : fallback;
}

export function findingsQuery(filters: FindingFilters): string {
  const query = new URLSearchParams();
  if (filters.severity) query.set("severity", filters.severity);
  if (filters.fixMode) query.set("fixMode", filters.fixMode);
  if (filters.disposition) query.set("disposition", filters.disposition);
  if (filters.automaticallyResolved !== undefined) query.set("automaticallyResolved", String(filters.automaticallyResolved));
  if (filters.domain) query.set("domain", filters.domain);
  if (filters.ruleCode) query.set("ruleCode", filters.ruleCode);
  if (filters.validationKey) query.set("validationKey", filters.validationKey);
  query.set("page", String(filters.page));
  query.set("pageSize", String(filters.pageSize));
  return query.toString();
}

export function auditSummaryPath(auditId: string): string {
  return `/api/audits/${encodeURIComponent(auditId)}`;
}

export function auditFindingsPath(auditId: string, filters: FindingFilters): string {
  return `${auditSummaryPath(auditId)}/findings?${findingsQuery(filters)}`;
}

export function auditFindingDetailPath(auditId: string, findingId: string): string {
  return `${auditSummaryPath(auditId)}/findings/${encodeURIComponent(findingId)}`;
}

export function structuralFindingExcerptPath(auditId: string, findingId: string): string {
  return `${auditFindingDetailPath(auditId, findingId)}/excerpt`;
}
