"use client";

import Link from "next/link";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { getAuditSummary, listAuditFindings } from "../lib/audit-api";
import { findingsQuery, fixModes, normalizeFindingFilters, severities, type AuditFindingPage, type AuditSummary, type FindingFilters } from "../lib/audit-contract";
import { findingGuidance, formatTimestamp, pageRange, scorePresentation } from "../lib/findings-presentation";
import { FindingLocation } from "./finding-location";
import { StatusBadge } from "./status-badge";

type DraftFilters = { severity: string; fixMode: string; domain: string; ruleCode: string; validationKey: string; pageSize: string };

export function AuditFindingsClient() {
  const auditId = String(useParams().auditId);
  const router = useRouter();
  const searchParams = useSearchParams();
  const queryKey = searchParams.toString();
  const filters = useMemo(() => normalizeFindingFilters(new URLSearchParams(queryKey)), [queryKey]);
  const [summary, setSummary] = useState<AuditSummary>();
  const [page, setPage] = useState<AuditFindingPage>();
  const [summaryError, setSummaryError] = useState("");
  const [findingsError, setFindingsError] = useState("");
  const [summaryLoading, setSummaryLoading] = useState(true);
  const [findingsLoading, setFindingsLoading] = useState(false);
  const [reload, setReload] = useState(0);
  const [copyStatus, setCopyStatus] = useState("");
  const [draft, setDraft] = useState<DraftFilters>(() => draftFrom(filters));

  useEffect(() => setDraft(draftFrom(filters)), [filters]);

  useEffect(() => {
    const controller = new AbortController();
    setSummaryLoading(true); setSummaryError("");
    getAuditSummary(auditId, controller.signal)
      .then(setSummary)
      .catch(error => { if (error?.name !== "AbortError") setSummaryError(safeError(error)); })
      .finally(() => { if (!controller.signal.aborted) setSummaryLoading(false); });
    return () => controller.abort();
  }, [auditId, reload]);

  useEffect(() => {
    if (summary?.status !== "Completed") { setPage(undefined); return; }
    const controller = new AbortController();
    setFindingsLoading(true); setFindingsError("");
    listAuditFindings(auditId, filters, controller.signal)
      .then(setPage)
      .catch(error => { if (error?.name !== "AbortError") setFindingsError(safeError(error)); })
      .finally(() => { if (!controller.signal.aborted) setFindingsLoading(false); });
    return () => controller.abort();
  }, [auditId, filters, summary?.status, reload]);

  const navigate = useCallback((next: FindingFilters) => {
    router.push(`/audits/${encodeURIComponent(auditId)}?${findingsQuery(next)}`);
  }, [auditId, router]);

  function applyFilters(event: FormEvent) {
    event.preventDefault();
    const query = new URLSearchParams();
    if (draft.severity) query.set("severity", draft.severity);
    if (draft.fixMode) query.set("fixMode", draft.fixMode);
    if (draft.domain.trim()) query.set("domain", draft.domain.trim());
    if (draft.ruleCode.trim()) query.set("ruleCode", draft.ruleCode.trim());
    if (draft.validationKey.trim()) query.set("validationKey", draft.validationKey.trim());
    query.set("pageSize", draft.pageSize);
    navigate({ ...normalizeFindingFilters(query), page: 1 });
  }

  async function copyHash() {
    if (!summary?.resolvedRuleSetHash) return;
    try { await navigator.clipboard.writeText(summary.resolvedRuleSetHash); setCopyStatus("Hash disalin."); }
    catch { setCopyStatus("Hash tidak dapat disalin."); }
  }

  if (summaryLoading) return <PageState title="Memuat hasil audit" message="Ringkasan audit sedang dimuat." busy />;
  if (summaryError) return <PageState title={summaryError.includes("tidak ditemukan") ? "Audit tidak ditemukan" : "Hasil audit tidak dapat dimuat"} message={summaryError} retry={() => setReload(value => value + 1)} />;
  if (!summary) return <PageState title="Hasil audit tidak tersedia" message="Respons audit tidak tersedia." retry={() => setReload(value => value + 1)} />;

  const score = scorePresentation(summary.scoreState, summary.score, summary.scorePolicyVersion);
  return (
    <main className="page-shell audit-page">
      <Link className="back-link" href="/">← Dokumen saya</Link>
      <header className="audit-header"><div><p className="eyebrow">Hasil audit historis</p><h1>Temuan audit</h1><p className="muted">Audit <span className="mono">{shortId(summary.id)}</span></p></div><StatusBadge status={summary.status} /></header>
      <section className="summary-grid" aria-label="Ringkasan audit">
        <SummaryCard label="Status" value={summary.status} detail={statusDescription(summary.status)} />
        <SummaryCard label="Aturan berlaku" value={String(summary.applicableRuleCount)} detail={`${summary.persistedFindingCount} temuan tersimpan`} />
        <SummaryCard label="Skor" value={score.title} detail={score.detail} wide />
        <SummaryCard label="Error" value={String(summary.severity.error)} detail="Pelanggaran prioritas tinggi" />
        <SummaryCard label="Peringatan" value={String(summary.severity.warning)} detail="Perlu ditinjau" />
        <SummaryCard label="Informasi" value={String(summary.severity.info)} detail="Catatan informatif" />
      </section>
      <section className="panel audit-metadata" aria-labelledby="audit-metadata-title">
        <div className="section-heading"><div><h2 id="audit-metadata-title">Snapshot audit</h2><p>Informasi berasal dari snapshot historis audit, bukan katalog aturan live.</p></div></div>
        <dl className="metadata-list">
          <div><dt>Jenis dokumen</dt><dd>{summary.documentKindSnapshot ?? "Tidak tersedia (audit historis)"}</dd></div>
          <div><dt>Mulai</dt><dd>{formatTimestamp(summary.startedAt)}</dd></div>
          <div><dt>Selesai</dt><dd>{formatTimestamp(summary.completedAt)}</dd></div>
          <div><dt>Mode perbaikan</dt><dd>Auto {summary.fixModes.auto} · Konfirmasi {summary.fixModes.confirm} · Manual {summary.fixModes.manual} · Laporan {summary.fixModes.report}</dd></div>
          <div className="metadata-wide"><dt>Hash set aturan</dt><dd className="hash-row"><code title={summary.resolvedRuleSetHash ?? undefined}>{summary.resolvedRuleSetHash ? compactHash(summary.resolvedRuleSetHash) : "Belum tersedia"}</code>{summary.resolvedRuleSetHash && <button className="text-button" type="button" onClick={copyHash}>Salin hash</button>}<span className="sr-status" aria-live="polite">{copyStatus}</span></dd></div>
        </dl>
        {summary.domains.length > 0 && <div className="domain-summary" aria-label="Jumlah temuan per domain">{summary.domains.map(item => <span className="count-chip" key={item.domain}>{item.domain}: <strong>{item.findingCount}</strong></span>)}</div>}
      </section>
      {summary.status === "Completed" ? <><FindingFiltersPanel draft={draft} setDraft={setDraft} domains={summary.domains.map(item => item.domain)} onSubmit={applyFilters} onClear={() => navigate({ page: 1, pageSize: 25 })} /><FindingsSection auditId={auditId} filters={filters} page={page} loading={findingsLoading} error={findingsError} retry={() => setReload(value => value + 1)} navigate={navigate} /></> : <AuditNonCompleted summary={summary} />}
    </main>
  );
}

function FindingFiltersPanel({ draft, setDraft, domains, onSubmit, onClear }: { draft: DraftFilters; setDraft: (value: DraftFilters) => void; domains: string[]; onSubmit: (event: FormEvent) => void; onClear: () => void }) {
  const update = (key: keyof DraftFilters, value: string) => setDraft({ ...draft, [key]: value });
  return <section className="panel" aria-labelledby="filter-title"><div className="section-heading"><div><h2 id="filter-title">Filter temuan</h2><p>Filter exact diproses oleh backend. Mengubah filter kembali ke halaman 1.</p></div><button className="text-button" type="button" onClick={onClear}>Hapus semua filter</button></div><form className="filter-grid" onSubmit={onSubmit}>
    <label>Keparahan<select value={draft.severity} onChange={event => update("severity", event.target.value)}><option value="">Semua</option>{severities.map(value => <option key={value}>{value}</option>)}</select></label>
    <label>Mode perbaikan<select value={draft.fixMode} onChange={event => update("fixMode", event.target.value)}><option value="">Semua</option>{fixModes.map(value => <option key={value}>{value}</option>)}</select></label>
    <label>Domain<input list="audit-domains" maxLength={128} value={draft.domain} onChange={event => update("domain", event.target.value)} placeholder="Sama persis" /><datalist id="audit-domains">{domains.map(value => <option key={value} value={value} />)}</datalist></label>
    <label>Kode aturan<input maxLength={128} value={draft.ruleCode} onChange={event => update("ruleCode", event.target.value)} placeholder="Sama persis" /></label>
    <label>Kunci validasi<input maxLength={256} value={draft.validationKey} onChange={event => update("validationKey", event.target.value)} placeholder="Sama persis" /></label>
    <label>Item per halaman<select value={draft.pageSize} onChange={event => update("pageSize", event.target.value)}><option value="10">10</option><option value="25">25</option><option value="50">50</option><option value="100">100</option></select></label>
    <button className="button filter-submit" type="submit">Terapkan filter</button>
  </form></section>;
}

function FindingsSection({ auditId, filters, page, loading, error, retry, navigate }: { auditId: string; filters: FindingFilters; page?: AuditFindingPage; loading: boolean; error: string; retry: () => void; navigate: (filters: FindingFilters) => void }) {
  const activeFilters = Boolean(filters.severity || filters.fixMode || filters.domain || filters.ruleCode || filters.validationKey);
  if (loading) return <section className="panel" aria-live="polite" aria-busy="true"><h2>Daftar temuan</h2><p>Memuat halaman temuan…</p></section>;
  if (error) return <section className="panel error-state" role="alert"><h2>Temuan tidak dapat dimuat</h2><p>{error}</p><button className="button secondary" onClick={retry}>Coba lagi</button></section>;
  if (!page) return null;
  const range = pageRange(page.page, page.pageSize, page.totalCount);
  if (page.items.length === 0) return <section className="panel empty-state"><h2>{activeFilters ? "Tidak ada temuan yang cocok" : "Tidak ada temuan"}</h2><p>{page.totalCount > 0 ? "Halaman pada URL sudah tidak tersedia. Kembali ke halaman pertama." : activeFilters ? "Ubah atau hapus filter untuk melihat temuan lain." : "Audit selesai tanpa temuan yang tersimpan."}</p>{page.totalCount > 0 && <button className="button secondary" onClick={() => navigate({ ...filters, page: 1 })}>Ke halaman pertama</button>}</section>;
  const query = findingsQuery(filters);
  return <section className="panel findings-panel" aria-labelledby="findings-title"><div className="section-heading"><div><h2 id="findings-title">Daftar temuan</h2><p>Menampilkan {range.start}–{range.end} dari {page.totalCount}. Urutan berasal dari backend.</p></div></div><ol className="findings-list" start={range.start}>{page.items.map(item => {
    const guidance = findingGuidance(item);
    return <li key={item.id}><article className="finding-item"><header><div className="finding-identifiers"><span className={`severity severity-${item.severity.toLowerCase()}`}>{item.severity}</span><strong className="rule-code" title={item.ruleCode}>{item.ruleCode}</strong><span className="domain-label">{item.domain}</span></div><FindingLocation value={item.location} /></header><h3>{guidance.title}</h3><p className="finding-summary">{guidance.issue}</p><div className="finding-plain-comparison"><div><span>Ditemukan</span><p>{guidance.issue}</p></div><div><span>Seharusnya</span><p>{guidance.expected}</p></div></div><footer><span>Status perbaikan: <strong>{guidance.repairStatus}</strong></span><Link className="button secondary" href={`/audits/${encodeURIComponent(auditId)}/findings/${encodeURIComponent(item.id)}?${query}`}>Lihat masalah dan cara memperbaiki</Link></footer></article></li>;
  })}</ol><nav className="pagination" aria-label="Navigasi halaman temuan"><button className="button secondary" disabled={page.page <= 1} onClick={() => navigate({ ...filters, page: page.page - 1 })}>Sebelumnya</button><span>Halaman {page.page} dari {range.totalPages}</span><button className="button secondary" disabled={page.page >= range.totalPages} onClick={() => navigate({ ...filters, page: page.page + 1 })}>Berikutnya</button></nav></section>;
}

function AuditNonCompleted({ summary }: { summary: AuditSummary }) { const failed = summary.status === "Failed"; return <section className={`panel ${failed ? "error-state" : "processing-state"}`} aria-live="polite"><h2>{failed ? "Audit gagal" : summary.status === "Cancelled" ? "Audit dibatalkan" : "Audit belum selesai"}</h2><p>{failed ? "Audit processing failed." : summary.status === "Queued" ? "Audit sedang menunggu untuk diproses." : summary.status === "Processing" ? "Audit sedang diproses. Muat ulang halaman nanti untuk melihat hasil." : "Audit tidak menghasilkan daftar temuan."}</p>{failed && summary.failureCode && <p>Kode: <code>{summary.failureCode}</code></p>}</section>; }
function SummaryCard({ label, value, detail, wide = false }: { label: string; value: string; detail: string; wide?: boolean }) { return <article className={`summary-card${wide ? " summary-wide" : ""}`}><span>{label}</span><strong>{value}</strong><small>{detail}</small></article>; }
function PageState({ title, message, busy = false, retry }: { title: string; message: string; busy?: boolean; retry?: () => void }) { return <main className="page-shell narrow"><Link className="back-link" href="/">← Dokumen saya</Link><section className="panel page-state" aria-live="polite" aria-busy={busy}><h1>{title}</h1><p>{message}</p>{retry && <button className="button secondary" onClick={retry}>Coba lagi</button>}</section></main>; }
function safeError(error: unknown): string { return error instanceof Error ? error.message : "Terjadi kesalahan saat memuat data."; }
function shortId(value: string): string { return `${value.slice(0, 8)}…${value.slice(-4)}`; }
function compactHash(value: string): string { return value.length > 24 ? `${value.slice(0, 12)}…${value.slice(-12)}` : value; }
function statusDescription(status: AuditSummary["status"]): string { return status === "Completed" ? "Hasil tersimpan" : status === "Processing" ? "Sedang diproses" : status === "Queued" ? "Menunggu proses" : status === "Failed" ? "Proses gagal" : "Dibatalkan"; }
function draftFrom(filters: FindingFilters): DraftFilters { return { severity: filters.severity ?? "", fixMode: filters.fixMode ?? "", domain: filters.domain ?? "", ruleCode: filters.ruleCode ?? "", validationKey: filters.validationKey ?? "", pageSize: String(filters.pageSize) }; }
