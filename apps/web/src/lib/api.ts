import { createClient } from "./supabase/client.ts";
import { getPublicSupabaseEnvironment } from "./supabase/environment.ts";
import { parseSafeProblemDetails, type SafeProblemDetails } from "./api-errors.ts";

const maximumErrorBodyLength = 64 * 1024;

export class ApiRequestError extends Error {
  readonly status: number;
  readonly problem: SafeProblemDetails;

  constructor(
    status: number,
    problem: SafeProblemDetails = { status },
  ) {
    super(status === 404
      ? "Data tidak ditemukan atau tidak dapat diakses."
      : status === 401 || status === 403
        ? "Sesi tidak dapat mengakses data ini."
        : status >= 500
          ? "Layanan sedang mengalami gangguan. Coba lagi nanti."
          : "Permintaan tidak dapat diproses.");
    this.name = "ApiRequestError";
    this.status = status;
    this.problem = problem;
  }

  get code(): string | undefined { return this.problem.code; }
}

export class ApiNetworkError extends Error {
  constructor() {
    super("Layanan tidak dapat dihubungi. Periksa koneksi lalu coba lagi.");
    this.name = "ApiNetworkError";
  }
}

export class ApiResponseError extends Error {
  readonly status: number;

  constructor(status: number) {
    super("Respons layanan tidak dapat dibaca.");
    this.name = "ApiResponseError";
    this.status = status;
  }
}

export class ApiRequestAbortedError extends Error {
  constructor() {
    super("Request cancelled.");
    this.name = "AbortError";
  }
}

export function isApiRequestAborted(value: unknown): boolean {
  return value instanceof ApiRequestAbortedError
    || typeof DOMException !== "undefined" && value instanceof DOMException && value.name === "AbortError"
    || Boolean(value && typeof value === "object" && "name" in value && value.name === "AbortError");
}

type ApiClientDependencies = {
  apiBaseUrl: string;
  getAccessToken: () => Promise<string | null>;
  fetch: typeof fetch;
  onUnauthorized: () => void;
};

export function createApiClient(dependencies: ApiClientDependencies) {
  async function request(path: string, init: RequestInit): Promise<Response> {
    if (init.signal?.aborted) throw new ApiRequestAbortedError();
    let token: string | null;
    try { token = await dependencies.getAccessToken(); }
    catch (error) {
      if (init.signal?.aborted || isApiRequestAborted(error)) throw new ApiRequestAbortedError();
      throw new ApiNetworkError();
    }
    if (!token) {
      dependencies.onUnauthorized();
      throw new ApiRequestError(401);
    }

    const headers = new Headers(init.headers);
    headers.set("Authorization", `Bearer ${token}`);
    if (!(init.body instanceof FormData) && init.body && !headers.has("Content-Type"))
      headers.set("Content-Type", "application/json");

    let response: Response;
    try {
      response = await dependencies.fetch(`${dependencies.apiBaseUrl}${path}`, {
        ...init,
        headers,
        cache: "no-store",
      });
    } catch (error) {
      if (init.signal?.aborted || isApiRequestAborted(error)) throw new ApiRequestAbortedError();
      throw new ApiNetworkError();
    }

    if (!response.ok) {
      const problem = await readProblemDetails(response);
      if (response.status === 401) dependencies.onUnauthorized();
      throw new ApiRequestError(response.status, problem);
    }
    return response;
  }

  return {
    async fetchJson<T>(path: string, init: RequestInit = {}): Promise<T> {
      const response = await request(path, init);
      if (response.status === 204) return undefined as T;
      const body = await response.text();
      if (!body) throw new ApiResponseError(response.status);
      try { return JSON.parse(body) as T; }
      catch { throw new ApiResponseError(response.status); }
    },
    async fetchBlob(path: string, init: RequestInit = {}): Promise<Blob> {
      return (await request(path, init)).blob();
    },
  };
}

async function readProblemDetails(response: Response): Promise<SafeProblemDetails> {
  const contentType = response.headers.get("content-type")?.split(";", 1)[0].trim().toLowerCase();
  if (contentType !== "application/json" && contentType !== "application/problem+json")
    return { status: response.status };
  try {
    const body = await response.text();
    if (!body || body.length > maximumErrorBodyLength) return { status: response.status };
    return parseSafeProblemDetails(JSON.parse(body), response.status);
  } catch {
    return { status: response.status };
  }
}

function redirectToLogin(): void {
  if (typeof window === "undefined") return;
  const next = `${window.location.pathname}${window.location.search}`;
  window.location.assign(`/login?next=${encodeURIComponent(next)}`);
}

function defaultClient() {
  const { apiBaseUrl } = getPublicSupabaseEnvironment();
  return createApiClient({
    apiBaseUrl,
    getAccessToken: async () => {
      const { data: { session } } = await createClient().auth.getSession();
      return session?.access_token ?? null;
    },
    fetch,
    onUnauthorized: redirectToLogin,
  });
}

export function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  return defaultClient().fetchJson<T>(path, init);
}

export async function apiFetchBlob(path: string, init: RequestInit = {}): Promise<Blob> {
  const blob = await defaultClient().fetchBlob(path, init);
  if (blob.type && blob.type !== "application/pdf") throw new ApiRequestError(502);
  return blob;
}
