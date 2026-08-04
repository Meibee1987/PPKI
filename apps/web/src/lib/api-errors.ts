export function safeProblemCode(value: unknown): string | undefined {
  if (!value || typeof value !== "object" || !("code" in value)) return undefined;
  const candidate = (value as { code?: unknown }).code;
  return typeof candidate === "string" && /^[a-z0-9-]{1,80}$/i.test(candidate) ? candidate : undefined;
}
