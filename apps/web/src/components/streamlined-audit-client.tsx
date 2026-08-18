"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useCallback, useEffect, useId, useMemo, useRef, useState } from "react";
import { ApiRequestError, apiFetchBlob } from "../lib/api";
import { getAuditSummary, listAuditFindings } from "../lib/audit-api";
import type { AuditFindingPage, AuditSummary } from "../lib/audit-contract";
import { createTextCorrectionBatch, getDocumentPreviewState, getTextCorrectionBatch, getTextCorrectionContext, listTextCorrections, submitTextCorrectionDecision } from "../lib/text-correction-api";
import type { CorrectionAction, CorrectionBatchStatus, DocumentPreviewState, TextCorrectionContext, TextCorrectionPage, TextCorrectionProposal } from "../lib/text-correction-contract";
import { automaticProgress, batchProgress, contextStateCopy, decisionLabel, highlightedContext, isTerminalBatch, pageLocationLabel, previewFragment, safeCommandMessage, scalarCount, validateManualReplacement } from "../lib/streamlined-audit-presentation";
import { ConfirmationDialog } from "./confirmation-dialog";
import { FindingPayload } from "./finding-payload";
import { FindingLocation } from "./finding-location";

const PAGE_SIZE = 25;
type ContextView = { state: "Loading" | "Exact" | "Stale" | "Unsupported" | "Unavailable"; value?: TextCorrectionContext };
type CorrectionFilter = "All" | "Undecided" | "Selected" | "Ignored" | "Problem";

export function StreamlinedAuditClient() {
  const auditId = String(useParams().auditId);
  const [summary, setSummary] = useState<AuditSummary>();
  const [corrections, setCorrections] = useState<TextCorrectionPage>();
  const [manualFindings, setManualFindings] = useState<AuditFindingPage>();
  const [proposalPage, setProposalPage] = useState(1);
  const [filter, setFilter] = useState<CorrectionFilter>("All");
  const [contexts, setContexts] = useState<Record<string, ContextView>>({});
  const [editing, setEditing] = useState<string>();
  const [manualValue, setManualValue] = useState("");
  const [manualError, setManualError] = useState("");
  const [busyProposal, setBusyProposal] = useState<string>();
  const [batch, setBatch] = useState<CorrectionBatchStatus>();
  const [preview, setPreview] = useState<DocumentPreviewState>();
  const [finalSummary, setFinalSummary] = useState<AuditSummary>();
  const [confirmBatch, setConfirmBatch] = useState(false);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [reload, setReload] = useState(0);
  const decisionKeys = useRef(new Map<string, string>());
  const batchKey = useRef<string | undefined>(undefined);
  const contextRequests = useRef(new Map<string, AbortController>());

  const loadCorrections = useCallback(async (signal?: AbortSignal) => {
    const value = await listTextCorrections(auditId, proposalPage, PAGE_SIZE, signal);
    setCorrections(value);
    setBatch(value.activeBatch ?? undefined);
  }, [auditId, proposalPage]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setError(""); setCorrections(undefined); setBatch(undefined); setContexts({});
    getAuditSummary(auditId, controller.signal)
      .then(setSummary)
      .catch(value => { if (!controller.signal.aborted) setError(commandMessage(value)); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [auditId, reload]);

  useEffect(() => {
    const state = summary?.automaticRemediation?.state;
    if (summary?.status === "Completed" && (!state || ["NoAction", "Completed", "Failed", "Conflict"].includes(state))) return;
    if (summary?.status === "Failed" || summary?.status === "Cancelled") return;
    const timer = setTimeout(() => setReload(value => value + 1), 1500);
    return () => clearTimeout(timer);
  }, [summary?.status, summary?.automaticRemediation?.state, reload]);

  useEffect(() => {
    if (summary?.status !== "Completed") return;
    const automatic = summary.automaticRemediation?.state;
    if (automatic && !["NoAction", "Completed"].includes(automatic)) return;
    const controller = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined;
    const load = async () => {
      try { await loadCorrections(controller.signal); }
      catch (value) {
        if (controller.signal.aborted) return;
        if (value instanceof ApiRequestError && value.status === 404) timer = setTimeout(load, 1500);
        else setError(commandMessage(value));
      }
    };
    void load();
    return () => { controller.abort(); if (timer) clearTimeout(timer); };
  }, [summary?.status, summary?.automaticRemediation?.state, loadCorrections]);

  useEffect(() => {
    if (summary?.status !== "Completed") return;
    const controller = new AbortController();
    listAuditFindings(auditId, { fixMode: "Manual", page: 1, pageSize: 25 }, controller.signal)
      .then(setManualFindings).catch(() => { /* Optional legacy section fails closed. */ });
    return () => controller.abort();
  }, [auditId, summary?.status]);

  useEffect(() => {
    if (!batch || isTerminalBatch(batch.state)) return;
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined, active: AbortController | undefined;
    const poll = async () => {
      active = new AbortController();
      try { const value = await getTextCorrectionBatch(batch.id, active.signal); if (!stopped) setBatch(value); }
      catch (value) { if (!stopped && (value as { name?: string })?.name !== "AbortError") setError(commandMessage(value)); }
      if (!stopped) timer = setTimeout(poll, 1500);
    };
    timer = setTimeout(poll, 1500);
    return () => { stopped = true; active?.abort(); if (timer) clearTimeout(timer); };
  }, [batch?.id, batch?.state]);

  useEffect(() => {
    const versionId = batch?.resultDocumentVersionId;
    if (!versionId) { setPreview(undefined); return; }
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined, active: AbortController | undefined;
    const poll = async () => {
      active = new AbortController();
      try {
        const value = await getDocumentPreviewState(versionId, active.signal);
        if (stopped) return; setPreview(value);
        if (value.state === "Pending" || value.state === "Processing") timer = setTimeout(poll, 1500);
      } catch (value) { if (!stopped && (value as { name?: string })?.name !== "AbortError") setError(commandMessage(value)); }
    };
    void poll();
    return () => { stopped = true; active?.abort(); if (timer) clearTimeout(timer); };
  }, [batch?.resultDocumentVersionId]);

  useEffect(() => {
    if (batch?.state !== "Completed" || !batch.reauditId) { setFinalSummary(undefined); return; }
    const controller = new AbortController();
    getAuditSummary(batch.reauditId, controller.signal)
      .then(setFinalSummary)
      .catch(value => { if (!controller.signal.aborted) setError(commandMessage(value)); });
    return () => controller.abort();
  }, [batch?.state, batch?.reauditId]);

  useEffect(() => () => { for (const controller of contextRequests.current.values()) controller.abort(); contextRequests.current.clear(); }, [auditId]);

  async function loadContext(proposalId: string): Promise<TextCorrectionContext | undefined> {
    const existing = contexts[proposalId]; if (existing?.value) return existing.value;
    contextRequests.current.get(proposalId)?.abort();
    const controller = new AbortController(); contextRequests.current.set(proposalId, controller);
    setContexts(value => ({ ...value, [proposalId]: { state: "Loading" } }));
    try {
      const value = await getTextCorrectionContext(proposalId, controller.signal);
      setContexts(current => ({ ...current, [proposalId]: { state: value.anchorStatus, value } })); return value;
    } catch (reason) {
      if ((reason as { name?: string })?.name !== "AbortError") setContexts(current => ({ ...current, [proposalId]: { state: "Unavailable" } }));
    } finally { contextRequests.current.delete(proposalId); }
  }

  async function choose(proposal: TextCorrectionProposal, action: CorrectionAction, replacement?: string) {
    const intent = `${proposal.id}:${action}:${replacement ?? ""}`;
    const key = decisionKeys.current.get(intent) ?? crypto.randomUUID(); decisionKeys.current.set(intent, key);
    setBusyProposal(proposal.id); setError("");
    try { await submitTextCorrectionDecision(proposal.id, action, key, replacement); await loadCorrections(); decisionKeys.current.delete(intent); setEditing(undefined); }
    catch (value) { if (value instanceof ApiRequestError && value.status === 409) await loadCorrections(); setError(commandMessage(value)); }
    finally { setBusyProposal(undefined); }
  }

  async function beginManual(proposal: TextCorrectionProposal) {
    const context = await loadContext(proposal.id); if (!context || context.anchorStatus !== "Exact") return;
    setEditing(proposal.id); setManualValue(context.suggestedReplacement); setManualError("");
  }

  async function saveManual(proposal: TextCorrectionProposal) {
    const validation = validateManualReplacement(manualValue); setManualError(validation ?? "");
    if (!validation) await choose(proposal, "EditManual", manualValue);
  }

  async function createBatch() {
    const key = batchKey.current ?? crypto.randomUUID(); batchKey.current = key; setError("");
    try {
      const accepted = await createTextCorrectionBatch(auditId, key);
      const canonical = await getTextCorrectionBatch(accepted.id); setBatch(canonical); setConfirmBatch(false);
    } catch (value) { if (value instanceof ApiRequestError && value.status === 409) await loadCorrections(); setError(commandMessage(value)); }
  }

  const visibleItems = useMemo(() => corrections?.items.filter(item => filter === "All"
    || filter === "Undecided" && item.effectiveDecision === null
    || filter === "Selected" && ["UseSuggestion", "EditManual"].includes(item.effectiveDecision?.action ?? "")
    || filter === "Ignored" && item.effectiveDecision?.action === "Ignore"
    || filter === "Problem" && item.anchorStatus !== "Exact") ?? [], [corrections?.items, filter]);

  if (loading) return <PageState title="Memuat hasil audit" message="Memeriksa status dokumen..." />;
  if (!summary) return <PageState title="Hasil audit tidak tersedia" message={error || "Data tidak ditemukan atau tidak dapat diakses."} retry={() => setReload(value => value + 1)} />;
  const readyCount = corrections?.summary.eligibleDecisionCount ?? 0;
  const final = batch?.state === "Completed";

  return <main className="page-shell streamlined-audit-page">
    <Link className="back-link" href="/">← Dokumen saya</Link>
    <header className="streamlined-header"><div><p className="eyebrow">{final ? "Hasil akhir" : "Hasil audit"}</p><h1>{final ? "Perbaikan selesai" : "Hasil Audit"}</h1><p className="muted">{final ? "Perbaikan telah diterapkan ke satu versi baru dan diverifikasi." : "Tinjau hanya temuan yang membutuhkan keputusan Anda."}</p></div></header>
    {error && <div className="error-box" role="alert">{error}<button className="text-button" type="button" onClick={() => setReload(value => value + 1)}>Coba lagi</button></div>}
    <AuditProgress summary={summary} />
    <section className="summary-strip" aria-label={final ? "Ringkasan hasil akhir" : "Ringkasan hasil audit"}>
      <SummaryMetric label={final ? "Temuan sebelum keputusan" : "Masalah ditemukan"} value={summary.findingCount} />
      <SummaryMetric label="Diperbaiki otomatis" value={summary.automaticRemediation?.verifiedResolvedCount ?? 0} />
      {!final && <SummaryMetric label="Perlu keputusan" value={corrections?.summary.undecidedCount ?? 0} />}
      <SummaryMetric label="Diabaikan" value={corrections?.summary.ignoredCount ?? 0} />
      {final ? <SummaryMetric label="Diperbaiki admin" value={batch.verificationCounts.VerifiedResolved ?? 0} />
        : <SummaryMetric label="Masih perlu pemeriksaan" value={summary.automaticRemediation?.stillDetectedCount ?? summary.fixModes.manual} />}
      {final && finalSummary && <SummaryMetric label="Temuan versi akhir" value={finalSummary.findingCount} />}
    </section>
    {final && !finalSummary && <p className="muted" role="status">Memuat ringkasan versi akhir...</p>}

    {batch && <BatchPanel batch={batch} preview={preview} openFinal={() => void openPreview(batch.resultDocumentVersionId, null)} />}

    {!final && corrections && <section className="correction-section" aria-labelledby="corrections-title">
      <div className="section-heading"><div><h2 id="corrections-title">Perbaikan bahasa</h2><p>{corrections.totalCount} usulan tersedia. Konteks dimuat hanya saat diminta.</p></div>
        <label className="compact-filter">Tampilkan<select value={filter} onChange={event => setFilter(event.target.value as CorrectionFilter)}><option value="All">Semua</option><option value="Undecided">Perlu keputusan</option><option value="Selected">Dipilih</option><option value="Ignored">Diabaikan</option><option value="Problem">Masih bermasalah</option></select></label>
      </div>
      <div className="correction-list">{visibleItems.map(proposal => <CorrectionCard key={proposal.id} proposal={proposal} context={contexts[proposal.id]} editing={editing === proposal.id} manualValue={manualValue} manualError={manualError} busy={busyProposal === proposal.id} previewReady={summary.documentRender.previewAvailable} onContext={() => void loadContext(proposal.id)} onUse={() => void choose(proposal, "UseSuggestion")} onEdit={() => void beginManual(proposal)} onIgnore={() => void choose(proposal, "Ignore")} onManualChange={setManualValue} onSave={() => void saveManual(proposal)} onCancel={() => { setEditing(undefined); setManualError(""); }} openPreview={() => void openPreview(corrections.documentVersionId, proposal.pageLocation)} />)}</div>
      <nav className="pagination" aria-label="Navigasi halaman usulan"><button className="button secondary" type="button" disabled={corrections.page <= 1} onClick={() => setProposalPage(value => value - 1)}>Sebelumnya</button><span>Halaman {corrections.page} dari {Math.max(1, Math.ceil(corrections.totalCount / corrections.pageSize))}</span><button className="button secondary" type="button" disabled={corrections.page * corrections.pageSize >= corrections.totalCount} onClick={() => setProposalPage(value => value + 1)}>Berikutnya</button></nav>
    </section>}

    {!final && readyCount > 0 && !batch && <aside className="batch-bar" aria-live="polite"><div><strong>{readyCount} perbaikan siap diterapkan</strong><span>Semua pilihan diterapkan ke satu versi baru.</span></div><button className="button" type="button" onClick={() => setConfirmBatch(true)}>Terapkan {readyCount} Perbaikan</button></aside>}
    {!final && corrections && readyCount === 0 && !batch && <aside className="batch-bar batch-disabled"><div><strong>Belum ada perbaikan siap diterapkan</strong><span>Pilih Gunakan Saran atau Edit Manual pada usulan.</span></div><button className="button" type="button" disabled>Terapkan Perbaikan</button></aside>}

    {manualFindings && manualFindings.items.length > 0 && <details className="panel legacy-findings"><summary>Lihat temuan yang perlu pemeriksaan manual</summary><p className="muted">Workflow lama tetap tersedia pada detail temuan struktural yang belum memiliki alur khusus.</p><ul>{manualFindings.items.map(item => <li key={item.id}><div><strong>{item.element}</strong><FindingLocation value={item.location} /></div><Link className="button secondary" href={`/audits/${encodeURIComponent(auditId)}/findings/${encodeURIComponent(item.id)}`}>Periksa temuan</Link></li>)}</ul></details>}
    <ConfirmationDialog open={confirmBatch} title={`Terapkan ${readyCount} perbaikan?`} description={`Terapkan ${readyCount} perbaikan ke satu versi baru dokumen?`} confirmLabel="Terapkan" busy={Boolean(batch && !isTerminalBatch(batch.state))} onConfirm={createBatch} onClose={() => setConfirmBatch(false)} />
  </main>;
}

function AuditProgress({ summary }: { summary: AuditSummary }) {
  if (summary.status !== "Completed") return <section className="panel progress-panel" aria-live="polite"><h2>Memeriksa dokumen...</h2><p>{summary.status === "Failed" ? "Audit tidak dapat diselesaikan." : "Audit sedang diproses."}</p></section>;
  if (!summary.automaticRemediation) return null;
  const failed = summary.automaticRemediation.state === "Failed" || summary.automaticRemediation.state === "Conflict";
  return <section className={`panel progress-panel${failed ? " progress-failed" : ""}`} aria-live="polite" aria-busy={["Pending", "Queued", "Processing", "ReauditPending"].includes(summary.automaticRemediation.state)}><h2>Progres dokumen</h2><ProgressList steps={automaticProgress(summary.automaticRemediation.state)} />{summary.automaticRemediation.state === "Completed" && <p className="success-copy">✓ {summary.automaticRemediation.verifiedResolvedCount} masalah format diperbaiki dan diverifikasi otomatis.</p>}{failed && <p>{summary.automaticRemediation.state === "Conflict" ? "Dokumen telah berubah. Muat ulang hasil audit." : "Perbaikan format otomatis tidak dapat diselesaikan. Dokumen asli tetap aman."}</p>}</section>;
}

function BatchPanel({ batch, preview, openFinal }: { batch: CorrectionBatchStatus; preview?: DocumentPreviewState; openFinal: () => void }) {
  const conflict = batch.state === "Conflict", failed = batch.state === "Failed";
  return <section className={`panel progress-panel${conflict || failed ? " progress-failed" : ""}`} aria-live="polite" aria-busy={!isTerminalBatch(batch.state)}><h2>{batch.state === "Completed" ? "Hasil Akhir" : "Menerapkan perbaikan"}</h2><ProgressList steps={batchProgress(batch, preview)} />{conflict && <p>Dokumen atau pilihan perbaikan telah berubah. Muat ulang hasil audit.</p>}{failed && <p>Perbaikan tidak dapat diterapkan. Dokumen asli tetap aman.</p>}{batch.state === "Completed" && <div className="final-actions">{preview?.previewAvailable ? <button className="button" type="button" onClick={openFinal}>Buka Dokumen Final</button> : <span>{preview?.state === "Failed" ? "Preview belum tersedia." : "Preview sedang dibuat..."}</span>}{batch.reauditId && <Link className="button secondary" href={`/audits/${encodeURIComponent(batch.reauditId)}`}>Lihat Temuan Tersisa</Link>}</div>}</section>;
}

function ProgressList({ steps }: { steps: ReturnType<typeof automaticProgress> }) { return <ol className="progress-list">{steps.map(item => <li key={item.label} className={`progress-${item.tone}`}><span aria-hidden="true">{item.tone === "done" ? "✓" : item.tone === "active" ? "…" : item.tone === "failed" ? "!" : "○"}</span><strong>{item.label}</strong><small>{item.status}</small></li>)}</ol>; }
function SummaryMetric({ label, value }: { label: string; value: number }) { return <article><strong>{value}</strong><span>{label}</span></article>; }

function CorrectionCard(props: { proposal: TextCorrectionProposal; context?: ContextView; editing: boolean; manualValue: string; manualError: string; busy: boolean; previewReady: boolean; onContext: () => void; onUse: () => void; onEdit: () => void; onIgnore: () => void; onManualChange: (value: string) => void; onSave: () => void; onCancel: () => void; openPreview: () => void }) {
  const { proposal, context, editing, manualValue, manualError, busy } = props;
  const fieldId = useId();
  return <article className="correction-card"><header><div><span className="page-label">{pageLocationLabel(proposal.pageLocation, !proposal.pageLocation)}</span><span className="category-label">{proposal.category}</span></div><span className={`decision-badge decision-${proposal.effectiveDecision?.action?.toLowerCase() ?? "none"}`}>{decisionLabel(proposal.effectiveDecision?.action ?? null)}</span></header>
    <ContextBlock view={context} />
    {!context && <button className="text-button" type="button" onClick={props.onContext}>Tampilkan kalimat dan saran</button>}
    {props.previewReady && <button className="text-button preview-link" type="button" onClick={props.openPreview}>Buka di dokumen</button>}
    {editing ? <div className="manual-editor"><label htmlFor={fieldId}>Perbaikan untuk bagian yang ditandai</label><textarea id={fieldId} value={manualValue} aria-describedby={`${fieldId}-count${manualError ? ` ${fieldId}-error` : ""}`} onChange={event => props.onManualChange(event.target.value)} /><div id={`${fieldId}-count`} className="counter">{scalarCount(manualValue)} / 256</div>{manualError && <p id={`${fieldId}-error`} className="field-error" role="alert">{manualError}</p>}<div className="card-actions"><button className="button" type="button" disabled={busy} onClick={props.onSave}>Simpan Perbaikan</button><button className="button secondary" type="button" disabled={busy} onClick={props.onCancel}>Batal</button></div></div>
      : <div className="card-actions"><button className="button" type="button" disabled={busy || proposal.anchorStatus !== "Exact"} onClick={props.onUse}>Gunakan Saran</button><button className="button secondary" type="button" disabled={busy || proposal.anchorStatus !== "Exact"} onClick={props.onEdit}>Edit Manual</button><button className="button secondary" type="button" disabled={busy} onClick={props.onIgnore}>Abaikan</button></div>}
  </article>;
}

function ContextBlock({ view }: { view?: ContextView }) {
  if (!view) return null;
  if (view.state === "Loading") return <div className="context-state" aria-live="polite">Memuat konteks...</div>;
  if (view.state !== "Exact" || !view.value) return <div className="context-state" role="status">{contextStateCopy(view.state)}</div>;
  const highlighted = highlightedContext(view.value);
  return <div className="context-block"><div><small>Kalimat</small><p>{highlighted ? <>{highlighted.before}<mark>{highlighted.target}</mark>{highlighted.after}</> : view.value.context}</p></div><dl><div><dt>Masalah</dt><dd>{view.value.targetText}</dd></div><div><dt>Saran</dt><dd>{view.value.suggestedReplacement}</dd></div></dl></div>;
}

async function openPreview(versionId: string | null, location: TextCorrectionProposal["pageLocation"]) {
  if (!versionId) return;
  const target = window.open("", "_blank"); if (target) target.opener = null;
  try { const blob = await apiFetchBlob(`/api/document-versions/${encodeURIComponent(versionId)}/preview`); const url = URL.createObjectURL(blob); const destination = `${url}${previewFragment(location)}`; if (target) target.location.href = destination; else window.open(destination, "_blank", "noopener,noreferrer"); window.setTimeout(() => URL.revokeObjectURL(url), 300_000); }
  catch { target?.close(); }
}
function commandMessage(value: unknown): string { return value instanceof ApiRequestError ? safeCommandMessage(value.status) : "Layanan sedang mengalami gangguan. Coba lagi."; }
function PageState({ title, message, retry }: { title: string; message: string; retry?: () => void }) { return <main className="page-shell narrow"><Link className="back-link" href="/">← Dokumen saya</Link><section className="panel page-state" aria-live="polite"><h1>{title}</h1><p>{message}</p>{retry && <button className="button secondary" type="button" onClick={retry}>Coba lagi</button>}</section></main>; }
