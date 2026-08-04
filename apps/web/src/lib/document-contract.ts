export type DocumentAudit = {
  id: string;
  status: string;
  score: number | null;
  errorCount: number;
  warningCount: number;
  infoCount: number;
  createdAt: string;
};

export type DocumentVersion = {
  id: string;
  versionNo: number;
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
  versions: DocumentVersion[];
};

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
