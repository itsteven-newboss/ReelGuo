import { unwrap } from "@reelguo/api-client";
import { Flag } from "lucide-react";
import { useState } from "react";
import { create } from "zustand";
import { api, type ReportReason, type ReportTarget } from "../api";
import { t } from "../i18n";
import { errorMessage } from "../state/auth";
import { closeOverlay, openOverlay } from "../state/nav";
import { useUi } from "../state/ui";
import { CloseButton, Modal, Sheet } from "../ui/Overlays";

type Target = { type: ReportTarget; id: number; label: string };

type ReportState = {
  target: Target | null;
  open: (target: Target) => void;
  close: () => void;
};

/** Anyone can report, signed in or not (UI-REFERENCE: 任何人都可以举报违规内容). */
export const useReport = create<ReportState>((set, get) => ({
  target: null,
  open: (target) => {
    if (get().target) return;
    set({ target });
    openOverlay(() => set({ target: null }));
  },
  close: () => {
    if (get().target) closeOverlay();
  },
}));

const REASONS: { value: ReportReason; label: string; hint?: string }[] = [
  { value: "minor", label: t("report.minor"), hint: t("report.priority") },
  { value: "non_consensual", label: t("report.nonConsensual"), hint: t("report.priority") },
  { value: "illegal", label: t("report.illegal") },
  { value: "copyright", label: t("report.copyright") },
  { value: "spam", label: t("report.spam") },
  { value: "other", label: t("report.other") },
];

export function ReportSheet({ wide }: { wide: boolean }) {
  const { target, close } = useReport();
  const toast = useUi((s) => s.toast);
  const [reason, setReason] = useState<ReportReason | null>(null);
  const [detail, setDetail] = useState("");
  const [busy, setBusy] = useState(false);
  const [shown, setShown] = useState<Target | null>(null);

  // Keep the last target while the sheet animates out; reset the form for a new one.
  if (target && target !== shown) {
    setShown(target);
    setReason(null);
    setDetail("");
  }

  const submit = async () => {
    if (!shown || !reason) return;
    setBusy(true);
    try {
      unwrap(await api.POST("/api/v1/reports", {
        body: { target_type: shown.type, target_id: shown.id, reason, detail: detail.trim() || null },
      }));
      close();
      toast(t("report.submitted"), "ok");
    } catch (e) {
      toast(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const body = shown && (
    <div className="p-5 flex flex-col min-h-0">
      <div className="flex items-center gap-2 mb-1">
        <span className="w-8 h-8 rounded-(--r-sm) bg-danger/15 text-danger flex items-center justify-center"><Flag size={15} /></span>
        <span className="text-[15px] font-bold text-on">{t("comments.report")}</span>
        <span className="ml-auto"><CloseButton onClick={close} /></span>
      </div>
      <p className="text-[11px] text-mut mb-3 truncate">{shown.label}</p>
      <div className="flex flex-col gap-1.5" role="radiogroup" aria-label={t("report.reason")}>
        {REASONS.map((r) => (
          <button key={r.value} role="radio" aria-checked={reason === r.value} onClick={() => setReason(r.value)}
            className={`flex items-center gap-2.5 h-10 px-3 rounded-(--r-sm) text-left text-[12.5px] pressable ${reason === r.value ? "bg-sec text-primary" : "bg-s4 text-on/85"}`}>
            <span className={`w-4 h-4 rounded-full border-2 shrink-0 ${reason === r.value ? "border-primary bg-primary" : "border-dim"}`} />
            <span className="flex-1 min-w-0 truncate">{r.label}</span>
            {r.hint && <span className="text-[10px] text-danger shrink-0">{r.hint}</span>}
          </button>
        ))}
      </div>
      <textarea value={detail} onChange={(e) => setDetail(e.target.value)} maxLength={1000} rows={3} placeholder={t("report.detailPlaceholder")}
        aria-label={t("report.detail")}
        className="mt-2.5 px-3 py-2 rounded-(--r-sm) bg-s4 text-[12.5px] text-on placeholder:text-mut outline-none resize-none focus:ring-2 focus:ring-[var(--p-primary)]" />
      <button onClick={() => void submit()} disabled={!reason || busy}
        className="mt-3 h-10 rounded-(--r-ctl) bg-primary text-on-primary text-[13px] font-bold pressable disabled:opacity-50">
        {busy ? t("common.submitting") : t("report.submit")}
      </button>
      <p className="text-[10.5px] text-dim mt-2.5 leading-relaxed">{t("report.note")}</p>
    </div>
  );

  return wide
    ? <Modal open={!!target} onClose={close} layer="z-[70]" width={380}>{body}</Modal>
    : <Sheet open={!!target} onClose={close} height="auto" layer="z-[70]">{body}</Sheet>;
}
