import type { AuditSource, FixMode, Severity } from "./audit-contract.ts";

export type SourceReferenceAvailability = "Unavailable" | "Partial" | "MetadataOnly";

export type SourceReferencePresentation = {
  availability: SourceReferenceAvailability;
  sourceSection: string | null;
  pdfPage: number | null;
  printedPage: string | null;
  navigationTarget: null;
};

const unsafeReference = /(?:^[a-z]:[\\/]|^\\\\|^\/(?:home|users|var|tmp|etc)(?:\/|$)|https?:\/\/|storage\/v1|(?:signed|access)[_-]?token|[?&](?:token|signature)=)/i;

function safeReferenceText(value: string | null): string | null {
  if (value === null) return null;
  const trimmed = value.trim();
  return !trimmed || trimmed.length > 240 || unsafeReference.test(trimmed) ? null : trimmed;
}

export function sourceReferencePresentation(source: AuditSource): SourceReferencePresentation {
  const sourceSection = safeReferenceText(source.sourceSection);
  const printedPage = safeReferenceText(source.printedPage);
  const pdfPage = source.pdfPage;
  const availableCount = Number(sourceSection !== null) + Number(pdfPage !== null) + Number(printedPage !== null);
  return {
    availability: availableCount === 0 ? "Unavailable" : availableCount === 3 ? "MetadataOnly" : "Partial",
    sourceSection,
    pdfPage,
    printedPage,
    // The immutable finding-source contract currently has no authoritative asset or URL.
    navigationTarget: null,
  };
}

export const severityGlossary: Record<Severity, string> = {
  Error: "Temuan dengan tingkat keparahan Error pada snapshot aturan.",
  Warning: "Temuan dengan tingkat keparahan Warning pada snapshot aturan.",
  Info: "Temuan informatif pada snapshot aturan.",
};

export const fixModeGlossary: Record<FixMode, string> = {
  Auto: "Aturan diklasifikasikan untuk mode perbaikan Auto; ketersediaan tindakan tetap ditentukan backend.",
  Confirm: "Perbaikan memerlukan konfirmasi pengguna sebelum diterapkan.",
  Manual: "Perbaikan memerlukan tindakan manual.",
  Report: "Temuan dilaporkan tanpa tindakan perbaikan otomatis.",
};
