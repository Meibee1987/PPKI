export function StatusBadge({ status }: { status: string }) {
  const normalized = status.toLowerCase().replaceAll(" ", "-");
  return <span className={`statusBadge status-${normalized}`}>{status}</span>;
}
