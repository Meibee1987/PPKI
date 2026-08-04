import assert from "node:assert/strict";
import test from "node:test";
import { safeProblemCode } from "./api-errors.ts";

test("parses only a bounded safe problem-details code", () => { assert.equal(safeProblemCode({ code: "finding-pagination-invalid", detail: "internal detail" }), "finding-pagination-invalid"); assert.equal(safeProblemCode({ code: "bad code with spaces" }), undefined); assert.equal(safeProblemCode({ code: "x".repeat(81) }), undefined); });
test("does not surface problem title, detail, or exception fields", () => assert.equal(safeProblemCode({ title: "unsafe", detail: "secret", exception: "stack" }), undefined));
