"use client";

import { useCallback, useEffect, useId, useRef, useState } from "react";
import { ApiRequestError, isApiRequestAborted } from "../lib/api";
import type { CanonicalAuditIdentity } from "../lib/canonical-audit-identity";
import { assertCanonicalFindingReview, findingReviewIdentityKey, maximumFindingReviewReasonLength, validateFindingReviewReason } from "../lib/finding-review-model";
import { createLatestFindingRequestGuard } from "../lib/finding-list-model";
import { decideFindingReview, getFindingReview, requestFindingReview } from "../lib/remediation-api";
import type { FindingReview, FindingReviewRequestType } from "../lib/remediation-contract";
import { newIntentKey, reviewPresentation } from "../lib/remediation-presentation";
import { ConfirmationDialog } from "./confirmation-dialog";

type ReviewAction = "ManualReviewRequest" | "IgnoreRequest" | "IgnoreDecision";

export function FindingReviewActions({ identity, findingId, onChanged }: {
  identity: CanonicalAuditIdentity;
  findingId: string;
  onChanged: () => Promise<void>;
}) {
  const [review, setReview] = useState<FindingReview>();
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");
  const [reason, setReason] = useState("");
  const [reasonError, setReasonError] = useState("");
  const [pending, setPending] = useState<ReviewAction>();
  const [reload, setReload] = useState(0);
  const reasonId = useId();
  const reasonHelpId = `${reasonId}-help`;
  const reasonCountId = `${reasonId}-count`;
  const reasonErrorId = `${reasonId}-error`;
  const requestGuard = useRef(createLatestFindingRequestGuard());
  const commandController = useRef<AbortController | undefined>(undefined);
  const commandInFlight = useRef(false);
  const idempotencyKey = useRef<string | undefined>(undefined);
  const activeIdentity = findingReviewIdentityKey(identity, findingId);
  const activeIdentityRef = useRef(activeIdentity);
  activeIdentityRef.current = activeIdentity;

  const installReview = useCallback((value: FindingReview) => {
    setReview(assertCanonicalFindingReview(identity, findingId, value));
  }, [identity.auditId, identity.documentVersionId, findingId]);

  useEffect(() => {
    const controller = new AbortController();
    const token = requestGuard.current.begin(activeIdentity);
    commandController.current?.abort();
    commandInFlight.current = false;
    idempotencyKey.current = undefined;
    setReview(undefined); setLoading(true); setBusy(false); setError(""); setSuccess("");
    setReason(""); setReasonError(""); setPending(undefined);
    getFindingReview(identity.auditId, findingId, controller.signal)
      .then(value => assertCanonicalFindingReview(identity, findingId, value))
      .then(value => { if (requestGuard.current.isCurrent(token)) setReview(value); })
      .catch(value => {
        if (requestGuard.current.isCurrent(token) && !isApiRequestAborted(value))
          setError(reviewActionError(value));
      })
      .finally(() => { if (requestGuard.current.isCurrent(token)) setLoading(false); });
    return () => { controller.abort(); commandController.current?.abort(); requestGuard.current.cancel(token); };
  }, [activeIdentity, identity.auditId, identity.documentVersionId, findingId, reload]);

  function prepare(action: ReviewAction) {
    const validation = validateFindingReviewReason(reason);
    setReasonError(validation ?? "");
    if (validation || busy || commandInFlight.current) return;
    idempotencyKey.current = newIntentKey();
    setPending(action);
  }

  async function submit() {
    const validation = validateFindingReviewReason(reason);
    setReasonError(validation ?? "");
    if (validation || !pending || !review || !idempotencyKey.current || commandInFlight.current) return;
    const commandIdentity = activeIdentity;
    const controller = new AbortController();
    commandController.current?.abort(); commandController.current = controller;
    commandInFlight.current = true; setBusy(true); setError(""); setSuccess("");
    try {
      let value: FindingReview;
      if (pending === "IgnoreDecision") {
        if (!review.reviewCaseId || !review.permissions.canDecide || !review.allowedDecisions.includes("Ignore")) return;
        value = await decideFindingReview(review.reviewCaseId, "Ignore", reason, idempotencyKey.current, controller.signal);
      } else {
        if (!review.permissions.canRequestReview) return;
        const disposition: FindingReviewRequestType = pending === "ManualReviewRequest" ? "ManualRemediation" : "Ignore";
        value = await requestFindingReview(identity.auditId, findingId, disposition, reason, idempotencyKey.current, controller.signal);
      }
      if (controller.signal.aborted || activeIdentityRef.current !== commandIdentity) return;
      installReview(value);
      setSuccess(pending === "IgnoreDecision" ? "Finding ditandai Ignored. Status ini bukan VerifiedResolved." : "Permintaan review tersimpan.");
      setReason(""); setPending(undefined); idempotencyKey.current = undefined;
      try { await onChanged(); }
      catch (refreshError) {
        if (!isApiRequestAborted(refreshError))
          setError("Review tersimpan, tetapi ringkasan belum dapat dimuat ulang. Muat ulang state untuk melihat data terbaru.");
      }
    } catch (value) {
      if (controller.signal.aborted || activeIdentityRef.current !== commandIdentity || isApiRequestAborted(value)) return;
      if (value instanceof ApiRequestError && value.status === 409) {
        setPending(undefined); idempotencyKey.current = undefined; setReload(current => current + 1);
      }
      setError(reviewActionError(value));
    } finally {
      if (activeIdentityRef.current === commandIdentity) { commandInFlight.current = false; setBusy(false); }
      if (commandController.current === controller) commandController.current = undefined;
    }
  }

  if (loading) return <section className="drawer-review-actions" aria-busy="true" aria-live="polite"><h3>Review manual dan Ignore</h3><p>Memuat izin workflow...</p></section>;
  if (!review) return <section className="drawer-review-actions"><h3>Review manual dan Ignore</h3><div className="error-box" role="alert"><p>{error || "Status review tidak tersedia."}</p><button className="text-button" type="button" onClick={() => setReload(value => value + 1)}>Coba lagi</button></div></section>;

  const reviewView = reviewPresentation(review.reviewState);
  const canRequest = review.permissions.canRequestReview;
  const canDecideIgnore = review.permissions.canDecide && review.allowedDecisions.includes("Ignore");
  const latestEvent = review.events.length ? review.events[review.events.length - 1] : undefined;
  return <section className="drawer-review-actions" aria-labelledby="drawer-review-actions-title">
    <h3 id="drawer-review-actions-title">Review manual dan Ignore</h3>
    <p><strong>Status authoritative:</strong> {reviewView.label}. {reviewView.explanation}</p>
    {latestEvent && <div className="review-latest"><small>Event review terakhir</small><strong>{latestEvent.eventType}</strong>{latestEvent.note && <p>{latestEvent.note}</p>}<time dateTime={latestEvent.createdAt}>{new Date(latestEvent.createdAt).toLocaleString("id-ID")}</time></div>}
    {error && <p className="error-box" role="alert">{error}</p>}
    {success && <p className="success-box" role="status">{success}</p>}
    {(canRequest || canDecideIgnore) && <div className="review-reason-field"><label htmlFor={reasonId}>Alasan wajib<textarea id={reasonId} value={reason} maxLength={maximumFindingReviewReasonLength} disabled={busy} aria-invalid={Boolean(reasonError)} aria-describedby={`${reasonHelpId} ${reasonCountId}${reasonError ? ` ${reasonErrorId}` : ""}`} aria-errormessage={reasonError ? reasonErrorId : undefined} onChange={event => { setReason(event.target.value); if (reasonError) setReasonError(""); }} /></label><div className="note-meta"><span id={reasonHelpId}>Teks biasa satu baris; maksimum 1.000 karakter. Alasan tidak disimpan di URL.</span><output id={reasonCountId} aria-label="Jumlah karakter alasan">{reason.length}/1000</output></div>{reasonError && <p id={reasonErrorId} className="error-box" role="alert">{reasonError}</p>}</div>}
    {canRequest && <div className="drawer-review-buttons" aria-label="Tindakan review yang diizinkan server"><button className="button secondary" type="button" disabled={busy} onClick={() => prepare("ManualReviewRequest")}>Tandai untuk review manual</button><button className="button danger" type="button" disabled={busy} onClick={() => prepare("IgnoreRequest")}>Ajukan Ignore</button></div>}
    {canDecideIgnore && <div className="drawer-review-buttons"><button className="button danger" type="button" disabled={busy} onClick={() => prepare("IgnoreDecision")}>Konfirmasi Ignore</button></div>}
    {!canRequest && !canDecideIgnore && <p className="muted">Tidak ada tindakan ManualReview atau Ignore yang diizinkan oleh workflow saat ini.</p>}
    <p className="ignore-warning">Ignore adalah keputusan administratif, bukan bukti perbaikan teknis. Finding Blocking tetap menghalangi ReadyForReview sampai backend memiliki bukti VerifiedResolved.</p>
    <ConfirmationDialog open={Boolean(pending)} title={confirmationTitle(pending)} description={confirmationDescription(pending)} confirmLabel="Simpan dengan alasan" busy={busy} onConfirm={() => void submit()} onClose={() => { if (!busy) { setPending(undefined); idempotencyKey.current = undefined; } }} />
  </section>;
}

function confirmationTitle(action?: ReviewAction): string {
  return action === "ManualReviewRequest" ? "Tandai untuk review manual?" : action === "IgnoreDecision" ? "Konfirmasi Ignore?" : "Ajukan Ignore?";
}

function confirmationDescription(action?: ReviewAction): string {
  return action === "ManualReviewRequest"
    ? "Alasan akan disimpan sebagai event review immutable. Status teknis finding tidak dianggap selesai."
    : "Alasan akan disimpan sebagai event review immutable. Ignore tidak berarti VerifiedResolved dan tidak menghapus blocker.";
}

function reviewActionError(value: unknown): string {
  if (!(value instanceof ApiRequestError)) return "Tindakan review tidak dapat diproses. Tidak ada detail internal yang ditampilkan.";
  if (value.status === 403) return "Anda tidak memiliki izin untuk workflow review internal ini.";
  if (value.status === 404) return "Finding atau status review tidak lagi tersedia.";
  if (value.status === 409) return "Status berubah di tempat lain. State authoritative telah dimuat ulang; periksa lalu konfirmasi lagi.";
  if (value.status === 400) return "Alasan atau transisi review tidak valid.";
  return value.message;
}
