type Finding = {
  ruleCode: string; element: string; domain: string; severity: string; fixMode: string;
  message: string; actual: unknown; expected: unknown; location: unknown;
  source: { sourceSection?: string; pdfPage?: number };
};

export function FindingCard({ finding }: { finding: Finding }) {
  return (
    <article className="finding-card">
      <div className="finding-title"><div><span className={`severity ${finding.severity.toLowerCase()}`}>{finding.severity}</span><strong>{finding.ruleCode} — {finding.element}</strong></div><span>{finding.fixMode}</span></div>
      <p>{finding.message}</p>
      <div className="comparison"><div><small>Aktual</small><pre>{JSON.stringify(finding.actual, null, 2)}</pre></div><div><small>Seharusnya</small><pre>{JSON.stringify(finding.expected, null, 2)}</pre></div></div>
      <small>Lokasi: {JSON.stringify(finding.location)} · Sumber: {finding.source.sourceSection ?? "PPKI"}{finding.source.pdfPage ? `, PDF hlm. ${finding.source.pdfPage}` : ""}</small>
    </article>
  );
}
