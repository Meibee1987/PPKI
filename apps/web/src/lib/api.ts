import { createClient } from "./supabase/client";
import { getPublicSupabaseEnvironment } from "./supabase/environment";
import { safeProblemCode } from "./api-errors";

export class ApiRequestError extends Error {
  constructor(public readonly status: number, public readonly code?: string) {
    super(status === 404
      ? "Data tidak ditemukan atau tidak dapat diakses."
      : status === 401 || status === 403
        ? "Sesi tidak dapat mengakses data ini."
        : status >= 500
          ? "Layanan sedang mengalami gangguan. Coba lagi nanti."
          : "Permintaan tidak dapat diproses.");
    this.name = "ApiRequestError";
  }
}

export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const { apiBaseUrl } = getPublicSupabaseEnvironment();
  const supabase = createClient();
  const { data: { session } } = await supabase.auth.getSession();
  if (!session?.access_token) throw new Error("Sesi login tidak tersedia.");

  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${session.access_token}`);
  if (!(init.body instanceof FormData) && init.body && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...init,
    headers,
    cache: "no-store",
  });
  if (!response.ok) {
    let code: string | undefined;
    try {
      const payload: unknown = await response.json();
      code = safeProblemCode(payload);
    } catch { /* Malformed error bodies are deliberately not surfaced. */ }
    if (response.status === 401 && typeof window !== "undefined") {
      const next = `${window.location.pathname}${window.location.search}`;
      window.location.assign(`/login?next=${encodeURIComponent(next)}`);
    }
    throw new ApiRequestError(response.status, code);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export async function apiFetchBlob(path: string, init: RequestInit = {}): Promise<Blob> {
  const { apiBaseUrl } = getPublicSupabaseEnvironment();
  const supabase = createClient();
  const { data: { session } } = await supabase.auth.getSession();
  if (!session?.access_token) throw new Error("Sesi login tidak tersedia.");
  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${session.access_token}`);
  const response = await fetch(`${apiBaseUrl}${path}`, { ...init, headers, cache: "no-store" });
  if (!response.ok) throw new ApiRequestError(response.status);
  if (response.headers.get("content-type")?.split(";", 1)[0] !== "application/pdf")
    throw new ApiRequestError(502);
  return response.blob();
}
