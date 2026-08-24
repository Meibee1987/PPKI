"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useCallback, useEffect, useId, useMemo, useRef, useState } from "react";
import { ApiRequestError, apiFetchBlob } from "../lib/api";
import { getAuditSummary, getStructuralFindingExcerpt, listAuditFindings } from "../lib/audit-api";
import { isTextCorrectionAnalysisTransitional, type AuditFinding, type AuditFindingPage, type AuditSummary, type StructuralFindingExcerpt } from "../lib/audit-contract";
import { auditProgressFromSummary, observeAuditProgress } from "../lib/audit-progress";
import { abbreviatedRuleSetHash, readinessPresentation, readinessStateLabel, scoreLabel } from "../lib/audit-readiness-presentation";
import { assertCanonicalSummary, canonicalIdentityFromCompletedBatch, canonicalIdentityFromRouteSummary, type CanonicalAuditIdentity } from "../lib/canonical-audit-identity";
import { createTextCorrectionBatch, getDocumentPreviewState, getTextCorrectionBatch, getTextCorrectionContext, listTextCorrections, submitTextCorrectionDecision } from "../lib/text-correction-api";
import type { CorrectionAction, CorrectionBatchStatus, DocumentPreviewState, TextCorrectionContext, TextCorrectionPage, TextCorrectionProposal } from "../lib/text-correction-contract";
import { automaticProgress, batchProgress, contextStateCopy, decisionLabel, highlightedContext, isTerminalBatch, pageLocationLabel, previewFragment, safeCommandMessage, scalarCount, validateManualReplacement } from "../lib/streamlined-audit-presentation";
import { ConfirmationDialog } from "./confirmation-dialog";
import { AuditFindingList } from "./audit-finding-list";
import { FindingLocation } from "./finding-location";

const PAGE_SIZE = 25;
type ContextView = { state: "Loading" | "Exact" | "Stale" | "Unsupported" | "Unavailable"; value?: TextCorrectionContext };
type StructuralExcerptView = { state: "Loading" | "Exact" | "Unavailable"; value?: StructuralFindingExcerpt };
type CorrectionFilter = "All" | "Undecided" | "Selected" | "Ignored" | "Problem";

export function StreamlinedAuditClient() {
  const routeAuditId = String(useParams().auditId);
  const [current, setCurrent] = useState<{ identity: CanonicalAuditIdentity; summary: AuditSummary }>();
  const summary = current?.summary;
  const canonicalAuditId = current?.identity.auditId;
  const [corrections, setCorrections] = useState<TextCorrectionPage>();
  const [manualFindings, setManualFindings] = useState<AuditFindingPage>();
  const [automaticFindings, setAutomaticFindings] = useState<AuditFindingPage>();
  const [proposalPage, setProposalPage] = useState(1);
  const [manualPage, setManualPage] = useState(1);
  const [automaticPage, setAutomaticPage] = useState(1);
  const [automaticExpanded, setAutomaticExpanded] = useState(false);
  const [filter, setFilter] = useState<CorrectionFilter>("All");
  const [contexts, setContexts] = useState<Record<string, ContextView>>({});
  const [structuralExcerpts, setStructuralExcerpts] = useState<Record<string, StructuralExcerptView>>({});
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
  const structuralExcerptRequests = useRef(new Map<string, AbortController>());
  const activeAuditId = useRef<string | undefined>(undefined);

  const loadCorrections = useCallback(async (signal?: AbortSignal) => {
    if (!canonicalAuditId) return;
    const requestedAuditId = canonicalAuditId;
    const value = await listTextCorrections(requestedAuditId, proposalPage, PAGE_SIZE, signal);
    if (signal?.aborted || activeAuditId.current !== requestedAuditId) return;
    setCorrections(value);
    setBatch(value.activeBatch ?? undefined);
  }, [canonicalAuditId, proposalPage]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setError(""); setCurrent(undefined); setCorrections(undefined); setManualFindings(undefined); setAutomaticFindings(undefined); setBatch(undefined); setContexts({}); setStructuralExcerpts({}); setManualPage(1); setAutomaticPage(1); setAutomaticExpanded(false);
    const load = async () => {
      try {
        const routeSummary = await getAuditSummary(routeAuditId, controller.signal);
        const identity = canonicalIdentityFromRouteSummary(routeAuditId, routeSummary);
        const canonicalSummary = identity.auditId === routeSummary.id
          ? routeSummary
          : await getAuditSummary(identity.auditId, controller.signal);
        if (controller.signal.aborted) return;
        activeAuditId.current = identity.auditId;
        setCurrent({ identity, summary: assertCanonicalSummary(identity, canonicalSummary) });
      } catch (value) { if (!controller.signal.aborted) setError(commandMessage(value)); }
      finally { if (!controller.signal.aborted) setLoading(false); }
    };
    void load();
    return () => controller.abort();
  }, [routeAuditId, reload]);

  useEffect(() => {
    if (!current || !["Queued", "Processing"].includes(current.summary.status)) return;
    const identity = current.identity;
    let latestSummary: AuditSummary | undefined;
    return observeAuditProgress({
      auditId: identity.auditId,
      initialStatus: current.summary.status,
      getStatus: async (auditId, signal) => {
        latestSummary = assertCanonicalSummary(identity, await getAuditSummary(auditId, signal));
        return auditProgressFromSummary(latestSummary);
      },
      onStatus: value => {
        if (value.status === "Completed" || !latestSummary) return;
        setCurrent(previous => previous?.identity.auditId === identity.auditId
          ? { identity, summary: latestSummary! }
          : previous);
      },
      onCompleted: async (_value, signal) => {
        const completed = assertCanonicalSummary(identity, await getAuditSummary(identity.auditId, signal));
        if (activeAuditId.current !== identity.auditId) return;
        setCurrent(previous => previous?.identity.auditId === identity.auditId
          ? { identity, summary: completed }
          : previous);
      },
      onUnavailable: () => setError("Status audit belum dapat diperbarui. Coba lagi."),
      shouldStopAfterError: value => value instanceof ApiRequestError && value.status === 401,
    });
  }, [canonicalAuditId, summary?.status]);

  useEffect(() => {
    if (summary?.status !== "Completed") return;
    const state = summary.automaticRemediation?.state;
    if (!state || ["NoAction", "Completed", "Failed", "Conflict"].includes(state)) return;
    const timer = setTimeout(() => setReload(value => value + 1), 1500);
    return () => clearTimeout(timer);
  }, [summary?.status, summary?.automaticRemediation?.state, reload]);

  useEffect(() => {
    if (!current || !isTextCorrectionAnalysisTransitional(current.summary.correctionAnalysis.state)) return;
    if (batch && isTerminalBatch(batch.state)) return;
    const identity = current.identity;
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined, active: AbortController | undefined;
    const poll = async () => {
      active = new AbortController();
      try {
        const value = assertCanonicalSummary(identity, await getAuditSummary(identity.auditId, active.signal));
        if (stopped) return;
        setCurrent(previous => previous?.identity.auditId === identity.auditId
          ? { identity, summary: value }
          : previous);
        if (isTextCorrectionAnalysisTransitional(value.correctionAnalysis.state))
          timer = setTimeout(poll, 1500);
      } catch (value) {
        if (!stopped && (value as { name?: string })?.name !== "AbortError") setError(commandMessage(value));
      }
    };
    timer = setTimeout(poll, 1500);
    return () => { stopped = true; active?.abort(); if (timer) clearTimeout(timer); };
  }, [canonicalAuditId, summary?.correctionAnalysis.state, batch?.state]);

  useEffect(() => {
    if (summary?.status !== "Completed") return;
    if (batch?.state === "Completed") return;
    if (summary.correctionAnalysis.state !== "Completed") return;
    const automatic = summary.automaticRemediation?.state;
    if (automatic && !["NoAction", "Completed"].includes(automatic)) return;
    const controller = new AbortController();
    const load = async () => {
      try { await loadCorrections(controller.signal); }
      catch (value) {
        if (controller.signal.aborted) return;
        setError(commandMessage(value));
      }
    };
    void load();
    return () => controller.abort();
  }, [summary?.status, summary?.automaticRemediation?.state, summary?.correctionAnalysis.state, batch?.state, loadCorrections]);

  useEffect(() => {
    if (summary?.status !== "Completed" || !canonicalAuditId) return;
    const controller = new AbortController();
    setManualFindings(undefined);
    listAuditFindings(canonicalAuditId, { disposition: "RequiresReview", page: manualPage, pageSize: 25 }, controller.signal)
      .then(setManualFindings).catch(() => { /* Optional legacy section fails closed. */ });
    return () => controller.abort();
  }, [canonicalAuditId, summary?.status, manualPage]);

  useEffect(() => {
    const automatic = summary?.automaticRemediationHistory;
    if (!automaticExpanded || !automatic || automatic.verifiedResolvedCount === 0) return;
    const controller = new AbortController();
    setAutomaticFindings(undefined);
    listAuditFindings(automatic.sourceAuditJobId, { disposition: "Resolved", automaticallyResolved: true, page: automaticPage, pageSize: 25 }, controller.signal)
      .then(setAutomaticFindings).catch(() => { /* Historical evidence remains optional and read-only. */ });
    return () => controller.abort();
  }, [automaticExpanded, automaticPage, summary?.automaticRemediationHistory]);

  useEffect(() => {
    if (!batch || isTerminalBatch(batch.state)) return;
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined, active: AbortController | undefined;
    const poll = async () => {
      active = new AbortController();
      try {
        const value = await getTextCorrectionBatch(batch.id, active.signal);
        if (stopped) return;
        setBatch(value);
        if (!isTerminalBatch(value.state)) timer = setTimeout(poll, 1500);
      }
      catch (value) { if (!stopped && (value as { name?: string })?.name !== "AbortError") setError(commandMessage(value)); }
    };
    timer = setTimeout(poll, 1500);
    return () => { stopped = true; active?.abort(); if (timer) clearTimeout(timer); };
  }, [batch?.id, batch?.state]);

  useEffect(() => {
    const versionId = batch?.resultDocumentVersionId;
    if (batch?.state !== "Completed" || !versionId) { setPreview(undefined); return; }
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
  }, [batch?.state, batch?.resultDocumentVersionId]);

  useEffect(() => {
    if (batch?.state !== "Completed" || !batch.reauditId || !current) { setFinalSummary(undefined); return; }
    if (current.identity.auditId === batch.reauditId && finalSummary?.id === batch.reauditId) return;
    const controller = new AbortController();
    const nextIdentity = canonicalIdentityFromCompletedBatch(current.identity, batch);
    getAuditSummary(nextIdentity.auditId, controller.signal)
      .then(value => {
        const canonicalSummary = assertCanonicalSummary(nextIdentity, value);
        activeAuditId.current = nextIdentity.auditId;
        setCorrections(undefined); setContexts({}); setManualFindings(undefined);
        setCurrent({ identity: nextIdentity, summary: canonicalSummary }); setFinalSummary(canonicalSummary);
      })
      .catch(value => { if (!controller.signal.aborted) setError(commandMessage(value)); });
    return () => controller.abort();
  }, [batch?.state, batch?.reauditId, batch?.resultDocumentVersionId, current?.identity.auditId, finalSummary?.id]);

  useEffect(() => () => { for (const controller of contextRequests.current.values()) controller.abort(); contextRequests.current.clear(); }, [canonicalAuditId]);
  useEffect(() => () => { for (const controller of structuralExcerptRequests.current.values()) controller.abort(); structuralExcerptRequests.current.clear(); }, [canonicalAuditId]);

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

  async function loadStructuralExcerpt(findingId: string) {
    if (!canonicalAuditId || structuralExcerpts[findingId]) return;
    const requestedAuditId = canonicalAuditId;
    structuralExcerptRequests.current.get(findingId)?.abort();
    const controller = new AbortController(); structuralExcerptRequests.current.set(findingId, controller);
    setStructuralExcerpts(value => ({ ...value, [findingId]: { state: "Loading" } }));
    try {
      const value = await getStructuralFindingExcerpt(requestedAuditId, findingId, controller.signal);
      if (activeAuditId.current !== requestedAuditId) return;
      setStructuralExcerpts(current => ({ ...current, [findingId]: { state: value.status, value } }));
    } catch (reason) {
      if ((reason as { name?: string })?.name !== "AbortError")
        setStructuralExcerpts(current => ({ ...current, [findingId]: { state: "Unavailable" } }));
    } finally { structuralExcerptRequests.current.delete(findingId); }
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
    if (!canonicalAuditId) return;
    const key = batchKey.current ?? crypto.randomUUID(); batchKey.current = key; setError("");
    try {
      const accepted = await createTextCorrectionBatch(canonicalAuditId, key);
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
    <AuditReadinessPanel summary={summary} />
    {summary.status === "Completed" && (!final || finalSummary) && <AuditSummaryPanel summary={summary} />}
    {summary.status === "Completed" && (!final || finalSummary) && <section className="summary-strip" aria-label={final ? "Ringkasan hasil akhir" : "Ringkasan hasil audit"}>
      <SummaryMetric label={final ? "Masalah tersisa" : "Masalah ditemukan"} value={summary.findingCount} />
      <SummaryMetric label="Diperbaiki otomatis" value={summary.automaticRemediationHistory?.verifiedResolvedCount ?? 0} />
      {!final && <SummaryMetric label="Perlu keputusan" value={corrections?.summary.undecidedCount ?? 0} />}
      <SummaryMetric label="Diabaikan" value={summary.findingDispositions.ignoredCount + (corrections?.summary.ignoredCount ?? 0)} />
      {final ? <SummaryMetric label="Diperbaiki admin" value={batch.verificationCounts.VerifiedResolved ?? 0} />
        : <SummaryMetric label="Masih perlu pemeriksaan" value={summary.findingDispositions.requiresReviewCount} />}
    </section>}
    {final && !finalSummary && <p className="muted" role="status">Memuat ringkasan versi akhir...</p>}
    {summary.status === "Completed" && (!final || finalSummary) && <AuditFindingList key={current.identity.auditId} identity={current.identity} summary={summary} />}

    {!final && summary.automaticRemediationHistory && summary.automaticRemediationHistory.verifiedResolvedCount > 0 && <section className="panel automatic-history" aria-labelledby="automatic-history-title">
      <button className="automatic-history-toggle" type="button" aria-expanded={automaticExpanded} onClick={() => setAutomaticExpanded(value => !value)}><span><strong id="automatic-history-title">Diperbaiki otomatis</strong><small>{summary.automaticRemediationHistory.verifiedResolvedCount} temuan dari audit sebelumnya telah diperbaiki dan diverifikasi.</small></span><span aria-hidden="true">{automaticExpanded ? "−" : "+"}</span></button>
      {automaticExpanded && !automaticFindings && <p className="muted" role="status">Memuat bukti perbaikan terverifikasi...</p>}
      {automaticExpanded && automaticFindings && <><ul className="evidence-card-list">{automaticFindings.items.map(item => <li key={item.id}><div><strong>{item.presentation.propertyLabel}</strong><span className="muted">{item.element} · {pageLocationLabel(item.pageLocation)}</span><FindingEvidence item={item} automatic /><span className="muted">Verifikasi: Diperbaiki dan tidak terdeteksi lagi</span></div></li>)}</ul><FindingPagination label="Navigasi riwayat perbaikan otomatis" page={automaticFindings.page} pageSize={automaticFindings.pageSize} totalCount={automaticFindings.totalCount} setPage={setAutomaticPage} /></>}
    </section>}

    {batch && <BatchPanel batch={batch} preview={preview} openFinal={() => void openPreview(batch.resultDocumentVersionId, null)} />}

    {!final && !corrections && <CorrectionAnalysisState state={summary.correctionAnalysis.state} />}

    {!final && corrections && <section className="correction-section" aria-labelledby="corrections-title">
      <div className="section-heading"><div><h2 id="corrections-title">Perbaikan bahasa</h2><p>{corrections.totalCount} usulan tersedia. Konteks dimuat hanya saat diminta.</p></div>
        <label className="compact-filter">Tampilkan<select value={filter} onChange={event => setFilter(event.target.value as CorrectionFilter)}><option value="All">Semua</option><option value="Undecided">Perlu keputusan</option><option value="Selected">Dipilih</option><option value="Ignored">Diabaikan</option><option value="Problem">Masih bermasalah</option></select></label>
      </div>
      <div className="correction-list">{visibleItems.map(proposal => <CorrectionCard key={proposal.id} proposal={proposal} context={contexts[proposal.id]} editing={editing === proposal.id} manualValue={manualValue} manualError={manualError} busy={busyProposal === proposal.id} previewReady={summary.documentRender.previewAvailable} onContext={() => void loadContext(proposal.id)} onUse={() => void choose(proposal, "UseSuggestion")} onEdit={() => void beginManual(proposal)} onIgnore={() => void choose(proposal, "Ignore")} onManualChange={setManualValue} onSave={() => void saveManual(proposal)} onCancel={() => { setEditing(undefined); setManualError(""); }} openPreview={() => void openPreview(corrections.documentVersionId, proposal.pageLocation)} />)}</div>
      <nav className="pagination" aria-label="Navigasi halaman usulan"><button className="button secondary" type="button" disabled={corrections.page <= 1} onClick={() => setProposalPage(value => value - 1)}>Sebelumnya</button><span>Halaman {corrections.page} dari {Math.max(1, Math.ceil(corrections.totalCount / corrections.pageSize))}</span><button className="button secondary" type="button" disabled={corrections.page * corrections.pageSize >= corrections.totalCount} onClick={() => setProposalPage(value => value + 1)}>Berikutnya</button></nav>
    </section>}

    {!final && readyCount > 0 && !batch && <aside className="batch-bar" aria-live="polite"><div><strong>{readyCount} perbaikan siap diterapkan</strong><span>Semua pilihan diterapkan ke satu versi baru.</span></div><button className="button" type="button" onClick={() => setConfirmBatch(true)}>Terapkan {readyCount} Perbaikan</button></aside>}
    {!final && corrections && readyCount === 0 && !batch && <aside className="batch-bar batch-disabled"><div><strong>Belum ada perbaikan siap diterapkan</strong><span>Pilih Gunakan Saran atau Edit Manual pada usulan.</span></div><button className="button" type="button" disabled>Terapkan Perbaikan</button></aside>}

    {manualFindings && manualFindings.totalCount > 0 && <section className="panel legacy-findings" aria-labelledby="remaining-findings-title">
      <div className="section-heading"><div><h2 id="remaining-findings-title">Masih perlu pemeriksaan</h2><p>{manualFindings.totalCount} temuan non-teks atau tanpa perbaikan otomatis yang aman.</p></div></div>
      <ul>{manualFindings.items.map(item => <ManualFindingCard key={item.id} item={item} excerpt={structuralExcerpts[item.id]} loadExcerpt={() => void loadStructuralExcerpt(item.id)} auditId={canonicalAuditId ?? routeAuditId} />)}</ul>
      <FindingPagination label="Navigasi temuan yang masih perlu pemeriksaan" page={manualFindings.page} pageSize={manualFindings.pageSize} totalCount={manualFindings.totalCount} setPage={setManualPage} />
    </section>}
    <ConfirmationDialog open={confirmBatch} title={`Terapkan ${readyCount} perbaikan?`} description={`Terapkan ${readyCount} perbaikan ke satu versi baru dokumen?`} confirmLabel="Terapkan" busy={Boolean(batch && !isTerminalBatch(batch.state))} onConfirm={createBatch} onClose={() => setConfirmBatch(false)} />
  </main>;
}

function ManualFindingCard({ item, excerpt, loadExcerpt, auditId }: { item: AuditFinding; excerpt?: StructuralExcerptView; loadExcerpt: () => void; auditId: string }) {
  return <li><div><strong>{item.presentation.propertyLabel}</strong><span className="muted">{item.element} · {item.domain} · {item.ruleCode}</span><span className="page-label">{pageLocationLabel(item.pageLocation)}</span><FindingLocation value={item.location} /><StructuralExcerpt view={excerpt} />{!excerpt && <button className="text-button excerpt-button" type="button" onClick={loadExcerpt}>Lihat bagian dokumen</button>}<FindingEvidence item={item} /><span className="muted">Resolusi: {item.resolutionState} · Review: {item.reviewState}</span></div><Link className="button secondary" href={`/audits/${encodeURIComponent(auditId)}/findings/${encodeURIComponent(item.id)}`}>Tinjau temuan / Perbaiki Manual</Link></li>;
}

function StructuralExcerpt({ view }: { view?: StructuralExcerptView }) {
  if (!view) return null;
  if (view.state === "Loading") return <div className="context-state" role="status">Memuat bagian dokumen...</div>;
  if (view.state !== "Exact" || !view.value?.excerpt) return <div className="context-state" role="status">Cuplikan dokumen tidak tersedia.</div>;
  const label = view.value.targetType === "Heading" ? "Teks pada dokumen"
    : view.value.targetType === "Section" ? "Cuplikan bagian dokumen" : "Cuplikan paragraf";
  return <div className="structural-excerpt"><small>{label}</small><blockquote>{view.value.excerpt}</blockquote></div>;
}

function FindingEvidence({ item, automatic = false }: { item: AuditFinding; automatic?: boolean }) {
  const evidence = item.presentation;
  return <div className="finding-evidence"><p><strong>Masalah</strong><span>{evidence.problem}</span></p><dl><div><dt>{evidence.beforeLabel}</dt><dd>{evidence.beforeValue ?? "Nilai aman tidak tersedia"}</dd></div><div><dt>{automatic ? "Setelah (terverifikasi)" : evidence.expectedLabel}</dt><dd>{evidence.expectedValue ?? "Nilai aman tidak tersedia"}</dd></div></dl>{evidence.evidenceState !== "Complete" && <small className="muted">Bukti aman tidak lengkap; nilai tidak diperkirakan atau dibuat.</small>}</div>;
}

function FindingPagination({ label, page, pageSize, totalCount, setPage }: { label: string; page: number; pageSize: number; totalCount: number; setPage: (update: (value: number) => number) => void }) {
  return <nav className="pagination" aria-label={label}><button className="button secondary" type="button" disabled={page <= 1} onClick={() => setPage(value => value - 1)}>Sebelumnya</button><span>Halaman {page} dari {Math.max(1, Math.ceil(totalCount / pageSize))}</span><button className="button secondary" type="button" disabled={page * pageSize >= totalCount} onClick={() => setPage(value => value + 1)}>Berikutnya</button></nav>;
}

function CorrectionAnalysisState({ state }: { state: AuditSummary["correctionAnalysis"]["state"] }) {
  if (isTextCorrectionAnalysisTransitional(state))
    return <section className="panel" role="status" aria-live="polite"><h2>Menyiapkan perbaikan bahasa...</h2><p>Saran sedang dianalisis. Halaman ini akan diperbarui otomatis.</p></section>;
  if (state === "Failed")
    return <section className="panel progress-failed" role="status"><h2>Perbaikan bahasa belum tersedia</h2><p>Analisis saran tidak dapat diselesaikan. Dokumen asli tetap aman.</p></section>;
  if (state === "Skipped")
    return <section className="panel" role="status"><h2>Perbaikan bahasa tidak tersedia</h2><p>Tidak ada analisis perbaikan bahasa untuk versi dokumen ini.</p></section>;
  return null;
}

function AuditProgress({ summary }: { summary: AuditSummary }) {
  if (summary.status !== "Completed") return <section className="panel progress-panel" aria-live="polite"><h2>Memeriksa dokumen...</h2><p>{summary.status === "Failed" ? "Audit tidak dapat diselesaikan." : summary.status === "Cancelled" ? "Audit dibatalkan." : "Audit sedang diproses."}</p></section>;
  if (!summary.automaticRemediation) return null;
  const failed = summary.automaticRemediation.state === "Failed" || summary.automaticRemediation.state === "Conflict";
  return <section className={`panel progress-panel${failed ? " progress-failed" : ""}`} aria-live="polite" aria-busy={["Pending", "Queued", "Processing", "ReauditPending"].includes(summary.automaticRemediation.state)}><h2>Progres dokumen</h2><ProgressList steps={automaticProgress(summary.automaticRemediation.state)} />{summary.automaticRemediation.state === "Completed" && <p className="success-copy">✓ {summary.automaticRemediation.verifiedResolvedCount} masalah format diperbaiki dan diverifikasi otomatis.</p>}{failed && <p>{summary.automaticRemediation.state === "Conflict" ? "Dokumen telah berubah. Muat ulang hasil audit." : "Perbaikan format otomatis tidak dapat diselesaikan. Dokumen asli tetap aman."}</p>}</section>;
}

function AuditReadinessPanel({ summary }: { summary: AuditSummary }) {
  const presentation = readinessPresentation(summary);
  return <section className={`panel readiness-panel readiness-${presentation.tone}`} aria-live="polite">
    <p className="eyebrow">Kesiapan review</p>
    <h2>{presentation.title}</h2>
    <p>{presentation.message}</p>
    {summary.readinessState === "NeedsFix" && <strong>{summary.blockingFindingCount} temuan penghambat</strong>}
  </section>;
}

function AuditSummaryPanel({ summary }: { summary: AuditSummary }) {
  return <section className="panel audit-summary-panel" aria-labelledby="authoritative-summary-title">
    <div className="section-heading"><div><h2 id="authoritative-summary-title">Ringkasan audit</h2><p>Semua nilai berasal dari audit canonical yang sama.</p></div></div>
    <div className="audit-summary-grid">
      <SummaryMetric label="Skor" value={scoreLabel(summary.score)} />
      <SummaryMetric label="Error" value={summary.errorCount} />
      <SummaryMetric label="Warning" value={summary.warningCount} />
      <SummaryMetric label="Info" value={summary.infoCount} />
      <SummaryMetric label="Temuan penghambat" value={summary.blockingFindingCount} />
      <SummaryMetric label="Aturan berlaku" value={summary.applicableRuleCount} />
      <SummaryMetric label="Versi profil" value={summary.profileVersionNo} />
      <SummaryMetric label="Status review" value={readinessStateLabel(summary.readinessState)} />
    </div>
    <dl className="audit-summary-metadata">
      <div><dt>Set aturan</dt><dd><code title={summary.resolvedRuleSetHash ?? undefined}>{abbreviatedRuleSetHash(summary.resolvedRuleSetHash)}</code></dd></div>
      <div><dt>Kebijakan kesiapan</dt><dd>{summary.readinessPolicyVersion ?? "Belum tersedia"}</dd></div>
    </dl>
  </section>;
}

function BatchPanel({ batch, preview, openFinal }: { batch: CorrectionBatchStatus; preview?: DocumentPreviewState; openFinal: () => void }) {
  const conflict = batch.state === "Conflict", failed = batch.state === "Failed";
  return <section className={`panel progress-panel${conflict || failed ? " progress-failed" : ""}`} aria-live="polite" aria-busy={!isTerminalBatch(batch.state)}><h2>{batch.state === "Completed" ? "Hasil Akhir" : "Menerapkan perbaikan"}</h2><ProgressList steps={batchProgress(batch, preview)} />{conflict && <p>Dokumen atau pilihan perbaikan telah berubah. Muat ulang hasil audit.</p>}{failed && <p>Perbaikan tidak dapat diterapkan. Dokumen asli tetap aman.</p>}{batch.state === "Completed" && <div className="final-actions">{preview?.previewAvailable ? <button className="button" type="button" onClick={openFinal}>Buka Dokumen Final</button> : <span>{preview?.state === "Failed" ? "Preview belum tersedia." : "Preview sedang dibuat..."}</span>}{batch.reauditId && <Link className="button secondary" href={`/audits/${encodeURIComponent(batch.reauditId)}`}>Lihat Temuan Tersisa</Link>}</div>}</section>;
}

function ProgressList({ steps }: { steps: ReturnType<typeof automaticProgress> }) { return <ol className="progress-list">{steps.map(item => <li key={item.label} className={`progress-${item.tone}`}><span aria-hidden="true">{item.tone === "done" ? "✓" : item.tone === "active" ? "…" : item.tone === "failed" ? "!" : "○"}</span><strong>{item.label}</strong><small>{item.status}</small></li>)}</ol>; }
function SummaryMetric({ label, value }: { label: string; value: string | number }) { return <article><strong>{value}</strong><span>{label}</span></article>; }

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
  return <div className="context-block"><div><small>Konteks</small><p>{highlighted ? <>{highlighted.before}<mark>{highlighted.target}</mark>{highlighted.after}</> : view.value.context}</p></div><p className="context-problem"><small>Masalah</small><span>Bagian yang ditandai memerlukan keputusan perbaikan bahasa.</span></p><dl><div><dt>Sebelum</dt><dd>{view.value.targetText}</dd></div><div><dt>Saran</dt><dd>{view.value.suggestedReplacement}</dd></div></dl></div>;
}

async function openPreview(versionId: string | null, location: TextCorrectionProposal["pageLocation"]) {
  if (!versionId) return;
  const target = window.open("", "_blank"); if (target) target.opener = null;
  try { const blob = await apiFetchBlob(`/api/document-versions/${encodeURIComponent(versionId)}/preview`); const url = URL.createObjectURL(blob); const destination = `${url}${previewFragment(location)}`; if (target) target.location.href = destination; else window.open(destination, "_blank", "noopener,noreferrer"); window.setTimeout(() => URL.revokeObjectURL(url), 300_000); }
  catch { target?.close(); }
}
function commandMessage(value: unknown): string { return value instanceof ApiRequestError ? safeCommandMessage(value.status) : "Layanan sedang mengalami gangguan. Coba lagi."; }
function PageState({ title, message, retry }: { title: string; message: string; retry?: () => void }) { return <main className="page-shell narrow"><Link className="back-link" href="/">← Dokumen saya</Link><section className="panel page-state" aria-live="polite"><h1>{title}</h1><p>{message}</p>{retry && <button className="button secondary" type="button" onClick={retry}>Coba lagi</button>}</section></main>; }
