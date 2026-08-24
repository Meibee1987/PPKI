"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import { ApiRequestError, isApiRequestAborted } from "../lib/api";
import { listAuditFindings } from "../lib/audit-api";
import { findingDispositions, findingsQuery, fixModes, normalizeFindingFilters, severities, type AuditFindingPage, type AuditSummary, type FindingDisposition, type FindingFilters, type FixMode, type Severity } from "../lib/audit-contract";
import type { CanonicalAuditIdentity } from "../lib/canonical-audit-identity";
import { assertCanonicalFindingPage, createLatestFindingRequestGuard, findingRequestKey, hasFindingQuery } from "../lib/finding-list-model";
import { pageRange } from "../lib/findings-presentation";
import { DocumentPageLocation } from "./document-page-location";
import { FindingLocation } from "./finding-location";

type FindingFilterDraft = {
  search: string;
  severity: Severity | "";
  fixMode: FixMode | "";
  disposition: FindingDisposition | "";
  domain: string;
  pageSize: "10" | "25" | "50" | "100";
};

export function AuditFindingList({ identity, summary }: { identity: CanonicalAuditIdentity; summary: AuditSummary }) {
  const router = useRouter();
  const searchParams = useSearchParams();
  const queryKey = searchParams.toString();
  const filters = useMemo(() => normalizeFindingFilters(new URLSearchParams(queryKey)), [queryKey]);
  const [draft, setDraft] = useState(() => draftFrom(filters));
  const [loaded, setLoaded] = useState<{ page: AuditFindingPage; filters: FindingFilters }>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [reload, setReload] = useState(0);
  const requests = useRef(createLatestFindingRequestGuard());

  useEffect(() => setDraft(draftFrom(filters)), [filters]);

  useEffect(() => {
    const controller = new AbortController();
    const token = requests.current.begin(findingRequestKey(identity, filters));
    setLoading(true);
    setError("");
    listAuditFindings(identity.auditId, filters, controller.signal)
      .then(value => assertCanonicalFindingPage(identity, value))
      .then(value => {
        if (requests.current.isCurrent(token)) setLoaded({ page: value, filters });
      })
      .catch(value => {
        if (requests.current.isCurrent(token) && !isApiRequestAborted(value))
          setError(findingListError(value));
      })
      .finally(() => {
        if (requests.current.isCurrent(token)) setLoading(false);
      });
    return () => {
      controller.abort();
      requests.current.cancel(token);
    };
  }, [identity.auditId, identity.documentVersionId, filters, reload]);

  const navigate = (next: FindingFilters) => {
    router.push(`/audits/${encodeURIComponent(identity.routeAuditId)}?${findingsQuery(next)}`);
  };

  const apply = (event: FormEvent) => {
    event.preventDefault();
    const query = new URLSearchParams();
    if (draft.search.trim()) query.set("search", draft.search.trim());
    if (draft.severity) query.set("severity", draft.severity);
    if (draft.fixMode) query.set("fixMode", draft.fixMode);
    if (draft.disposition) query.set("disposition", draft.disposition);
    if (draft.domain.trim()) query.set("domain", draft.domain.trim());
    query.set("pageSize", draft.pageSize);
    navigate({ ...normalizeFindingFilters(query), page: 1 });
  };

  const visible = loaded?.page.auditId === identity.auditId && loaded.page.documentVersionId === identity.documentVersionId
    ? loaded : undefined;
  const filtered = hasFindingQuery(filters);

  return <section className="panel finding-log" aria-labelledby="finding-log-title" aria-busy={loading}>
    <div className="section-heading"><div><h2 id="finding-log-title">Daftar temuan audit</h2><p>Daftar ini dipaginasi dan difilter oleh backend; ringkasan audit di atas tidak berubah.</p></div></div>
    <form className="finding-filter-grid" role="search" onSubmit={apply}>
      <label className="finding-search">Cari kode atau elemen aturan<input type="search" maxLength={128} value={draft.search} onChange={event => setDraft({ ...draft, search: event.target.value })} placeholder="Contoh: PPKI-LAYOUT" /></label>
      <label>Keparahan<select value={draft.severity} onChange={event => setDraft({ ...draft, severity: event.target.value as Severity | "" })}><option value="">Semua</option>{severities.map(value => <option key={value} value={value}>{value}</option>)}</select></label>
      <label>Mode perbaikan<select value={draft.fixMode} onChange={event => setDraft({ ...draft, fixMode: event.target.value as FixMode | "" })}><option value="">Semua</option>{fixModes.map(value => <option key={value} value={value}>{value}</option>)}</select></label>
      <label>Status temuan<select value={draft.disposition} onChange={event => setDraft({ ...draft, disposition: event.target.value as FindingDisposition | "" })}><option value="">Semua</option>{findingDispositions.map(value => <option key={value} value={value}>{dispositionLabel(value)}</option>)}</select></label>
      <label>Domain<select value={draft.domain} onChange={event => setDraft({ ...draft, domain: event.target.value })}><option value="">Semua</option>{summary.domains.map(value => <option key={value.domain} value={value.domain}>{value.domain}</option>)}</select></label>
      <label>Per halaman<select value={draft.pageSize} onChange={event => setDraft({ ...draft, pageSize: event.target.value as FindingFilterDraft["pageSize"] })}><option value="10">10</option><option value="25">25</option><option value="50">50</option><option value="100">100</option></select></label>
      <div className="finding-filter-actions"><button className="button" type="submit">Terapkan</button><button className="button secondary" type="button" onClick={() => navigate({ page: 1, pageSize: 25 })} disabled={!filtered}>Hapus filter</button></div>
    </form>
    {loading && !visible && <div className="finding-list-state" role="status">Memuat halaman pertama temuan...</div>}
    {loading && visible && <p className="muted finding-refresh" role="status">Memperbarui daftar temuan; hasil sebelumnya tetap ditampilkan...</p>}
    {error && <div className="error-box" role="alert"><p>{error}</p><button className="text-button" type="button" onClick={() => setReload(value => value + 1)}>Coba lagi</button></div>}
    {visible && <FindingPageView identity={identity} summary={summary} filters={visible.filters} page={visible.page} navigate={navigate} />}
  </section>;
}

function FindingPageView({ identity, summary, filters, page, navigate }: { identity: CanonicalAuditIdentity; summary: AuditSummary; filters: FindingFilters; page: AuditFindingPage; navigate: (filters: FindingFilters) => void }) {
  const range = pageRange(page.page, page.pageSize, page.totalCount);
  if (summary.findingCount === 0)
    return <div className="finding-list-state empty-state"><h3>Audit ini tidak memiliki temuan</h3><p>Audit selesai tanpa temuan yang tersimpan.</p></div>;
  if (page.totalCount === 0)
    return <div className="finding-list-state empty-state"><h3>Tidak ada temuan yang cocok</h3><p>Ubah pencarian atau filter untuk melihat hasil lain. Ringkasan audit tetap menunjukkan seluruh temuan.</p></div>;
  if (page.items.length === 0)
    return <div className="finding-list-state empty-state"><h3>Halaman tidak tersedia</h3><p>Jumlah halaman berubah setelah filter diterapkan.</p><button className="button secondary" type="button" onClick={() => navigate({ ...filters, page: 1 })}>Kembali ke halaman pertama</button></div>;
  const detailQuery = findingsQuery(filters);
  return <>
    <p className="finding-range" aria-live="polite">Menampilkan {range.start}–{range.end} dari {page.totalCount} hasil{hasFindingQuery(filters) ? " yang cocok" : ""}.</p>
    <ol className="finding-log-list" start={range.start}>{page.items.map(item => <li key={item.id}><article>
      <header><div><span className={`severity severity-${item.severity.toLowerCase()}`}>{item.severity}</span><strong>{item.ruleCode}</strong><span className="domain-label">{item.domain}</span></div><DocumentPageLocation versionId={page.documentVersionId} value={item.pageLocation} /></header>
      <h3>{item.presentation.propertyLabel}</h3><p>{item.presentation.problem}</p>
      <dl><div><dt>Elemen</dt><dd>{item.element}</dd></div><div><dt>Mode perbaikan</dt><dd>{item.fixMode}</dd></div><div><dt>Lokasi</dt><dd><FindingLocation value={item.location} /></dd></div></dl>
      <Link className="button secondary" href={`/audits/${encodeURIComponent(identity.auditId)}/findings/${encodeURIComponent(item.id)}?${detailQuery}`}>Lihat detail</Link>
    </article></li>)}</ol>
    <nav className="pagination" aria-label="Navigasi halaman daftar temuan"><button className="button secondary" type="button" aria-label="Halaman temuan sebelumnya" disabled={page.page <= 1} onClick={() => navigate({ ...filters, page: page.page - 1 })}>Sebelumnya</button><span>Halaman {page.page} dari {range.totalPages}</span><button className="button secondary" type="button" aria-label="Halaman temuan berikutnya" disabled={page.page >= range.totalPages} onClick={() => navigate({ ...filters, page: page.page + 1 })}>Berikutnya</button></nav>
  </>;
}

function draftFrom(filters: FindingFilters): FindingFilterDraft {
  const pageSize = [10, 25, 50, 100].includes(filters.pageSize) ? String(filters.pageSize) as FindingFilterDraft["pageSize"] : "25";
  return { search: filters.search ?? "", severity: filters.severity ?? "", fixMode: filters.fixMode ?? "", disposition: filters.disposition ?? "", domain: filters.domain ?? "", pageSize };
}

function dispositionLabel(value: FindingDisposition): string {
  return value === "Resolved" ? "Selesai" : value === "Ignored" ? "Diabaikan" : "Perlu review";
}

function findingListError(value: unknown): string {
  return value instanceof ApiRequestError ? value.message : "Daftar temuan tidak dapat dimuat. Coba lagi.";
}
