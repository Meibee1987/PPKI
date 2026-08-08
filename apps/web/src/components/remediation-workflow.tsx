"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import type { AuditFinding } from "../lib/audit-contract";
import { ApiRequestError } from "../lib/api";
import { createFixExecution, createReaudit, getComparison, getFixExecution, getReaudit, previewFixPlan, reconcileResolution } from "../lib/remediation-api";
import type { AuditComparison, FixExecutionStatus, FixPlanItemDisposition, FixPlanPreview, ReauditAccepted } from "../lib/remediation-contract";
import { canCreateReaudit, comparisonPresentation, failureCategoryLabel, failureMessage, isTerminalExecution, newIntentKey, nextPollDelay, toggleSelection } from "../lib/remediation-presentation";
import { ConfirmationDialog } from "./confirmation-dialog";
import { StatusBadge } from "./status-badge";

export function RemediationSelection({ items, selected, onSelectedChange }: { auditId: string; items: AuditFinding[]; selected: string[]; onSelectedChange: (ids: string[]) => void }) {
  return <fieldset className="selection-list" aria-describedby="selection-help"><legend>Pilih kandidat remediation</legend><p id="selection-help" className="muted">Selection ini hanya kandidat. Kelayakan akhir ditentukan oleh pratinjau server untuk exact selection. Maksimum 100 temuan.</p>
    {items.map(item => { const checked = selected.includes(item.id); return <label className="selection-row" key={item.id}><input type="checkbox" checked={checked} onChange={() => onSelectedChange(toggleSelection(selected, item.id, true))} /><span>{item.ruleCode}</span><small>Kandidat — belum dinyatakan eligible</small></label>; })}
  </fieldset>;
}

export function RemediationWorkflow({ auditId, selected, clearSelection }: { auditId: string; selected: string[]; clearSelection: () => void }) {
  const [preview, setPreview] = useState<FixPlanPreview>();
  const [execution, setExecution] = useState<FixExecutionStatus>();
  const [reaudit, setReaudit] = useState<ReauditAccepted>();
  const [reauditState, setReauditState] = useState("");
  const [comparison, setComparison] = useState<AuditComparison>();
  const [error, setError] = useState(""); const [busy, setBusy] = useState(false); const [confirm, setConfirm] = useState(false);
  const [lostResponse, setLostResponse] = useState(false); const intentKey = useRef<string | undefined>(undefined); const commandInFlight = useRef(false); const previewRequest = useRef<AbortController | undefined>(undefined); const activeAuditId = useRef(auditId);
  activeAuditId.current = auditId;
  const selectedSignature = [...selected].sort().join(",");
  useEffect(() => { previewRequest.current?.abort(); previewRequest.current = undefined; setPreview(undefined); intentKey.current = undefined; setLostResponse(false); setError(""); return () => previewRequest.current?.abort(); }, [auditId, selectedSignature]);
  useEffect(() => { setExecution(undefined); setReaudit(undefined); setReauditState(""); setComparison(undefined); setConfirm(false); commandInFlight.current = false; }, [auditId]);

  async function loadPreview() { if (previewRequest.current) return; const controller = new AbortController(); previewRequest.current = controller; setBusy(true); setError(""); try { const result = await previewFixPlan(auditId, selected, controller.signal); if (!controller.signal.aborted) { setPreview(result); intentKey.current = undefined; } } catch (value) { if ((value as { name?: string })?.name !== "AbortError") setError(commandError(value)); } finally { if (previewRequest.current === controller) { previewRequest.current = undefined; setBusy(false); } } }
  async function apply() {
    if (!preview || commandInFlight.current) return; const commandAuditId = auditId; commandInFlight.current = true; setBusy(true); setError(""); if (!intentKey.current) intentKey.current = newIntentKey();
    try { const accepted = await createFixExecution(commandAuditId, selected, preview.planHash, intentKey.current); const status = await getFixExecution(commandAuditId, accepted.id); if (activeAuditId.current !== commandAuditId) return; setExecution(status); setPreview(undefined); setLostResponse(false); setConfirm(false); }
    catch (value) { if (activeAuditId.current !== commandAuditId) return; if (value instanceof ApiRequestError && value.status === 409) { setPreview(undefined); setConfirm(false); intentKey.current = undefined; setError("Rencana bertentangan dengan state server terbaru. Muat ulang state dan buat pratinjau baru."); } else { setLostResponse(true); setError(commandError(value)); } }
    finally { if (activeAuditId.current === commandAuditId) { commandInFlight.current = false; setBusy(false); } }
  }

  useExecutionPolling(auditId, execution, setExecution, setError);
  useEffect(() => { if (!execution || !isTerminalExecution(execution.state)) return; setLostResponse(false); }, [execution?.state]);

  async function startReaudit() { if (!execution || !canCreateReaudit(execution.state, execution.resultDocumentVersionId) || commandInFlight.current) return; const commandAuditId = auditId; commandInFlight.current = true; setBusy(true); setError(""); try { const value = await createReaudit(execution.id); if (activeAuditId.current !== commandAuditId) return; setReaudit(value); setReauditState(value.status); } catch (value) { if (activeAuditId.current === commandAuditId) setError(commandError(value)); } finally { if (activeAuditId.current === commandAuditId) { commandInFlight.current = false; setBusy(false); } } }
  useEffect(() => {
    if (!reaudit || !execution) return; let timer: ReturnType<typeof setTimeout>; let stopped = false; let failures = 0; let active: AbortController | undefined;
    const poll = async () => { active = new AbortController(); try { const audit = await getReaudit(reaudit.auditId, active.signal); failures = 0; if (stopped) return; setReauditState(audit.status); if (audit.status === "Completed") { try { setComparison(await getComparison(execution.id, active.signal)); await reconcileResolution(execution.id); } catch (value) { if ((value as { name?: string })?.name !== "AbortError") setError(commandError(value)); } return; } if (audit.status === "Failed" || audit.status === "Cancelled") { setError("Re-audit berakhir tanpa comparison yang dapat digunakan."); return; } } catch (value) { if ((value as { name?: string })?.name !== "AbortError") failures += 1; } if (!stopped) timer = setTimeout(poll, nextPollDelay(failures)); };
    timer = setTimeout(poll, 1000); return () => { stopped = true; clearTimeout(timer); active?.abort(); };
  }, [reaudit?.auditId, execution?.id]);

  return <section className="panel workflow-panel" aria-labelledby="workflow-title"><div className="section-heading"><div><h2 id="workflow-title">Workflow remediation</h2><p>{selected.length} temuan dipilih. Selection hanya berlaku untuk audit aktif.</p></div><button className="text-button" type="button" disabled={!selected.length} onClick={clearSelection}>Kosongkan pilihan</button></div>
    {error && <div className="error-box" role="alert"><p>{error}</p><button className="text-button" type="button" onClick={() => setError("")}>Tutup pesan</button></div>}
    {!preview && !execution && <div className="workflow-empty"><p>{selected.length ? "Buat pratinjau canonical agar server menentukan kelayakan exact selection." : "Pilih kandidat temuan untuk diperiksa oleh server."}</p><button className="button" type="button" disabled={!selected.length || busy} onClick={loadPreview}>{busy ? "Memuat…" : "Buat pratinjau"}</button></div>}
    {preview && <PreviewCard preview={preview} onApply={() => setConfirm(true)} busy={busy} />}
    {lostResponse && preview && <button className="button secondary" type="button" disabled={busy} onClick={apply}>Kirim ulang intent yang sama</button>}
    {execution && <ExecutionCard execution={execution} />}
    {execution && canCreateReaudit(execution.state, execution.resultDocumentVersionId) && !reaudit && <button className="button" type="button" disabled={busy} onClick={startReaudit}>Jalankan re-audit canonical</button>}
    {reaudit && <div className="subpanel" aria-live="polite"><h3>Re-audit</h3><p>Status canonical: <strong>{reauditState || reaudit.status}</strong></p><Link href={`/audits/${reaudit.auditId}`}>Buka hasil re-audit</Link></div>}
    {comparison && <ComparisonView comparison={comparison} />}
    <ConfirmationDialog open={confirm} title="Terapkan rencana perbaikan?" description={`Server akan menjalankan ${preview?.operationCount ?? 0} operasi untuk ${preview?.selectedFindingCount ?? 0} temuan dan membuat DocumentVersion immutable baru. Hasil belum dianggap terverifikasi sampai re-audit selesai.`} confirmLabel="Terapkan exact plan" busy={busy} onConfirm={apply} onClose={() => setConfirm(false)} />
  </section>;
}

function useExecutionPolling(auditId: string, execution: FixExecutionStatus | undefined, setExecution: (value: FixExecutionStatus) => void, setError: (value: string) => void) {
  const executionId = execution?.id, terminal = execution ? isTerminalExecution(execution.state) : true;
  useEffect(() => {
    if (!executionId || terminal) return; let stopped = false; let timer: ReturnType<typeof setTimeout>; let failures = 0; let active: AbortController | undefined;
    const poll = async () => { active = new AbortController(); try { const value = await getFixExecution(auditId, executionId, active.signal); failures = 0; if (!stopped) setExecution(value); if (isTerminalExecution(value.state)) return; } catch (error) { if ((error as { name?: string })?.name !== "AbortError") { failures += 1; if (failures >= 3) setError("Status execution belum dapat diperbarui. Sistem akan mencoba kembali."); } } if (!stopped) timer = setTimeout(poll, nextPollDelay(failures)); };
    const visible = () => { if (document.visibilityState === "visible" && !stopped) { clearTimeout(timer); void poll(); } };
    timer = setTimeout(poll, 1500); document.addEventListener("visibilitychange", visible); return () => { stopped = true; clearTimeout(timer); active?.abort(); document.removeEventListener("visibilitychange", visible); };
  }, [auditId, executionId, terminal, setExecution, setError]);
}

function PreviewCard({ preview, onApply, busy }: { preview: FixPlanPreview; onApply: () => void; busy: boolean }) { const rejected = preview.items.filter(item => item.disposition !== "Planned"); return <article className="subpanel"><header><h3>Pratinjau canonical</h3><StatusBadge status={preview.state} /></header><dl className="compact-metrics"><div><dt>Dipilih</dt><dd>{preview.selectedFindingCount}</dd></div><div><dt>Operasi</dt><dd>{preview.operationCount}</dd></div><div><dt>Tidak didukung</dt><dd>{preview.unsupportedFindingCount}</dd></div><div><dt>Konflik</dt><dd>{preview.conflictFindingCount}</dd></div></dl>{rejected.length > 0 && <div><h4>Kandidat yang tidak eligible</h4><ul>{rejected.map(item => <li key={item.findingId}><strong>{item.ruleCode}</strong>: {dispositionLabel(item.disposition)} ({item.diagnosticCode})</li>)}</ul></div>}{preview.diagnostics.length > 0 && <p className="muted">Server melaporkan {preview.diagnostics.length} diagnostik aman.</p>}<p>Rencana dibentuk server dari snapshot historis. Payload operasi tidak ditampilkan atau dibentuk ulang di browser.</p><button className="button" type="button" disabled={preview.state !== "Ready" || preview.operationCount < 1 || busy} onClick={onApply}>Konfirmasi dan jalankan</button>{preview.state !== "Ready" && <p className="muted">Rencana belum siap diterapkan. Perbarui selection atau state server.</p>}</article>; }
function ExecutionCard({ execution }: { execution: FixExecutionStatus }) { return <article className="subpanel" aria-live="polite"><header><h3>Fix execution</h3><StatusBadge status={execution.state} /></header><p>Attempt {execution.attemptCount} dari {execution.maxAttempts} · {execution.completedOperationCount}/{execution.plannedOperationCount} operasi selesai.</p>{execution.retryPending && <p className="notice">Sistem akan mencoba kembali secara otomatis. Jangan menjalankan apply lagi.</p>}{execution.state === "Failed" && <div className="error-box"><strong>{failureCategoryLabel(execution.failureCategory)}</strong><p>{failureMessage(execution.failureCode)}</p></div>}{execution.state === "NoChange" && <p>Tidak ada perubahan yang perlu dipublikasikan; tidak ada versi baru.</p>}{execution.state === "Completed" && <p>Versi hasil immutable tersedia. Re-audit diperlukan untuk verifikasi finding.</p>}</article>; }
function ComparisonView({ comparison }: { comparison: AuditComparison }) { return <section className="subpanel" aria-labelledby="comparison-title"><h3 id="comparison-title">Perbandingan deterministik</h3><p className="muted">Diturunkan dari dua audit historis; matching tidak dihitung di browser.</p><div className="comparison-counts">{Object.entries(comparison.counts).map(([status, count]) => <span className="count-chip" key={status}>{comparisonPresentation(status as keyof typeof comparison.counts)}: <strong>{count}</strong></span>)}</div>{comparison.items.length ? <ul className="comparison-list">{comparison.items.map((item, index) => <li key={`${item.status}-${item.beforeFindingId ?? item.afterFindingId}-${index}`}><strong>{comparisonPresentation(item.status)}</strong> · {item.ruleCode} · {item.element}{item.beforeFindingId && <Link href={`/audits/${comparison.sourceAuditId}/findings/${item.beforeFindingId}`}>Temuan sumber</Link>}{item.afterFindingId && <Link href={`/audits/${comparison.resultAuditId}/findings/${item.afterFindingId}`}>Temuan hasil</Link>}</li>)}</ul> : <p>Comparison tersedia tanpa item pada halaman ini.</p>}</section>; }
function dispositionLabel(value?: FixPlanItemDisposition): string { return value === "Unsupported" ? "Capability tidak tersedia" : value === "Conflict" ? "Konflik server" : value === "InvalidSnapshot" ? "Snapshot tidak valid" : "Tidak eligible"; }
function commandError(value: unknown): string { if (value instanceof ApiRequestError) return value.status === 401 ? "Sesi berakhir. Masuk kembali untuk melanjutkan." : value.status === 403 ? "Akses ditolak. Hanya PPKIAdmin database-authoritative yang dapat memakai workflow ini." : value.status === 404 ? "Resource tidak tersedia atau sudah berubah." : value.status === 409 ? "State server berubah dan command tidak diterapkan. Muat ulang state canonical." : value.message; return "Permintaan tidak dapat diselesaikan. Muat ulang state sebelum mencoba kembali."; }
