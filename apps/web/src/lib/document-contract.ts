import { auditStatuses, type AuditStatus } from "./audit-contract.ts";

export type DocumentAudit = {
  id: string;
  status: AuditStatus;
  score: number | null;
  errorCount: number;
  warningCount: number;
  infoCount: number;
  createdAt: string;
};

export type DocumentVersion = {
  id: string;
  versionNo: number;
  parentVersionId: string | null;
  originalFilename: string;
  sizeBytes: number;
  sha256: string;
  createdAt: string;
  audits: DocumentAudit[];
};

export type DocumentDetail = {
  id: string;
  title: string;
  documentType: string;
  currentVersionNo: number;
  createdAt: string;
  updatedAt: string;
  versions: DocumentVersion[];
};

export type DocumentListAudit = Omit<DocumentAudit, "createdAt">;

export type DocumentListItem = {
  id: string;
  title: string;
  documentType: string;
  currentVersionNo: number;
  updatedAt: string;
  latestAudit: DocumentListAudit | null;
};

export type DocumentCreated = {
  id: string;
  versionId: string;
  title: string;
  currentVersionNo: number;
  sha256: string;
};

export type AuditAccepted = { id: string; status: AuditStatus };

export function selectLatestAudit(versions: readonly DocumentVersion[]): DocumentAudit | undefined {
  let latest: DocumentAudit | undefined;
  let latestTimestamp = Number.NEGATIVE_INFINITY;

  for (const audit of versions.flatMap(version => version.audits)) {
    const timestamp = Date.parse(audit.createdAt);
    if (Number.isNaN(timestamp)) continue;
    if (timestamp > latestTimestamp || (timestamp === latestTimestamp && latest && audit.id > latest.id)) {
      latest = audit;
      latestTimestamp = timestamp;
    }
  }

  return latest;
}

type UnknownRecord = Record<string, unknown>;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export class DocumentContractError extends Error {
  constructor() {
    super("Respons layanan tidak sesuai kontrak dokumen.");
    this.name = "DocumentContractError";
  }
}

function record(value: unknown): UnknownRecord {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new DocumentContractError();
  return value as UnknownRecord;
}

function string(value: unknown): string {
  if (typeof value !== "string") throw new DocumentContractError();
  return value;
}

function uuid(value: unknown): string {
  const parsed = string(value);
  if (!uuidPattern.test(parsed)) throw new DocumentContractError();
  return parsed;
}

function integer(value: unknown, minimum = 0): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < minimum) throw new DocumentContractError();
  return value;
}

function nullableNumber(value: unknown): number | null {
  if (value === null) return null;
  if (typeof value !== "number" || !Number.isFinite(value)) throw new DocumentContractError();
  return value;
}

function dateTime(value: unknown): string {
  const parsed = string(value);
  if (Number.isNaN(Date.parse(parsed))) throw new DocumentContractError();
  return parsed;
}

function status(value: unknown): AuditStatus {
  if (typeof value !== "string" || !auditStatuses.includes(value as AuditStatus)) throw new DocumentContractError();
  return value as AuditStatus;
}

function documentAudit(value: unknown): DocumentAudit {
  const data = record(value);
  return {
    id: uuid(data.id), status: status(data.status), score: nullableNumber(data.score),
    errorCount: integer(data.errorCount), warningCount: integer(data.warningCount), infoCount: integer(data.infoCount),
    createdAt: dateTime(data.createdAt),
  };
}

function listAudit(value: unknown): DocumentListAudit {
  const data = record(value);
  return {
    id: uuid(data.id), status: status(data.status), score: nullableNumber(data.score),
    errorCount: integer(data.errorCount), warningCount: integer(data.warningCount), infoCount: integer(data.infoCount),
  };
}

export function parseDocumentList(value: unknown): DocumentListItem[] {
  if (!Array.isArray(value)) throw new DocumentContractError();
  return value.map(item => {
    const data = record(item);
    return {
      id: uuid(data.id), title: string(data.title), documentType: string(data.documentType),
      currentVersionNo: integer(data.currentVersionNo, 1), updatedAt: dateTime(data.updatedAt),
      latestAudit: data.latestAudit === null ? null : listAudit(data.latestAudit),
    };
  });
}

export function parseDocumentDetail(value: unknown): DocumentDetail {
  const data = record(value);
  if (!Array.isArray(data.versions)) throw new DocumentContractError();
  return {
    id: uuid(data.id), title: string(data.title), documentType: string(data.documentType),
    currentVersionNo: integer(data.currentVersionNo, 1), createdAt: dateTime(data.createdAt), updatedAt: dateTime(data.updatedAt),
    versions: data.versions.map(item => {
      const version = record(item);
      if (!Array.isArray(version.audits)) throw new DocumentContractError();
      return {
        id: uuid(version.id), versionNo: integer(version.versionNo, 1),
        parentVersionId: version.parentVersionId === null ? null : uuid(version.parentVersionId),
        originalFilename: string(version.originalFilename), sizeBytes: integer(version.sizeBytes, 1),
        sha256: string(version.sha256), createdAt: dateTime(version.createdAt),
        audits: version.audits.map(documentAudit),
      };
    }),
  };
}

export function parseDocumentCreated(value: unknown): DocumentCreated {
  const data = record(value);
  return { id: uuid(data.id), versionId: uuid(data.versionId), title: string(data.title),
    currentVersionNo: integer(data.currentVersionNo, 1), sha256: string(data.sha256) };
}

export function parseAuditAccepted(value: unknown): AuditAccepted {
  const data = record(value);
  return { id: uuid(data.id), status: status(data.status) };
}
