import type { AuditSummary, ReviewReadinessReason, ReviewReadinessState } from "./audit-contract.ts";

export type ReadinessPresentation = {
  title: string;
  message: string;
  tone: "progress" | "needs-fix" | "ready" | "unknown";
};

const unknownMessages: Record<ReviewReadinessReason, string> = {
  AuditFailed: "Kesiapan review belum dapat ditentukan karena audit tidak berhasil diselesaikan.",
  AuditCancelled: "Kesiapan review belum dapat ditentukan karena audit dibatalkan.",
  PolicyUnknown: "Kesiapan review belum dapat ditentukan karena kebijakan kesiapan audit ini tidak lengkap.",
  NoApplicableRules: "Kesiapan review belum dapat ditentukan karena tidak ada aturan yang berlaku untuk audit ini.",
};

export function readinessPresentation(summary: Pick<AuditSummary,
  "readinessState" | "readinessReason" | "blockingFindingCount">): ReadinessPresentation {
  if (summary.readinessState === "AuditInProgress") return {
    title: "Audit sedang berlangsung",
    message: "Status kesiapan review akan diperbarui setelah pemeriksaan selesai.",
    tone: "progress",
  };
  if (summary.readinessState === "NeedsFix") return {
    title: "Belum siap untuk direview",
    message: `${summary.blockingFindingCount} temuan penghambat review masih memerlukan perbaikan terverifikasi.`,
    tone: "needs-fix",
  };
  if (summary.readinessState === "ReadyForReview") return {
    title: "Siap untuk direview",
    message: "Tidak ada temuan penghambat review yang masih efektif pada audit ini.",
    tone: "ready",
  };
  return {
    title: "Kesiapan review belum diketahui",
    message: unknownMessages[summary.readinessReason ?? "PolicyUnknown"],
    tone: "unknown",
  };
}

export function abbreviatedRuleSetHash(hash: string | null): string {
  return hash === null ? "Belum tersedia" : hash.slice(0, 12);
}

export function scoreLabel(score: number | null): string {
  return score === null ? "Belum tersedia" : String(score);
}

export function readinessStateLabel(state: ReviewReadinessState): string {
  return state === "AuditInProgress" ? "Sedang diaudit"
    : state === "NeedsFix" ? "Perlu diperbaiki"
    : state === "ReadyForReview" ? "Siap direview"
    : "Belum diketahui";
}
