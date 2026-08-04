import type { JsonValue } from "../lib/audit-contract";
import { presentLocation } from "../lib/findings-presentation";

export function FindingLocation({ value }: { value: JsonValue }) {
  const location = presentLocation(value);
  return <span className="location" aria-label={location.accessibleLabel} title={location.details.join(" · ") || location.primary}><span>{location.primary}</span>{location.compact && <small>{location.compact}</small>}</span>;
}
