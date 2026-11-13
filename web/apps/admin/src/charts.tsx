import { useState, type ReactNode } from "react";

/**
 * Small, dependency-free figures for the admin dashboards. Mark specs follow the data-viz method: columns at most
 * 24px wide with a 4px rounded top and a square base, 2px gaps, hairline solid gridlines, text in text tokens,
 * the series color only on the marks (--series-1, validated against the card surface).
 */

/** Stat tile: label, value (proportional figures), optional secondary line. */
export function StatTile({ label, value, sub, icon }: { label: string; value: ReactNode; sub?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="rounded-(--r-md) bg-s1 elev-1 p-4 min-w-0">
      <div className="flex items-center gap-1.5 text-[12px] text-mut">{icon}{label}</div>
      <div className="mt-1.5 text-[22px] font-semibold text-on leading-tight truncate">{value}</div>
      {sub && <div className="mt-1 text-[11.5px] text-dim truncate">{sub}</div>}
    </div>
  );
}

/** 1, 2 or 5 times a power of ten: clean axis steps. */
function niceStep(raw: number): number {
  const pow = 10 ** Math.floor(Math.log10(Math.max(raw, 1e-9)));
  const n = raw / pow;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow;
}

const compact = (v: number) => (v >= 10_000 ? `${(v / 1000).toFixed(v % 1000 === 0 ? 0 : 1)}K` : v.toLocaleString("en-US"));

export type Column = { key: string; label: string; value: number; detail?: string };

/**
 * Single-series column chart (no legend: the title names the series). Each column's slot is its hover / focus
 * target, with a tooltip showing the value first. Every value also appears in the table under the chart.
 */
export function ColumnChart({ data, title, unit = "", height = 180 }: { data: Column[]; title: string; unit?: string; height?: number }) {
  const [active, setActive] = useState<number | null>(null);
  const step = niceStep(Math.max(1, ...data.map((d) => d.value)) / 4);
  const max = step * Math.max(1, Math.ceil(Math.max(1, ...data.map((d) => d.value)) / step));
  const ticks = Array.from({ length: Math.round(max / step) + 1 }, (_, i) => i * step);
  const every = Math.max(1, Math.ceil(data.length / 10));
  const hovered = active === null ? null : data[active];

  return (
    <figure aria-label={title} className="m-0">
      <div className="flex gap-2">
        <div className="relative shrink-0 w-10 text-right text-[10.5px] text-dim tabular-nums" style={{ height }} aria-hidden>
          {ticks.map((t) => (
            <span key={t} className="absolute right-0" style={{ bottom: `${(t / max) * 100}%`, transform: "translateY(50%)" }}>{compact(t)}</span>
          ))}
        </div>
        <div className="relative flex-1 min-w-0" style={{ height }}>
          {ticks.map((t) => (
            <div key={t} className="absolute inset-x-0 h-px bg-white/[.07]" style={{ bottom: `${(t / max) * 100}%` }} aria-hidden />
          ))}
          <div className="absolute inset-0 flex items-end gap-[2px]" onMouseLeave={() => setActive(null)}>
            {data.map((d, i) => (
              <button key={d.key} type="button"
                aria-label={`${d.label}：${d.value.toLocaleString("en-US")}${unit}${d.detail ? `，${d.detail}` : ""}`}
                onMouseEnter={() => setActive(i)} onFocus={() => setActive(i)} onBlur={() => setActive(null)}
                className="relative h-full flex-1 min-w-0 flex items-end justify-center outline-none focus-visible:bg-white/[.04] rounded-sm">
                {d.value > 0 && (
                  <span className="block w-full max-w-[24px] rounded-t-[4px] transition-[filter]"
                    style={{ height: `${(d.value / max) * 100}%`, background: "var(--series-1)", filter: active === i ? "brightness(1.25)" : undefined }} />
                )}
              </button>
            ))}
          </div>
          {hovered && active !== null && (
            <div role="tooltip"
              className="pointer-events-none absolute z-10 -translate-x-1/2 px-2.5 py-1.5 rounded-md bg-s4 elev-2 whitespace-nowrap"
              style={{ left: `${((active + 0.5) / data.length) * 100}%`, bottom: `calc(${(hovered.value / max) * 100}% + 8px)` }}>
              <div className="text-[13px] font-semibold text-on">{hovered.value.toLocaleString("en-US")}{unit}</div>
              <div className="text-[11px] text-mut">{hovered.label}{hovered.detail && ` · ${hovered.detail}`}</div>
            </div>
          )}
        </div>
      </div>
      <div className="flex gap-[2px] pl-12 mt-1.5" aria-hidden>
        {data.map((d, i) => (
          <span key={d.key} className="flex-1 min-w-0 text-center text-[10.5px] text-dim tabular-nums truncate">{i % every === 0 ? d.label : ""}</span>
        ))}
      </div>
    </figure>
  );
}
