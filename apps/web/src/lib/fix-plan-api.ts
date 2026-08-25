import { apiFetch } from "./api.ts";
import { parseFixPlanApproval, parseFixPlanDraft, parseFixPlanPreview, type FixPlanApproval, type FixPlanDraft, type FixPlanPreview } from "./fix-plan-contract.ts";

const root = (auditId: string) => `/api/audits/${encodeURIComponent(auditId)}/fix-plans`;
export async function createFixPlanDraft(auditId: string, findingIds: string[], idempotencyKey: string, signal?: AbortSignal): Promise<FixPlanDraft> { return parseFixPlanDraft(await apiFetch(root(auditId), { method: "POST", headers: { "Idempotency-Key": idempotencyKey }, body: JSON.stringify({ findingIds }), signal })); }
export async function updateFixPlanDraft(auditId: string, planId: string, findingIds: string[], signal?: AbortSignal): Promise<FixPlanDraft> { return parseFixPlanDraft(await apiFetch(`${root(auditId)}/${encodeURIComponent(planId)}`, { method: "PUT", body: JSON.stringify({ findingIds }), signal })); }
export async function previewFixPlanDraft(auditId: string, planId: string, signal?: AbortSignal): Promise<FixPlanPreview> { return parseFixPlanPreview(await apiFetch(`${root(auditId)}/${encodeURIComponent(planId)}/preview`, { signal })); }
export async function approveFixPlan(auditId: string, planId: string, approvedConfirmItemIds: string[], signal?: AbortSignal): Promise<FixPlanApproval> { return parseFixPlanApproval(await apiFetch(`${root(auditId)}/${encodeURIComponent(planId)}/approval`, { method: "POST", body: JSON.stringify({ approvedConfirmItemIds }), signal })); }
