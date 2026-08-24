import assert from "node:assert/strict";
import test from "node:test";
import { abbreviatedRuleSetHash, readinessPresentation, readinessStateLabel, scoreLabel } from "./audit-readiness-presentation.ts";
import type { AuditSummary, ReviewReadinessReason, ReviewReadinessState } from "./audit-contract.ts";

const readiness = (
  readinessState: ReviewReadinessState,
  readinessReason: ReviewReadinessReason | null = null,
  blockingFindingCount = 0,
): Pick<AuditSummary, "readinessState" | "readinessReason" | "blockingFindingCount"> =>
  ({ readinessState, readinessReason, blockingFindingCount });

test("NeedsFix renders the authoritative blocking count", () => {
  const value = readinessPresentation(readiness("NeedsFix", null, 7));
  assert.equal(value.title, "Belum siap untuk direview");
  assert.match(value.message, /^7 temuan penghambat review/);
});

test("ReadyForReview communicates review readiness without export claims", () => {
  const value = readinessPresentation(readiness("ReadyForReview"));
  assert.equal(value.title, "Siap untuk direview");
  assert.doesNotMatch(`${value.title} ${value.message}`, /ekspor|ReadyForExport/i);
});

test("AuditInProgress has an understandable waiting state", () => {
  const value = readinessPresentation(readiness("AuditInProgress"));
  assert.equal(value.tone, "progress");
  assert.match(value.message, /diperbarui setelah pemeriksaan selesai/);
});

for (const [reason, expected] of [
  ["AuditFailed", /audit tidak berhasil/],
  ["AuditCancelled", /audit dibatalkan/],
  ["PolicyUnknown", /kebijakan kesiapan audit ini tidak lengkap/],
  ["NoApplicableRules", /tidak ada aturan yang berlaku/],
] as const) {
  test(`Unknown/${reason} has safe reason-specific copy`, () => {
    const value = readinessPresentation(readiness("Unknown", reason));
    assert.equal(value.title, "Kesiapan review belum diketahui");
    assert.match(value.message, expected);
    assert.doesNotMatch(value.message, /exception|stack|diagnostic|storage|secret/i);
  });
}

test("score presentation displays a completed numeric score and never fabricates a missing one", () => {
  assert.equal(scoreLabel(92.5), "92.5");
  assert.equal(scoreLabel(null), "Belum tersedia");
});

test("rule-set hash abbreviation is deterministic and null-safe", () => {
  const hash = "abcdef0123456789".repeat(4);
  assert.equal(abbreviatedRuleSetHash(hash), "abcdef012345");
  assert.equal(abbreviatedRuleSetHash(hash), abbreviatedRuleSetHash(hash));
  assert.equal(abbreviatedRuleSetHash(null), "Belum tersedia");
});

test("readiness state labels remain review-only", () => {
  assert.equal(readinessStateLabel("NeedsFix"), "Perlu diperbaiki");
  assert.equal(readinessStateLabel("ReadyForReview"), "Siap direview");
  assert.doesNotMatch(["AuditInProgress", "NeedsFix", "ReadyForReview", "Unknown"]
    .map(value => readinessStateLabel(value as ReviewReadinessState)).join(" "), /ekspor/i);
});
