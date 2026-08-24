import assert from "node:assert/strict";
import test from "node:test";
import { observeAuditProgress, type AuditProgressSnapshot } from "./audit-progress.ts";

const auditId = "11111111-1111-4111-8111-111111111111";
const snapshot = (status: AuditProgressSnapshot["status"]): AuditProgressSnapshot =>
  ({ id: auditId, status, score: status === "Completed" ? 90 : null, errorCount: 1, warningCount: 2, infoCount: 3 });

function scheduler() {
  const tasks: { callback: () => void; cancelled: boolean }[] = [];
  return {
    schedule(callback: () => void) {
      const task = { callback, cancelled: false }; tasks.push(task);
      return () => { task.cancelled = true; };
    },
    runNext() {
      const task = tasks.find(value => !value.cancelled);
      if (!task) throw new Error("No scheduled poll.");
      task.cancelled = true; task.callback();
    },
    activeCount: () => tasks.filter(value => !value.cancelled).length,
    activeCallback: () => tasks.find(value => !value.cancelled)?.callback,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

const flush = () => new Promise<void>(resolve => setImmediate(resolve));

test("Queued audit schedules status polling", () => {
  const clock = scheduler();
  const stop = observeAuditProgress({ auditId, initialStatus: "Queued", getStatus: async () => snapshot("Queued"),
    onStatus: () => {}, onUnavailable: () => {}, schedule: clock.schedule });
  assert.equal(clock.activeCount(), 1); stop();
});

test("Processing response becomes visible and schedules the next poll", async () => {
  const clock = scheduler(); const visible: string[] = [];
  const stop = observeAuditProgress({ auditId, initialStatus: "Queued", getStatus: async () => snapshot("Processing"),
    onStatus: value => visible.push(value.status), onUnavailable: () => {}, schedule: clock.schedule });
  clock.runNext(); await flush();
  assert.deepEqual(visible, ["Processing"]); assert.equal(clock.activeCount(), 1); stop();
});

test("Completed response is visible, refreshes canonical data, and stops polling", async () => {
  const clock = scheduler(); const visible: string[] = []; let refreshed = 0;
  observeAuditProgress({ auditId, initialStatus: "Processing", getStatus: async () => snapshot("Completed"),
    onStatus: value => visible.push(value.status), onCompleted: () => { refreshed += 1; },
    onUnavailable: () => {}, schedule: clock.schedule });
  clock.runNext(); await flush();
  assert.deepEqual(visible, ["Completed"]); assert.equal(refreshed, 1); assert.equal(clock.activeCount(), 0);
});

test("Failed response is visible and stops polling", async () => {
  const clock = scheduler(); const visible: string[] = [];
  observeAuditProgress({ auditId, initialStatus: "Processing", getStatus: async () => snapshot("Failed"),
    onStatus: value => visible.push(value.status), onUnavailable: () => {}, schedule: clock.schedule });
  clock.runNext(); await flush();
  assert.deepEqual(visible, ["Failed"]); assert.equal(clock.activeCount(), 0);
});

test("already Completed audit does not poll unnecessarily", () => {
  const clock = scheduler(); let requests = 0;
  observeAuditProgress({ auditId, initialStatus: "Completed", getStatus: async () => { requests += 1; return snapshot("Completed"); },
    onStatus: () => {}, onUnavailable: () => {}, schedule: clock.schedule });
  assert.equal(clock.activeCount(), 0); assert.equal(requests, 0);
});

test("cleanup aborts an outstanding request", async () => {
  const clock = scheduler(); const pending = deferred<AuditProgressSnapshot>(); let signal: AbortSignal | undefined;
  const stop = observeAuditProgress({ auditId, initialStatus: "Queued", getStatus: async (_id, value) => { signal = value; return pending.promise; },
    onStatus: () => {}, onUnavailable: () => {}, schedule: clock.schedule });
  clock.runNext(); await Promise.resolve(); stop();
  assert.equal(signal?.aborted, true); pending.resolve(snapshot("Processing")); await flush();
});

test("AbortError is never presented as an unavailable user-facing failure", async () => {
  const clock = scheduler(); let unavailable = 0;
  observeAuditProgress({ auditId, initialStatus: "Queued", getStatus: async () => { throw new DOMException("Aborted", "AbortError"); },
    onStatus: () => {}, onUnavailable: () => { unavailable += 1; }, schedule: clock.schedule });
  clock.runNext(); await flush();
  assert.equal(unavailable, 0); assert.equal(clock.activeCount(), 0);
});

test("authentication errors can stop observation without a user-facing polling failure", async () => {
  const clock = scheduler(); let unavailable = 0;
  const unauthorized = Object.assign(new Error("safe auth redirect already started"), { status: 401 });
  observeAuditProgress({ auditId, initialStatus: "Queued", getStatus: async () => { throw unauthorized; },
    onStatus: () => {}, onUnavailable: () => { unavailable += 1; }, schedule: clock.schedule,
    shouldStopAfterError: value => value === unauthorized });
  clock.runNext(); await flush();
  assert.equal(unavailable, 0); assert.equal(clock.activeCount(), 0);
});

test("a scheduled callback cannot produce overlapping requests", async () => {
  const clock = scheduler(); const pending = deferred<AuditProgressSnapshot>(); let requests = 0;
  const stop = observeAuditProgress({ auditId, initialStatus: "Queued", getStatus: async () => { requests += 1; return pending.promise; },
    onStatus: () => {}, onUnavailable: () => {}, schedule: clock.schedule });
  const callback = clock.activeCallback(); assert.ok(callback); callback(); callback(); await Promise.resolve();
  assert.equal(requests, 1); pending.resolve(snapshot("Processing")); await flush(); stop();
});

test("late response after cleanup cannot replace newer state", async () => {
  const clock = scheduler(); const pending = deferred<AuditProgressSnapshot>(); const visible: string[] = [];
  const stop = observeAuditProgress({ auditId, initialStatus: "Queued", getStatus: async () => pending.promise,
    onStatus: value => visible.push(value.status), onUnavailable: () => {}, schedule: clock.schedule });
  clock.runNext(); await Promise.resolve(); stop(); pending.resolve(snapshot("Completed")); await flush();
  assert.deepEqual(visible, []);
});

test("three consecutive transient failures pause bounded polling with one safe notification", async () => {
  const clock = scheduler(); let unavailable = 0;
  observeAuditProgress({ auditId, initialStatus: "Queued", getStatus: async () => { throw new Error("network internals"); },
    onStatus: () => {}, onUnavailable: () => { unavailable += 1; }, schedule: clock.schedule });
  for (let attempt = 0; attempt < 3; attempt += 1) { clock.runNext(); await flush(); }
  assert.equal(unavailable, 1); assert.equal(clock.activeCount(), 0);
});
