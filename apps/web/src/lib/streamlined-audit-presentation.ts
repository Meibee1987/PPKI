import type { AutomaticRemediationState, PageLocationConfidence } from "./audit-contract";
import type { CorrectionAction, CorrectionAnchorState, CorrectionBatchStatus, CorrectionPageLocation, DocumentPreviewState, TextCorrectionContext } from "./text-correction-contract";

export type ProgressTone = "done" | "active" | "waiting" | "failed";
export type ProgressStep = { label: string; tone: ProgressTone; status: string };

export function automaticProgress(state: AutomaticRemediationState): ProgressStep[] {
  const failed = state === "Failed" || state === "Conflict";
  const auditDone = !["Pending"].includes(state);
  const formatDone = ["ReauditPending", "Completed", "NoAction"].includes(state);
  const reauditDone = ["Completed", "NoAction"].includes(state);
  return [
    step("Memeriksa dokumen", auditDone ? "done" : "active"),
    step("Memperbaiki format otomatis", failed ? "failed" : formatDone ? "done" : auditDone ? "active" : "waiting"),
    step("Memeriksa ulang hasil", failed ? "waiting" : reauditDone ? "done" : state === "ReauditPending" ? "active" : "waiting"),
  ];
}
export function batchProgress(batch: CorrectionBatchStatus, preview?: DocumentPreviewState): ProgressStep[] {
  const failed = batch.state === "Failed" || batch.state === "Conflict";
  const hasVersion = batch.resultDocumentVersionId !== null;
  const hasReaudit = batch.reauditId !== null;
  return [
    step("Menerapkan perbaikan", failed ? "failed" : hasVersion ? "done" : "active"),
    step("Membuat versi baru", failed ? "waiting" : hasVersion ? "done" : "waiting"),
    step("Membuat preview", failed ? "waiting" : preview?.state === "Completed" ? "done" : preview?.state === "Failed" ? "failed" : hasVersion ? "active" : "waiting"),
    step("Memeriksa ulang dokumen", failed ? "waiting" : hasReaudit ? "done" : hasVersion ? "active" : "waiting"),
    step("Memverifikasi hasil", failed ? "waiting" : batch.state === "Completed" ? "done" : batch.state === "VerificationPending" ? "active" : "waiting"),
  ];
}
function step(label: string, tone: ProgressTone): ProgressStep { return { label, tone, status: tone === "done" ? "Selesai" : tone === "active" ? "Sedang diproses" : tone === "failed" ? "Gagal" : "Menunggu" }; }

export function pageLocationLabel(value: CorrectionPageLocation, pending = false): string {
  if (value?.pageNumber !== null && value?.confidence === "Exact") return `Halaman ${value.pageNumber}`;
  if (value?.pageNumber !== null && value?.confidence === "Estimated") return `Perkiraan halaman ${value.pageNumber}`;
  return pending ? "Menentukan halaman..." : "Lokasi halaman belum tersedia";
}
export function previewFragment(value: CorrectionPageLocation): string { return value?.confidence === "Exact" && value.pageNumber !== null ? `#page=${value.pageNumber}` : ""; }
export function decisionLabel(action: CorrectionAction | null): string { return action === "UseSuggestion" ? "Gunakan saran" : action === "EditManual" ? "Edit manual" : action === "Ignore" ? "Diabaikan" : "Belum dipilih"; }
export function contextStateCopy(status: CorrectionAnchorState | "Unavailable"): string { return status === "Stale" ? "Sumber temuan sudah berubah. Muat ulang hasil audit." : status === "Unsupported" ? "Temuan ini tidak dapat diperbaiki otomatis. Periksa dokumen secara manual." : status === "Unavailable" ? "Konteks belum tersedia." : ""; }
export function scalarCount(value: string): number { return Array.from(value).length; }
export function validateManualReplacement(value: string): string | null {
  const count = scalarCount(value);
  if (!value.trim()) return "Perbaikan tidak boleh kosong.";
  if (/\r|\n/.test(value)) return "Perbaikan tidak boleh mengandung baris baru.";
  if (/\t/.test(value)) return "Perbaikan tidak boleh mengandung tab.";
  if (Array.from(value).some(character => { const code = character.codePointAt(0) ?? 0; return code < 0x20 || code === 0x7f; })) return "Perbaikan mengandung karakter kontrol yang tidak didukung.";
  if (count > 256) return "Perbaikan maksimal 256 karakter Unicode.";
  return null;
}
export function highlightedContext(value: TextCorrectionContext): { before: string; target: string; after: string } | null {
  if (value.anchorStatus !== "Exact" || value.context === null || value.targetText === null || value.targetOffsetInContext === null) return null;
  const context = Array.from(value.context), target = Array.from(value.targetText), offset = value.targetOffsetInContext;
  if (offset < 0 || offset + target.length > context.length) return null;
  return { before: context.slice(0, offset).join(""), target: context.slice(offset, offset + target.length).join(""), after: context.slice(offset + target.length).join("") };
}
export function safeCommandMessage(status: number): string { return status === 403 ? "Akses ditolak." : status === 409 ? "Dokumen atau pilihan perbaikan telah berubah. Muat ulang hasil audit." : status === 404 ? "Data tidak ditemukan atau tidak dapat diakses." : status >= 500 ? "Layanan sedang mengalami gangguan. Coba lagi." : "Permintaan tidak dapat diproses."; }
export function isTerminalBatch(state: CorrectionBatchStatus["state"]): boolean { return state === "Completed" || state === "Failed" || state === "Conflict"; }
export function pageConfidence(value: PageLocationConfidence): PageLocationConfidence { return value; }
