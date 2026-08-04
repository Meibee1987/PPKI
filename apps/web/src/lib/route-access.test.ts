import assert from "node:assert/strict";
import test from "node:test";
import { isProtectedPath } from "./route-access.ts";

test("audit detail route is protected", () => assert.equal(isProtectedPath("/audits/11111111-1111-4111-8111-111111111111"), true));
test("nested finding route is protected", () => assert.equal(isProtectedPath("/audits/11111111-1111-4111-8111-111111111111/findings/22222222-2222-4222-8222-222222222222"), true));
test("auth callback is not treated as protected", () => assert.equal(isProtectedPath("/auth/callback"), false));
test("similar audit prefix is not treated as an audit segment", () => assert.equal(isProtectedPath("/audits-other"), false));
test("documents use the same exact segment matching", () => {
  assert.equal(isProtectedPath("/documents"), true);
  assert.equal(isProtectedPath("/documents/123"), true);
  assert.equal(isProtectedPath("/documents-other"), false);
});
