"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { isApiRequestAborted } from "../lib/api";
import { approveFixPlan, createFixPlanDraft, previewFixPlanDraft, updateFixPlanDraft } from "../lib/fix-plan-api";
import type { FixPlanApproval, FixPlanDraft, FixPlanPreview, FixPlanPreviewItem } from "../lib/fix-plan-contract";
import { approvalBlockReason, canonicalFindingIds, fixPlanError, newIdempotencyKey, type SelectedFixFinding } from "../lib/fix-plan-selection";
import type { CanonicalAuditIdentity } from "../lib/canonical-audit-identity";

export function FixPlanWorkflow({ identity, selected, locked, onApproved, onClear, onAuthoritativeRefresh }: { identity: CanonicalAuditIdentity; selected: SelectedFixFinding[]; locked: boolean; onApproved: () => void; onClear: () => void; onAuthoritativeRefresh: () => void }) {
  const ids = useMemo(() => canonicalFindingIds(selected), [selected]);
  const signature = `${identity.auditId}:${identity.documentVersionId}:${ids.join(",")}`;
  const [draft, setDraft] = useState<FixPlanDraft>();
  const [preview, setPreview] = useState<FixPlanPreview>();
  const [approvedIds, setApprovedIds] = useState<Set<string>>(new Set());
  const [approval, setApproval] = useState<FixPlanApproval>();
  const [busy, setBusy] = useState<"review" | "approve">();
  const [error, setError] = useState("");
  const request = useRef<{ sequence: number; controller?: AbortController }>({ sequence: 0 });
  const createIntent = useRef<{ signature: string; key: string } | undefined>(undefined);
  const auditKey = `${identity.auditId}:${identity.documentVersionId}`;
  const priorAuditKey = useRef(auditKey);
  const reviewButton = useRef<HTMLButtonElement>(null);
  const reviewHeading = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    request.current.controller?.abort();
    request.current.sequence += 1;
    setPreview(undefined); setApprovedIds(new Set()); setError("");
    if (priorAuditKey.current !== auditKey) { setDraft(undefined); setApproval(undefined); priorAuditKey.current = auditKey; }
  }, [auditKey, signature]);
  useEffect(() => () => request.current.controller?.abort(), []);

  async function review() {
    if (!ids.length || locked) return;
    request.current.controller?.abort();
    const controller = new AbortController(); const sequence = ++request.current.sequence;
    request.current.controller = controller; setBusy("review"); setError(""); setPreview(undefined); setApprovedIds(new Set());
    try {
      if (!createIntent.current || createIntent.current.signature !== signature) createIntent.current = { signature, key: newIdempotencyKey() };
      const persisted = draft?.state === "Draft"
        ? await updateFixPlanDraft(identity.auditId, draft.id, ids, controller.signal)
        : await createFixPlanDraft(identity.auditId, ids, createIntent.current.key, controller.signal);
      if (sequence !== request.current.sequence || signature !== `${identity.auditId}:${identity.documentVersionId}:${ids.join(",")}`) return;
      if (persisted.auditId !== identity.auditId || persisted.sourceDocumentVersionId !== identity.documentVersionId) throw new Error("identity-mismatch");
      setDraft(persisted);
      const value = await previewFixPlanDraft(identity.auditId, persisted.id, controller.signal);
      if (sequence !== request.current.sequence || value.planId !== persisted.id || value.auditId !== identity.auditId || value.sourceDocumentVersionId !== identity.documentVersionId) return;
      setPreview(value);
      if (value.state === "Stale") onAuthoritativeRefresh();
      queueMicrotask(() => reviewHeading.current?.focus());
    } catch (value) { if (!isApiRequestAborted(value) && sequence === request.current.sequence) setError(fixPlanError(value)); }
    finally { if (sequence === request.current.sequence) setBusy(undefined); }
  }

  async function approve() {
    if (!preview || approvalBlockReason(preview, approvedIds) || busy) return;
    const controller = new AbortController(); const sequence = ++request.current.sequence;
    request.current.controller = controller; setBusy("approve"); setError("");
    try {
      const value = await approveFixPlan(identity.auditId, preview.planId, [...approvedIds].sort(), controller.signal);
      if (sequence !== request.current.sequence || value.auditId !== identity.auditId || value.sourceDocumentVersionId !== identity.documentVersionId || value.planId !== preview.planId) return;
      setApproval(value); onApproved();
    } catch (value) {
      if (!isApiRequestAborted(value) && sequence === request.current.sequence) { setError(fixPlanError(value)); if (value && typeof value === "object" && "status" in value && value.status === 409) { setPreview(undefined); setApprovedIds(new Set()); onAuthoritativeRefresh(); } }
    } finally { if (sequence === request.current.sequence) setBusy(undefined); }
  }

  if (!ids.length && !approval) return null;
  if (approval) return <section className="fix-plan-review" aria-labelledby="fix-plan-approved-title"><h3 id="fix-plan-approved-title">Rencana telah disetujui</h3><p>Snapshot immutable berisi {approval.itemCount} item telah dibuat. Job apply berstatus <strong>{approval.applyJobState}</strong>.</p><p className="muted">Persetujuan ini belum berarti dokumen sudah diperbaiki. Proses eksekusi tidak dijalankan atau dipantau dari layar ini.</p></section>;
  return <section className="fix-plan-review" aria-labelledby="fix-plan-review-title">
    <div className="fix-plan-toolbar"><div><h3 id="fix-plan-review-title" ref={reviewHeading} tabIndex={-1}>Review rencana perbaikan</h3><p>{ids.length} temuan dipilih. Pilihan disimpan sebagai draft sebelum preview dibuat.</p></div><div><button ref={reviewButton} className="button secondary" type="button" disabled={busy !== undefined || locked} onClick={review}>{preview ? "Perbarui preview" : "Review pilihan"}</button><button className="text-button" type="button" disabled={busy !== undefined || locked} onClick={onClear}>Hapus pilihan</button></div></div>
    {busy === "review" && <p role="status">Menyimpan draft dan menyiapkan preview...</p>}
    {error && <div className="error-box" role="alert"><p>{error}</p>{!preview && <button className="text-button" type="button" onClick={review}>Perbarui preview</button>}</div>}
    {preview && <Preview value={preview} selected={selected} approvedIds={approvedIds} setApprovedIds={setApprovedIds} />}
    {preview && <div className="fix-plan-approval"><p><strong>Total:</strong> {preview.itemCount} item · <strong>Konflik:</strong> {preview.mutationAnalysis?.conflictItemCount ?? 0} · <strong>Urutan/dependensi:</strong> {preview.mutationAnalysis?.orderedItemCount ?? 0}</p>{approvalBlockReason(preview, approvedIds) && <p className="warning-copy" role="status">{approvalBlockReason(preview, approvedIds)}</p>}<button className="button" type="button" disabled={busy !== undefined || Boolean(approvalBlockReason(preview, approvedIds))} onClick={approve}>{busy === "approve" ? "Menyetujui..." : "Setujui rencana dan antrekan apply"}</button></div>}
  </section>;
}

function Preview({ value, selected, approvedIds, setApprovedIds }: { value: FixPlanPreview; selected: SelectedFixFinding[]; approvedIds: Set<string>; setApprovedIds: (value: Set<string>) => void }) {
  const metadata = new Map(selected.map(x => [x.findingId, x]));
  const groups = new Map<string, FixPlanPreviewItem[]>();
  for (const item of value.items) { const meta = metadata.get(item.findingId); const key = `${meta?.domain ?? "Domain lain"} · ${locationLabel(item)}`; groups.set(key, [...(groups.get(key) ?? []), item]); }
  const analysis = new Map(value.mutationAnalysis?.items.map(x => [x.itemId, x]) ?? []);
  return <div className="fix-plan-preview"><p className="fix-plan-state"><strong>Status preview:</strong> {value.state}</p>{value.mutationAnalysis?.conflicts.length ? <div className="error-box"><strong>Konflik terdeteksi</strong><ul>{value.mutationAnalysis.conflicts.map((x, index) => <li key={index}>{x.itemIds.length} item: {x.reasonCode}</li>)}</ul></div> : null}{value.mutationAnalysis?.relationships.some(x => x.kind !== "Independent") ? <div className="notice"><strong>Dependensi dan hubungan perubahan</strong><ul>{value.mutationAnalysis.relationships.filter(x => x.kind !== "Independent").map((x, index) => <li key={index}>{x.kind}: {x.reasonCode}</li>)}</ul></div> : null}
    {[...groups].map(([label, items]) => <section className="fix-plan-group" key={label}><h4>{label}</h4><div className="fix-plan-items">{items.map(item => { const status = analysis.get(item.itemId); return <article className="fix-plan-item" key={item.itemId}><header><strong>{item.ruleCode}</strong><span className="domain-label">{item.fixMode}</span></header><p>{item.change?.propertyLabel ?? item.propertyIdentifier ?? "Perubahan tidak tersedia"}</p>{item.change && <dl className="fix-plan-change"><div><dt>{item.change.beforeLabel}</dt><dd>{item.change.beforeValue ?? "Tidak tersedia"}</dd></div><div><dt>{item.change.afterLabel}</dt><dd>{item.change.afterValue ?? "Tidak tersedia"}</dd></div></dl>}<p className="muted">Analisis: {status?.status ?? item.previewState}{status?.executionOrdinal != null ? ` · urutan ${status.executionOrdinal}` : ""} · {status?.reasonCode ?? item.reasonCode}</p>{item.requiresExplicitApproval && <label className="confirm-consent"><input type="checkbox" checked={approvedIds.has(item.itemId)} onChange={event => { const next = new Set(approvedIds); event.target.checked ? next.add(item.itemId) : next.delete(item.itemId); setApprovedIds(next); }} />Saya menyetujui perubahan Confirm ini secara eksplisit.</label>}</article>; })}</div></section>)}
  </div>;
}

function locationLabel(item: FixPlanPreviewItem): string { const x = item.location; if (!x) return "Lokasi tidak tersedia"; const detail = [["bagian", x.sectionIndex], ["elemen", x.bodyElementIndex], ["paragraf", x.paragraphIndex], ["run", x.runIndex]].filter(([, v]) => v !== null).map(([k, v]) => `${k} ${v}`).join(", "); return detail ? `${x.scope} (${detail})` : x.scope; }
