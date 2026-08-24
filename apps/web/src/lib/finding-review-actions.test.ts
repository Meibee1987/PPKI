import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (relative: string) => readFileSync(new URL(relative, import.meta.url), "utf8");
const actions = read("../components/finding-review-actions.tsx");
const drawer = read("../components/finding-detail-drawer.tsx");
const list = read("../components/audit-finding-list.tsx");
const page = read("../components/streamlined-audit-client.tsx");
const api = read("./remediation-api.ts");
const baseApi = read("./api.ts");
const readiness = read("../../../../backend/tests/Ppki.RuleEngine.Tests/ReviewReadinessPolicyTests.cs");

const checks: [string, () => void][] = [
  ["manual-review request uses server permission", () => { assert.match(actions, /review\.permissions\.canRequestReview/); assert.match(actions, /ManualReviewRequest/); }],
  ["Ignore request uses server permission", () => assert.match(actions, /canRequest &&[\s\S]*IgnoreRequest/)],
  ["final Ignore uses the exact allowed decision", () => assert.match(actions, /review\.permissions\.canDecide && review\.allowedDecisions\.includes\("Ignore"\)/)],
  ["reason is required before opening confirmation", () => assert.match(actions, /validateFindingReviewReason\(reason\)[\s\S]*if \(validation/)],
  ["reason maximum is shared with the model", () => assert.match(actions, /maxLength=\{maximumFindingReviewReasonLength\}/)],
  ["reason is accessible", () => { assert.match(actions, /htmlFor=\{reasonId\}/); assert.match(actions, /aria-describedby=\{`\$\{reasonHelpId\} \$\{reasonCountId\}/); assert.match(actions, /aria-errormessage=\{reasonError \? reasonErrorId : undefined\}/); }],
  ["reason is React text and never HTML", () => { assert.match(actions, /\{latestEvent\.note\}/); assert.doesNotMatch(actions, /dangerouslySetInnerHTML/); }],
  ["reason is absent from URLs", () => { assert.doesNotMatch(api, /apiFetch\(`[^`]*\$\{(?:note|reason)/i); assert.match(api, /JSON\.stringify\(\{ requestedDisposition, note:/); }],
  ["duplicate submissions have a synchronous guard", () => assert.match(actions, /commandInFlight\.current/)],
  ["commands use stable idempotency keys", () => { assert.match(actions, /idempotencyKey\.current = newIntentKey\(\)/); assert.match(api, /"Idempotency-Key": key/); }],
  ["correct audit and finding IDs are submitted", () => assert.match(actions, /requestFindingReview\(identity\.auditId, findingId/)],
  ["document version is checked from the review read model", () => assert.match(actions, /assertCanonicalFindingReview\(identity, findingId/)],
  ["successful mutation refreshes detail list and summary", () => { assert.match(actions, /await onChanged\(\)/); assert.match(drawer, /setReload\(value => value \+ 1\)/); assert.match(list, /setReload\(value => value \+ 1\); await refreshSummary\(\)/); }],
  ["summary refresh is backend authoritative", () => assert.match(page, /assertCanonicalSummary\(identity, await getAuditSummary\(identity\.auditId\)\)/)],
  ["frontend never calculates readiness", () => assert.doesNotMatch(actions, /blockingFindingCount|readinessState|ReadyForReview\s*=/)],
  ["ignored blocker remains blocking", () => { assert.match(readiness, /FindingReviewEventType\.Ignored/); assert.match(readiness, /Blocking_finding_remains_effective_without_verified_resolution/); }],
  ["Ignore is explicitly not VerifiedResolved", () => assert.match(actions, /Ignore tidak berarti VerifiedResolved/)],
  ["manual review does not claim technical resolution", () => assert.match(actions, /Status teknis finding tidak dianggap selesai/)],
  ["401 remains delegated to shared transport", () => assert.match(baseApi, /response\.status === 401\) dependencies\.onUnauthorized\(\)/)],
  ["forbidden is safely presented", () => assert.match(actions, /value\.status === 403[\s\S]*tidak memiliki izin/)],
  ["conflict clears intent and refreshes canonical review", () => assert.match(actions, /value\.status === 409[\s\S]*setPending\(undefined\)[\s\S]*setReload/)],
  ["identity change aborts old loads and commands", () => assert.match(actions, /controller\.abort\(\); commandController\.current\?\.abort\(\)/)],
  ["late mutation cannot update a newer selection", () => assert.match(actions, /activeIdentityRef\.current !== commandIdentity/)],
  ["S6-T04 URL and loaded page state are untouched", () => { assert.doesNotMatch(actions, /useRouter|useSearchParams|findingsQuery|setLoaded/); assert.match(list, /filters=\{visible\.filters\} page=\{visible\.page\}/); }],
  ["S6-T05 evidence remains in the drawer", () => { assert.match(drawer, /Aktual \(Actual\)/); assert.match(drawer, /Diharapkan \(Expected\)/); assert.match(drawer, /FindingReviewActions/); }],
  ["AcceptedRisk and export waiver are not introduced", () => assert.doesNotMatch(actions, /AcceptedRisk|AcceptRisk|ReadyForExport|waiver/i)],
  ["S6-T07 source navigation is absent", () => assert.doesNotMatch(actions, /sourceReference|source.*(?:href|router\.push)/i)],
  ["confirmation explains administrative impact", () => { assert.match(actions, /<ConfirmationDialog/); assert.match(actions, /tidak menghapus blocker/); }],
  ["drawer Escape defers to the nested confirmation", () => assert.match(drawer, /querySelector\("dialog\[open\]"\)\) return/)],
];

for (const [name, run] of checks) test(name, run);
