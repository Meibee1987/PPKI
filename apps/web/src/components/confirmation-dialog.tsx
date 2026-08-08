"use client";

import { useEffect, useRef } from "react";

export function ConfirmationDialog({ open, title, description, confirmLabel, busy, onConfirm, onClose }: { open: boolean; title: string; description: string; confirmLabel: string; busy?: boolean; onConfirm: () => void; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const trigger = useRef<HTMLElement | null>(null);
  useEffect(() => {
    const element = dialog.current;
    if (!element) return;
    if (open && !element.open) { trigger.current = document.activeElement as HTMLElement | null; element.showModal(); }
    if (!open && element.open) element.close();
  }, [open]);
  useEffect(() => {
    const element = dialog.current; if (!element) return;
    const close = () => { onClose(); trigger.current?.focus(); };
    element.addEventListener("close", close); return () => element.removeEventListener("close", close);
  }, [onClose]);
  return <dialog ref={dialog} className="confirm-dialog" aria-labelledby="confirm-title" aria-describedby="confirm-description" onCancel={event => { event.preventDefault(); if (!busy) dialog.current?.close(); }}>
    <h2 id="confirm-title">{title}</h2><p id="confirm-description">{description}</p>
    <div className="dialog-actions"><button className="button secondary" type="button" disabled={busy} onClick={() => dialog.current?.close()}>Batal</button><button className="button" type="button" disabled={busy} onClick={onConfirm}>{busy ? "Memproses…" : confirmLabel}</button></div>
  </dialog>;
}
