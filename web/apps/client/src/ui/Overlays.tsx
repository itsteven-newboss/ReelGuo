import { Check, Heart, Sparkles, Star, X } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { useUi } from "../state/ui";

/** Bottom-centred toasts (UI-REFERENCE §1). */
export function ToastHost() {
  const toasts = useUi((s) => s.toasts);
  return (
    <div className="fixed inset-x-0 z-[80] flex flex-col items-center gap-2 pointer-events-none px-6" style={{ bottom: "calc(env(safe-area-inset-bottom, 0px) + 96px)" }}>
      {toasts.map((t) => (
        <div key={t.id} role="status" className="a-rise flex items-center gap-2.5 max-w-full bg-s4/95 backdrop-blur-md rounded-(--r-sm) pl-3.5 pr-4 py-2.5 elev-3">
          {t.icon === "ok" && <Check size={16} className="text-success shrink-0" />}
          {t.icon === "heart" && <Heart size={16} className="text-like fill-current shrink-0" />}
          {t.icon === "star" && <Star size={16} className="text-star fill-current shrink-0" />}
          {t.icon === "info" && <Sparkles size={15} className="text-primary shrink-0" />}
          <span className="text-[13px] text-on truncate">{t.msg}</span>
        </div>
      ))}
    </div>
  );
}

/** Mobile bottom sheet: dimmed backdrop, rounded top, grab handle, slides up (UI-REFERENCE §1). */
/** `layer` sets the stacking order: app-wide sheets (login, report) sit above pages and the feed's own sheets. */
export function Sheet({ open, onClose, children, height = "72vh", layer = "z-40" }: {
  open: boolean; onClose: () => void; children: ReactNode; height?: string; layer?: string;
}) {
  const [rendered, setRendered] = useState(open);
  useEffect(() => {
    if (open) {
      setRendered(true);
      return undefined;
    }
    const t = window.setTimeout(() => setRendered(false), 230);
    return () => window.clearTimeout(t);
  }, [open]);
  if (!rendered) return null;

  return (
    <div className={`fixed inset-0 ${layer}`}>
      <div className={`absolute inset-0 transition-opacity duration-200 ${open ? "opacity-100" : "opacity-0"}`} style={{ background: "rgba(0,0,0,.55)" }} onClick={onClose} />
      <div
        role="dialog"
        aria-modal="true"
        className="absolute bottom-0 inset-x-0 bg-s2 elev-3 overflow-hidden flex flex-col transition-transform duration-300"
        style={{
          height,
          paddingBottom: "env(safe-area-inset-bottom, 0px)",
          borderTopLeftRadius: "var(--r-lg)",
          borderTopRightRadius: "var(--r-lg)",
          transform: open ? "translateY(0)" : "translateY(100%)",
          transitionTimingFunction: "cubic-bezier(.2,0,0,1)",
          animation: open ? "rg-sheet-in .32s cubic-bezier(.2,0,0,1) both" : undefined,
        }}
      >
        <div className="pt-3 pb-1 flex justify-center shrink-0"><div className="w-8 h-1 rounded-full bg-dim/70" /></div>
        {children}
      </div>
    </div>
  );
}

export function CloseButton({ onClick }: { onClick: () => void }) {
  return (
    <button onClick={onClick} aria-label="关闭" className="w-8 h-8 rounded-full bg-s3 flex items-center justify-center text-mut pressable">
      <X size={14} />
    </button>
  );
}

/** Switch, 46×26 (UI-REFERENCE §12). */
export function Toggle({ on, onChange, label }: { on: boolean; onChange: (on: boolean) => void; label: string }) {
  return (
    <button role="switch" aria-checked={on} aria-label={label} onClick={() => onChange(!on)}
      className={`relative w-[46px] h-[26px] rounded-full transition-colors duration-200 shrink-0 ${on ? "bg-primary" : "bg-s4"}`}>
      <span className={`absolute top-[3px] w-5 h-5 rounded-full elev-1 transition-all duration-200 ${on ? "left-[23px] bg-on-primary" : "left-[3px] bg-mut"}`} />
    </button>
  );
}

/** Centred dialog: dimmed + blurred backdrop, 340 px card (UI-REFERENCE §1). */
export function Modal({ open, onClose, children, layer = "z-[55]", width = 340 }: {
  open: boolean; onClose: () => void; children: ReactNode; layer?: string; width?: number;
}) {
  if (!open) return null;
  return (
    <div className={`fixed inset-0 ${layer} flex items-center justify-center p-6`} role="dialog" aria-modal="true">
      <div className="absolute inset-0 bg-black/70 a-fade" onClick={onClose} />
      <div className="relative bg-[#141414] border border-white/[.1] rounded-[20px] elev-3 a-scale-in max-w-full" style={{ width }}>{children}</div>
    </div>
  );
}
