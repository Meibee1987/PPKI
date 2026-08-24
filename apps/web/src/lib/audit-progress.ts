import type { AuditStatus, AuditSummary } from "./audit-contract.ts";
import type { AuditAccepted, DocumentAudit } from "./document-contract.ts";

export const auditPollingIntervalMilliseconds = 2_000;
export const maximumAuditPolls = 60;
export const maximumConsecutiveAuditPollFailures = 3;

export type AuditProgressSnapshot = Pick<AuditSummary,
  "id" | "status" | "score" | "errorCount" | "warningCount" | "infoCount">;

export function auditProgressFromDocument(value: DocumentAudit): AuditProgressSnapshot {
  return { id: value.id, status: value.status, score: value.score, errorCount: value.errorCount,
    warningCount: value.warningCount, infoCount: value.infoCount };
}

export function auditProgressFromAccepted(value: AuditAccepted): AuditProgressSnapshot {
  return { id: value.id, status: value.status, score: null, errorCount: 0, warningCount: 0, infoCount: 0 };
}

export function auditProgressFromSummary(value: AuditSummary): AuditProgressSnapshot {
  return { id: value.id, status: value.status, score: value.score, errorCount: value.errorCount,
    warningCount: value.warningCount, infoCount: value.infoCount };
}

export function isAuditPollingStatus(status: AuditStatus): boolean {
  return status === "Queued" || status === "Processing";
}

export function isAuditTerminalStatus(status: AuditStatus): boolean {
  return status === "Completed" || status === "Failed" || status === "Cancelled";
}

type Schedule = (callback: () => void, delayMilliseconds: number) => () => void;

type AuditProgressObserverOptions = {
  auditId: string;
  initialStatus: AuditStatus;
  getStatus: (auditId: string, signal: AbortSignal) => Promise<AuditProgressSnapshot>;
  onStatus: (value: AuditProgressSnapshot) => void;
  onCompleted?: (value: AuditProgressSnapshot, signal: AbortSignal) => Promise<void> | void;
  onUnavailable: () => void;
  shouldStopAfterError?: (error: unknown) => boolean;
  schedule?: Schedule;
  intervalMilliseconds?: number;
  maximumPolls?: number;
  maximumConsecutiveFailures?: number;
};

export function observeAuditProgress(options: AuditProgressObserverOptions): () => void {
  if (!isAuditPollingStatus(options.initialStatus)) return () => {};

  const schedule = options.schedule ?? defaultSchedule;
  const interval = options.intervalMilliseconds ?? auditPollingIntervalMilliseconds;
  const pollLimit = options.maximumPolls ?? maximumAuditPolls;
  const failureLimit = options.maximumConsecutiveFailures ?? maximumConsecutiveAuditPollFailures;
  let stopped = false, inFlight = false, pollCount = 0, consecutiveFailures = 0;
  let cancelScheduled: (() => void) | undefined;
  let active: AbortController | undefined;

  const stop = () => {
    stopped = true;
    cancelScheduled?.();
    active?.abort();
  };

  const scheduleNext = () => {
    if (!stopped) cancelScheduled = schedule(() => { void poll(); }, interval);
  };

  const poll = async () => {
    if (stopped || inFlight) return;
    inFlight = true;
    active = new AbortController();
    const signal = active.signal;
    try {
      pollCount += 1;
      const value = await options.getStatus(options.auditId, signal);
      if (stopped || signal.aborted) return;
      if (value.id !== options.auditId) {
        stopped = true;
        options.onUnavailable();
        return;
      }
      consecutiveFailures = 0;
      options.onStatus(value);
      if (value.status === "Completed") {
        stopped = true;
        try { await options.onCompleted?.(value, signal); }
        catch (error) { if (!signal.aborted && !isAbort(error)) options.onUnavailable(); }
        return;
      }
      if (isAuditTerminalStatus(value.status)) { stopped = true; return; }
      if (pollCount >= pollLimit) { stopped = true; options.onUnavailable(); return; }
    } catch (error) {
      if (stopped || signal.aborted || isAbort(error)) return;
      if (options.shouldStopAfterError?.(error)) { stopped = true; return; }
      consecutiveFailures += 1;
      if (consecutiveFailures >= failureLimit || pollCount >= pollLimit) {
        stopped = true;
        options.onUnavailable();
        return;
      }
    } finally {
      inFlight = false;
      if (active?.signal === signal) active = undefined;
    }
    scheduleNext();
  };

  scheduleNext();
  return stop;
}

function defaultSchedule(callback: () => void, delayMilliseconds: number): () => void {
  const timer = globalThis.setTimeout(callback, delayMilliseconds);
  return () => globalThis.clearTimeout(timer);
}

function isAbort(value: unknown): boolean {
  return Boolean(value && typeof value === "object" && "name" in value && value.name === "AbortError");
}
