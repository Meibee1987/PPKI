import { presentPayload, type DisplayRow } from "../lib/findings-presentation";
import type { JsonValue } from "../lib/audit-contract";

export function FindingPayload({ label, value }: { label: string; value: JsonValue }) {
  const rows = presentPayload(value);
  return (
    <section className="payload-panel" aria-label={label}>
      <h3>{label}</h3>
      {rows.length ? <PayloadRows rows={rows} /> : <p className="muted">Tidak ada data aman untuk ditampilkan.</p>}
    </section>
  );
}

function PayloadRows({ rows }: { rows: DisplayRow[] }) {
  return <dl className="payload-list">{rows.map((row, index) => <div key={`${row.label}-${index}`}><dt>{row.label}</dt><dd>{row.value}</dd></div>)}</dl>;
}
