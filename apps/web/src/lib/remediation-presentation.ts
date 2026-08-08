import type { AuditComparisonStatus, FindingResolutionState, FindingReviewState, FixExecutionState, FixFailureCategory } from "./remediation-contract";

const failureMessages: Record<string, string> = {
  "fix-source-version-superseded": "Dokumen memiliki versi yang lebih baru. Muat ulang audit sebelum menerapkan perbaikan.",
  "fix-plan-stale": "Rencana perbaikan sudah tidak berlaku. Buat pratinjau baru.",
  "source-storage-object-missing": "Berkas sumber tidak tersedia.",
  "source-hash-mismatch": "Integritas berkas sumber tidak dapat diverifikasi.",
  "source-package-invalid": "Berkas sumber bukan paket DOCX yang valid.",
  "approved-plan-invalid": "Rencana perbaikan tidak valid.",
  "fix-provider-version-unavailable": "Versi mesin perbaikan yang digunakan rencana ini tidak tersedia.",
  "storage-download-transient": "Pengambilan berkas mengalami gangguan sementara.",
  "storage-upload-transient": "Penyimpanan hasil mengalami gangguan sementara.",
  "database-transient": "Penyimpanan status mengalami gangguan sementara.",
  "worker-lease-lost": "Pemrosesan diambil alih secara aman dan akan diperbarui.",
  "fix-result-object-conflict": "Hasil penyimpanan bertentangan dengan hasil eksekusi sebelumnya.",
  "database-finalization-terminal": "Hasil tidak dapat difinalisasi.",
};
export function failureMessage(code: string | null): string { return code && failureMessages[code] ? failureMessages[code] : "Eksekusi gagal. Muat ulang status atau hubungi pengelola sistem."; }
export function isTerminalExecution(state: FixExecutionState): boolean { return state === "Completed" || state === "Failed" || state === "NoChange"; }
export function canCreateReaudit(state: FixExecutionState, resultVersionId: string | null): boolean { return state === "Completed" && resultVersionId !== null; }
export function resolutionPresentation(state: FindingResolutionState): { label: string; explanation: string; verified: boolean } {
  const values = { Open: ["Terbuka", "Belum ada bukti remediation.", false], Applied: ["Diterapkan", "Perbaikan diterapkan, tetapi belum diverifikasi oleh re-audit.", false], ReauditPending: ["Re-audit tertunda", "Verifikasi sedang menunggu hasil audit ulang.", false], VerifiedResolved: ["Terverifikasi selesai", "Re-audit memastikan temuan tidak lagi terdeteksi.", true], VerifiedStillDetected: ["Masih terdeteksi", "Re-audit masih mendeteksi temuan atau perubahannya.", true] } as const;
  const value = values[state]; return { label: value[0], explanation: value[1], verified: value[2] };
}
export function reviewPresentation(state: FindingReviewState): { label: string; explanation: string } {
  const labels: Record<FindingReviewState, string> = { NoReview: "Belum direview", PendingReview: "Menunggu keputusan", NeedsRevision: "Perlu revisi", ManualRemediationApproved: "Remediasi manual disetujui", ManualRemediationReported: "Remediasi manual dilaporkan", Rejected: "Ditolak", Ignored: "Diabaikan", AcceptedRisk: "Risiko diterima" };
  const nonVerified = ["Ignored", "AcceptedRisk", "ManualRemediationReported"].includes(state);
  return { label: labels[state], explanation: nonVerified ? "Keputusan review ini bukan bukti bahwa temuan telah terselesaikan." : "Status review terpisah dari verifikasi otomatis." };
}
export function comparisonPresentation(status: AuditComparisonStatus): string { return ({ StillDetected: "Masih terdeteksi", Changed: "Terdeteksi dengan perubahan", NoLongerDetected: "Sudah tidak terdeteksi", NewlyDetected: "Temuan baru" } as const)[status]; }
export function failureCategoryLabel(category: FixFailureCategory | null): string { return category ? ({ Conflict: "Konflik", InvalidInput: "Input tidak valid", InvalidSource: "Sumber tidak valid", InvalidPlan: "Rencana tidak valid", CapabilityUnavailable: "Mesin tidak tersedia", TransientInfrastructure: "Gangguan sementara", TerminalInfrastructure: "Gangguan terminal" } as const)[category] : "Tidak dikategorikan"; }

export function nextPollDelay(failures: number): number { return Math.min(15_000, 2_000 * (2 ** Math.min(Math.max(failures, 0), 3))); }
export function toggleSelection(current: readonly string[], findingId: string, eligible: boolean, maximum = 100): string[] {
  if (current.includes(findingId)) return current.filter(value => value !== findingId);
  if (!eligible || current.length >= maximum) return [...current];
  return [...current, findingId];
}
export function newIntentKey(): string { return crypto.randomUUID(); }
