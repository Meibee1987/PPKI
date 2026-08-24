import type { AuditSource, FixMode, Severity } from "../lib/audit-contract";
import { fixModeGlossary, severityGlossary, sourceReferencePresentation } from "../lib/source-reference-model";

export function SourceReference({ source, severity, fixMode }: {
  source: AuditSource;
  severity: Severity;
  fixMode: FixMode;
}) {
  const reference = sourceReferencePresentation(source);

  return <section className="source-reference" aria-labelledby="drawer-source-title">
    <h3 id="drawer-source-title">Referensi sumber</h3>
    {reference.availability === "Unavailable"
      ? <p>Referensi sumber tidak tersedia.</p>
      : <>
        <dl>
          {reference.sourceSection !== null && <div><dt>Bagian sumber</dt><dd>{reference.sourceSection}</dd></div>}
          {reference.pdfPage !== null && <div><dt>Halaman PDF</dt><dd>{reference.pdfPage}</dd></div>}
          {reference.printedPage !== null && <div><dt>Halaman cetak</dt><dd>{reference.printedPage}</dd></div>}
        </dl>
        <p className="muted">{reference.availability === "Partial" ? "Metadata referensi tersedia sebagian. " : "Metadata referensi tersedia. "}Dokumen sumber belum tersedia di aplikasi, sehingga metadata ini tidak ditautkan.</p>
      </>}
    <details className="source-glossary">
      <summary>Arti severity dan mode perbaikan</summary>
      <dl>
        <div><dt>Severity — {severity}</dt><dd>{severityGlossary[severity]}</dd></div>
        <div><dt>Mode perbaikan — {fixMode}</dt><dd>{fixModeGlossary[fixMode]}</dd></div>
      </dl>
    </details>
  </section>;
}
