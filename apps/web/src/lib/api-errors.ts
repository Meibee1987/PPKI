export type ProblemDetails = {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code?: string;
};

export type SafeProblemDetails = Readonly<{
  status: number;
  code?: string;
}>;

const safeCodePattern = /^[a-z0-9-]{1,80}$/i;

export function parseSafeProblemDetails(value: unknown, responseStatus: number): SafeProblemDetails {
  if (!value || typeof value !== "object" || Array.isArray(value)) return { status: responseStatus };
  const payload = value as Partial<Record<keyof ProblemDetails, unknown>>;
  const code = typeof payload.code === "string" && safeCodePattern.test(payload.code)
    ? payload.code
    : undefined;

  // API title/detail and arbitrary extensions are intentionally not retained. They may
  // contain exception internals or document content and are not a UI trust boundary.
  return code ? { status: responseStatus, code } : { status: responseStatus };
}

export function safeProblemCode(value: unknown): string | undefined {
  return parseSafeProblemDetails(value, 0).code;
}
