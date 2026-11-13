import { ApiError } from "@reelguo/api-client";
import { useEffect, type ButtonHTMLAttributes, type ReactNode } from "react";

export function PageHeader({ title, sub, actions }: { title: string; sub?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="flex items-start gap-4 mb-5">
      <div className="flex-1 min-w-0">
        <h1 className="text-[20px] font-bold">{title}</h1>
        {sub && <div className="mt-1.5 text-[12.5px] text-mut leading-relaxed">{sub}</div>}
      </div>
      {actions && <div className="flex items-center gap-2 shrink-0">{actions}</div>}
    </div>
  );
}

export function Card({ children, className = "" }: { children: ReactNode; className?: string }) {
  return <div className={`rounded-(--r-md) bg-s1 elev-1 ${className}`}>{children}</div>;
}

type Variant = "primary" | "secondary" | "danger" | "ghost";

export function Button({ variant = "secondary", className = "", ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
  const styles: Record<Variant, string> = {
    primary: "bg-primary text-on-primary font-bold",
    secondary: "bg-s3 text-on hover:bg-s4",
    danger: "bg-danger/15 text-danger hover:bg-danger/25",
    ghost: "text-mut hover:text-on hover:bg-(--p-hover)",
  };
  return (
    <button
      type="button"
      {...props}
      className={`h-9 px-4 rounded-(--r-ctl) text-[13px] pressable disabled:opacity-50 disabled:pointer-events-none ${styles[variant]} ${className}`}
    />
  );
}

export function Field({ label, hint, children, className = "" }: { label: string; hint?: string; children: ReactNode; className?: string }) {
  return (
    <label className={`flex flex-col gap-1 text-[12px] text-mut ${className}`}>
      <span>{label}</span>
      {children}
      {hint && <span className="text-[11px] text-dim">{hint}</span>}
    </label>
  );
}

export const inputClass = "h-9 px-3 rounded-(--r-sm) bg-s3 text-on text-[13px] outline-none focus:ring-2 ring-primary placeholder:text-dim";

const STATUS: Record<string, [string, string]> = {
  draft: ["草稿", "bg-s4 text-mut"],
  published: ["已发布", "bg-success/20 text-success"],
  hidden: ["已下架", "bg-star/20 text-star"],
  removed: ["已删除", "bg-danger/20 text-danger"],
  uploading: ["待上传", "bg-s4 text-mut"],
  processing: ["处理中", "bg-pc text-on-pc"],
  ready: ["就绪", "bg-success/20 text-success"],
  failed: ["失败", "bg-danger/20 text-danger"],
  active: ["开放", "bg-success/20 text-success"],
  coming_soon: ["即将上线", "bg-pc text-on-pc"],
  disabled: ["已停用", "bg-danger/20 text-danger"],
  pending: ["待审核", "bg-pc text-on-pc"],
  approved: ["已通过", "bg-success/20 text-success"],
  rejected: ["已拒绝", "bg-danger/20 text-danger"],
  open: ["待处理", "bg-star/20 text-star"],
  resolved: ["已处理", "bg-success/20 text-success"],
  dismissed: ["已驳回", "bg-s4 text-mut"],
};

export function StatusBadge({ status }: { status: string }) {
  const [label, style] = STATUS[status] ?? [status, "bg-s4 text-mut"];
  return <span className={`inline-block px-2 py-0.5 rounded-md text-[11px] font-semibold whitespace-nowrap ${style}`}>{label}</span>;
}

export function ErrorText({ error }: { error: unknown }) {
  if (!error) return null;
  const message = error instanceof ApiError ? error.message : error instanceof Error ? error.message : "操作失败";
  return <p className="text-[12.5px] text-danger">{message}</p>;
}

/** US$4.99 for USD, 4.99 EUR otherwise. */
export function formatMoney(cents: number, currency: string) {
  const amount = (cents / 100).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  return currency.trim().toUpperCase() === "USD" ? `US$${amount}` : `${amount} ${currency.trim().toUpperCase()}`;
}

/** Centred dialog with a dimmed backdrop; Escape or the backdrop closes it. */
export function Dialog({ open, title, onClose, children, width = 640 }: { open: boolean; title: string; onClose: () => void; children: ReactNode; width?: number }) {
  useEffect(() => {
    if (!open) return undefined;
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-6" role="dialog" aria-modal="true" aria-label={title}>
      <div className="absolute inset-0 bg-black/60" onClick={onClose} />
      <div className="relative bg-s1 rounded-(--r-lg) elev-3 max-h-[90vh] max-w-full flex flex-col a-scale-in" style={{ width }}>
        <div className="shrink-0 flex items-center gap-3 px-5 h-12 border-b border-white/5">
          <span className="text-[14px] font-bold truncate">{title}</span>
          <button onClick={onClose} className="ml-auto text-[12.5px] text-mut hover:text-on">关闭</button>
        </div>
        <div className="flex-1 overflow-auto p-5">{children}</div>
      </div>
    </div>
  );
}

export function formatTime(iso: string | null | undefined) {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString("zh-CN", { hour12: false });
}
