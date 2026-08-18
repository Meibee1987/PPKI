export function textCorrectionsPath(auditId: string, page: number, pageSize: number): string {
  return `/api/audits/${encodeURIComponent(auditId)}/text-corrections?page=${page}&pageSize=${pageSize}`;
}

export function textCorrectionBatchPath(auditId: string): string {
  return `/api/audits/${encodeURIComponent(auditId)}/text-correction-batches`;
}
