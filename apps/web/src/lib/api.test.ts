import assert from "node:assert/strict";
import test from "node:test";
import { ApiRequestError, ApiResponseError, createApiClient, isApiRequestAborted } from "./api.ts";

function client(response: Response | ((input: RequestInfo | URL, init?: RequestInit) => Promise<Response>), unauthorized = () => {}) {
  const fetchImplementation = typeof response === "function" ? response : async () => response;
  return createApiClient({ apiBaseUrl: "https://api.example.test", getAccessToken: async () => "session-token",
    fetch: fetchImplementation as typeof fetch, onUnauthorized: unauthorized });
}

test("returns a typed JSON response with bearer auth and the caller AbortSignal", async () => {
  type Payload = { id: string; status: "Completed" };
  const controller = new AbortController();
  let observed: RequestInit | undefined;
  const api = client(async (_input, init) => { observed = init; return Response.json({ id: "audit-1", status: "Completed" }); });
  const result = await api.fetchJson<Payload>("/api/audits/audit-1", { signal: controller.signal });
  assert.deepEqual(result, { id: "audit-1", status: "Completed" });
  assert.equal(new Headers(observed?.headers).get("Authorization"), "Bearer session-token");
  assert.equal(observed?.signal, controller.signal);
});

test("parses ProblemDetails into a safe status and bounded code", async () => {
  const api = client(new Response(JSON.stringify({ title: "Do not expose", detail: "secret thesis text",
    code: "finding-pagination-invalid", exception: "stack" }), { status: 400, headers: { "Content-Type": "application/problem+json" } }));
  await assert.rejects(api.fetchJson("/api/audits/a/findings"), error => {
    assert.ok(error instanceof ApiRequestError); assert.equal(error.status, 400); assert.equal(error.code, "finding-pagination-invalid");
    assert.deepEqual(error.problem, { status: 400, code: "finding-pagination-invalid" });
    assert.doesNotMatch(error.message, /secret|thesis|stack|Do not expose/i); return true;
  });
});

test("401 invokes the established unauthorized flow and throws a safe typed error", async () => {
  let redirects = 0;
  const api = client(new Response(null, { status: 401 }), () => { redirects += 1; });
  await assert.rejects(api.fetchJson("/api/documents"), error => error instanceof ApiRequestError && error.status === 401);
  assert.equal(redirects, 1);
});

test("missing session uses the same unauthorized flow without making a request", async () => {
  let redirects = 0, requests = 0;
  const api = createApiClient({ apiBaseUrl: "https://api.example.test", getAccessToken: async () => null,
    fetch: (async () => { requests += 1; return Response.json({}); }) as typeof fetch, onUnauthorized: () => { redirects += 1; } });
  await assert.rejects(api.fetchJson("/api/documents"), error => error instanceof ApiRequestError && error.status === 401);
  assert.equal(redirects, 1); assert.equal(requests, 0);
});

test("AbortSignal cancellation remains distinguishable and is not converted to a user-facing failure", async () => {
  const controller = new AbortController(); controller.abort();
  const api = client(Response.json({}));
  await assert.rejects(api.fetchJson("/api/documents", { signal: controller.signal }), error => {
    assert.equal(isApiRequestAborted(error), true); assert.equal((error as Error).name, "AbortError"); return true;
  });
});

test("malformed JSON success is a typed response error with its HTTP status", async () => {
  const api = client(new Response("not-json", { status: 200, headers: { "Content-Type": "application/json" } }));
  await assert.rejects(api.fetchJson("/api/documents"), error => error instanceof ApiResponseError && error.status === 200);
});

test("malformed non-JSON errors retain status without surfacing their body", async () => {
  const api = client(new Response("database connection string and raw exception", { status: 502, headers: { "Content-Type": "text/plain" } }));
  await assert.rejects(api.fetchJson("/api/documents"), error => {
    assert.ok(error instanceof ApiRequestError); assert.equal(error.status, 502); assert.deepEqual(error.problem, { status: 502 });
    assert.doesNotMatch(error.message, /database|connection|string|exception/i); return true;
  });
});
