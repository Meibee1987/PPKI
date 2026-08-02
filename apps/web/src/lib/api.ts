import { createClient } from "./supabase/client";
import { getPublicSupabaseEnvironment } from "./supabase/environment";

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
    const payload = await response.text();
    throw new Error(payload || `API error ${response.status}`);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}
