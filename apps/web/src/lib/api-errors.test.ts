import assert from "node:assert/strict";
import test from "node:test";
import { parseSafeProblemDetails, safeProblemCode } from "./api-errors.ts";

test("parses only a bounded safe problem-details code", () => { assert.equal(safeProblemCode({ code: "finding-pagination-invalid", detail: "internal detail" }), "finding-pagination-invalid"); assert.equal(safeProblemCode({ code: "bad code with spaces" }), undefined); assert.equal(safeProblemCode({ code: "x".repeat(81) }), undefined); });
test("does not surface problem title, detail, or exception fields", () => assert.equal(safeProblemCode({ title: "unsafe", detail: "secret", exception: "stack" }), undefined));
test("retains only HTTP status and a safe code from ProblemDetails", () => {
  const parsed = parseSafeProblemDetails({ type: "about:blank", title: "unsafe title", status: 500,
    detail: "Server=secret; stack trace and thesis contents", code: "audit-failed", exception: "raw stack" }, 409);
  assert.deepEqual(parsed, { status: 409, code: "audit-failed" });
  assert.equal("title" in parsed, false); assert.equal("detail" in parsed, false); assert.equal("exception" in parsed, false);
});
