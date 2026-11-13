import { Img, Spinner } from "@reelguo/ui";
import { ChevronDown, ChevronLeft, ChevronRight, Play } from "lucide-react";
import { useState, type ReactNode } from "react";
import type { VideoCard } from "../api";
import { fmtAgo } from "../player/format";
import { href, Link } from "../router";

/*
 * Building blocks of the portal layout, measured on the reference site at 1440 px (desktop) and 390 px (phone):
 * content column `.rg-wrap` (13 px phone gutters; 96 px margins on desktop, at most 1440 px), plain section titles,
 * 3:4 drama cards in 7 columns (2 on phones), pill "more" links, numbered pagination and FAQ blocks.
 * Sizes in rem follow the reference's root size (14 px, 16 px on wide screens).
 */

export function Container({ children, className = "" }: { children: ReactNode; className?: string }) {
  return <div className={`rg-wrap ${className}`}>{children}</div>;
}

export type Crumb = { label: string; to?: string };

/** "首页 / 专题": 14 px, 60 % white, the current page in the accent colour. */
export function Breadcrumb({ items }: { items: Crumb[] }) {
  return (
    <nav aria-label="位置" className="flex flex-wrap items-center gap-[3px] text-[12px] lg:text-[14px] leading-[20px] text-white/60 pt-3 lg:pt-2">
      {items.map((c, i) => (
        <span key={i} className="flex items-center gap-[3px]">
          {i > 0 && <span>/</span>}
          {i === items.length - 1 || !c.to
            ? <span className={i === items.length - 1 ? "text-primary" : ""}>{c.label}</span>
            : <Link to={c.to} className="hover:text-white">{c.label}</Link>}
        </span>
      ))}
    </nav>
  );
}

/** Page heading: 26.25 px semibold (18 px on phones), optional line below. */
export function PageTitle({ children, sub, right }: { children: ReactNode; sub?: ReactNode; right?: ReactNode }) {
  return (
    <div className="mt-3 lg:mt-[18px]">
      <h1 className="text-[18px] lg:text-[1.875rem] font-semibold text-white leading-[1.4]">{children}</h1>
      {(sub || right) && (
        <div className="mt-2 lg:mt-3 flex items-start gap-4 text-[12px] lg:text-[14px] leading-[20px] text-white/65">
          <p className="flex-1 min-w-0 clamp-2 lg:clamp-1">{sub}</p>
          {right && <span className="shrink-0 font-medium">{right}</span>}
        </div>
      )}
    </div>
  );
}

/** Pill link at the right of a section title: "更多推荐 ›". */
export function MorePill({ label, to }: { label: string; to: string }) {
  return (
    <Link to={to} className="ml-auto h-8 lg:h-9 pl-4 pr-3 lg:pl-[13px] lg:pr-3 rounded-full bg-white/10 border border-white/10 hover:bg-white/15 text-[12px] text-white flex items-center gap-1 shrink-0">
      {label}<ChevronRight size={13} />
    </Link>
  );
}

/** "精选推荐 …… 更多推荐 ›": plain semibold title (16 px phone, 1.625 rem desktop). */
export function SectionTitle({ title, more, className = "" }: { title: ReactNode; more?: { label: string; to: string }; className?: string }) {
  return (
    <div className={`flex items-center gap-3 h-8 lg:h-9 mb-[14px] lg:mb-[15px] ${className}`}>
      <h2 className="text-[16px] lg:text-[1.625rem] font-semibold text-white leading-none">{title}</h2>
      {more && <MorePill {...more} />}
    </div>
  );
}

const DAY = 86_400_000;

/** Published within the last week: the "新片" (new) badge. */
export const isNew = (v: { published_at?: string | null }) => !!v.published_at && Date.now() - new Date(v.published_at).getTime() < 7 * DAY;

export const progressLabel = (v: Pick<VideoCard, "is_finished" | "episode_count" | "total_episodes">) =>
  v.is_finished ? `全${Math.max(v.episode_count, v.total_episodes)}集` : `更新至${v.episode_count}集`;

/** "44.4w" / "6272.2w", as on the reference's cards. */
export function fmtPlays(n: number): string {
  if (n >= 10_000) return `${(n / 10_000).toFixed(1).replace(/\.0$/, "")}w`;
  return String(n);
}

/** 原创 · 新片 badges: one pill, split when both show. */
export function CardBadges({ original, fresh, className = "" }: { original: boolean; fresh: boolean; className?: string }) {
  if (!original && !fresh) return null;
  const base = "h-[26px] lg:h-[23px] px-[10px] lg:px-[7.5px] text-[12px] font-medium text-[#111] flex items-center leading-none";
  return (
    <span className={`flex ${className}`}>
      {original && <span className={`${base} bg-[#ffd21c] ${fresh ? "rounded-l-full" : "rounded-full"}`}>原创</span>}
      {fresh && <span className={`${base} bg-[#89eed3] ${original ? "rounded-r-full" : "rounded-full"}`}>新片</span>}
    </span>
  );
}

/** 3:4 drama card: badges, score, play count and update chips on the cover; title and tag chips below. */
export function PosterCard({ v }: { v: VideoCard }) {
  const updated = v.last_episode_at ?? v.published_at;
  return (
    <div className="min-w-0">
      <Link to={href.video(v.id)} className="group block relative w-full overflow-hidden rounded-[12px] lg:rounded-[0.9rem] bg-[#242424]" style={{ aspectRatio: "3 / 4" }}>
        <div className="absolute inset-0 transition-transform duration-300 group-hover:scale-[1.04]"><Img src={v.cover} /></div>
        <CardBadges original={v.is_original} fresh={isNew(v)} className="absolute top-[6px] left-[6px]" />
        {v.score > 0 && (
          <span className="absolute top-[6px] right-[6px] h-[26px] lg:h-[23px] px-[7px] lg:px-[5px] rounded-[6px] lg:rounded-[4.5px] bg-black/70 backdrop-blur-sm text-[12px] font-bold text-[#ffd900] flex items-center tabular-nums">
            {v.score.toFixed(1)}分
          </span>
        )}
        <div className="absolute left-[6px] right-[6px] bottom-[6px] lg:bottom-[7px] flex items-end gap-1">
          <span className="h-5 lg:h-[23px] px-[6px] lg:px-[5px] rounded-[5px] lg:rounded-[4.5px] bg-black/55 lg:bg-black/80 text-[11px] lg:text-[12.25px] text-white flex items-center gap-1 shrink-0 tabular-nums">
            <Play size={10} className="fill-current" />{fmtPlays(v.hot)}
          </span>
          <span className="ml-auto min-w-0 h-6 px-[6px] rounded-[4px] lg:rounded-[4.8px] bg-black/80 text-[11px] lg:text-[12.25px] text-white flex items-center gap-1 whitespace-nowrap overflow-hidden">
            {updated && !v.is_finished && <i className="not-italic truncate">{fmtAgo(updated)}</i>}
            <span className="truncate">{progressLabel(v)}</span>
          </span>
        </div>
      </Link>
      <Link to={href.video(v.id)} className="block mt-[7px] lg:mt-2 text-[14px] font-medium leading-5 lg:leading-[19px] text-white/90 lg:text-[#e7e6e6] truncate hover:text-primary">{v.title}</Link>
      {v.tags.length > 0 && (
        <div className="mt-1 lg:mt-2 flex flex-wrap gap-x-[10px] lg:gap-x-[13px] gap-y-[13px] overflow-hidden max-h-5 lg:max-h-[55px]">
          {v.tags.slice(0, 4).map((t) => (
            <Link key={t} to={href.search(t)} className="h-5 lg:h-[21px] px-[2px] lg:px-[3px] rounded-[2px] lg:rounded-[3px] bg-white/5 text-[12px] leading-[18px] text-white/60 hover:text-white flex items-center">{t}</Link>
          ))}
        </div>
      )}
    </div>
  );
}

/** 2 columns on phones, 7 on desktop (gaps as measured). */
export const GRID = "grid grid-cols-2 lg:grid-cols-7 gap-x-[10px] lg:gap-x-[18px] gap-y-3 lg:gap-y-4";

export function PosterGrid({ items, className = "" }: { items: VideoCard[]; className?: string }) {
  return <div className={`${GRID} ${className}`}>{items.map((v) => <PosterCard key={v.id} v={v} />)}</div>;
}

export function PosterSkeleton({ count = 14 }: { count?: number }) {
  return (
    <div className={GRID}>
      {Array.from({ length: count }, (_, i) => (
        <div key={i}>
          <div className="w-full rounded-[12px] shimmer" style={{ aspectRatio: "3 / 4" }} />
          <div className="mt-2 h-4 w-2/3 rounded shimmer" />
          <div className="mt-2 h-4 w-1/2 rounded shimmer" />
        </div>
      ))}
    </div>
  );
}

/** Text tabs with an accent underline (series page 猜你喜欢 / 最新地址, rankings). */
export function UnderlineTabs<T extends string>({ tabs, active, onChange, big = false }: {
  tabs: readonly (readonly [T, string])[]; active: T; onChange: (t: T) => void; big?: boolean;
}) {
  return (
    <div role="tablist" className="flex gap-5 border-b border-white/[.08] overflow-x-auto no-bar">
      {tabs.map(([k, label]) => (
        <button key={k} role="tab" aria-selected={active === k} onClick={() => onChange(k)}
          className={`h-[37px] -mb-px border-b whitespace-nowrap ${active === k
            ? `border-[#ffd21c] text-[#ffd21c] font-semibold ${big ? "text-[15.75px]" : "text-[14px]"}`
            : "border-transparent text-[14px] text-white/50 hover:text-white"}`}>
          {label}
        </button>
      ))}
    </div>
  );
}

/** Pill tabs (channel page): the active one filled with the accent colour. */
export function PillTabs<T extends string>({ tabs, active, onChange }: { tabs: readonly (readonly [T, string])[]; active: T; onChange: (t: T) => void }) {
  return (
    <div role="tablist" className="flex gap-[11px] overflow-x-auto no-bar">
      {tabs.map(([k, label]) => (
        <button key={k} role="tab" aria-selected={active === k} onClick={() => onChange(k)}
          className={`h-8 lg:h-9 px-3 lg:px-2 min-w-[72px] rounded-[7.5px] text-[13px] lg:text-[14px] font-medium whitespace-nowrap ${active === k ? "bg-[#ffd321] text-black" : "bg-white/10 text-white hover:bg-white/15"}`}>
          {label}
        </button>
      ))}
    </div>
  );
}

/** "‹ 1 2 3 4 … 39 › 跳转至 [ ] 页" */
export function Pager({ page, pages, onPage }: { page: number; pages: number; onPage: (p: number) => void }) {
  const [jump, setJump] = useState("");
  if (pages <= 1) return null;
  const nums: (number | "…")[] = [];
  const add = (n: number) => { if (!nums.includes(n)) nums.push(n); };
  const from = Math.max(1, Math.min(page - 1, pages - 3));
  if (from > 1) { add(1); if (from > 2) nums.push("…"); }
  for (let n = from; n <= Math.min(pages, from + 3); n++) add(n);
  if (from + 3 < pages) { if (from + 4 < pages) nums.push("…"); add(pages); }
  const cell = "w-7 h-7 rounded-[4.5px] text-[12.25px] font-semibold flex items-center justify-center tabular-nums";
  const go = () => { const n = Number(jump); if (n >= 1 && n <= pages) { onPage(n); setJump(""); } };
  return (
    <nav aria-label="分页" className="flex flex-wrap items-center justify-center gap-[6px] py-3 my-3">
      <button aria-label="上一页" disabled={page <= 1} onClick={() => onPage(page - 1)} className={`${cell} text-white disabled:opacity-30`}><ChevronLeft size={12} /></button>
      {nums.map((n, i) => n === "…"
        ? <span key={`e${i}`} className="w-[18px] text-center text-[12.25px] font-semibold text-[#888]">...</span>
        : <button key={n} onClick={() => onPage(n)} aria-current={n === page ? "page" : undefined}
            className={`${cell} ${n === page ? "bg-[#ffd21c] text-black" : "text-white hover:bg-white/10"}`}>{n}</button>)}
      <button aria-label="下一页" disabled={page >= pages} onClick={() => onPage(page + 1)} className={`${cell} text-white disabled:opacity-30`}><ChevronRight size={12} /></button>
      <span className="flex items-center gap-[6px] ml-5">
        <button onClick={go} className="h-7 px-[10px] rounded-[4.5px] bg-[#ffd21c] text-black text-[12px] font-semibold">跳转至</button>
        <input value={jump} onChange={(e) => setJump(e.target.value.replace(/\D/g, ""))} onKeyDown={(e) => e.key === "Enter" && go()} inputMode="numeric" aria-label="页码"
          className="w-[57px] h-7 px-2 rounded-[4.5px] bg-[#23211f] border border-white/20 text-[12.25px] text-white outline-none focus:border-primary" />
        <span className="text-[12.25px] font-medium text-white">页</span>
      </span>
    </nav>
  );
}

/** FAQ block: "FAQ" heading, then question rows that open to show the answer. */
export function Faq({ items, open = false, small = false }: { items: { q: string; a: string }[]; open?: boolean; small?: boolean }) {
  return (
    <section className="mt-10 lg:mt-[54px]">
      <h2 className="text-[18px] lg:text-[1.75rem] font-semibold text-white leading-[34px] mb-[15px]">FAQ</h2>
      <div className="flex flex-col gap-[9px]">
        {items.map((f) => (
          <details key={f.q} open={open} className="group rounded-[10.5px] bg-white/5 border border-white/10">
            <summary className="list-none cursor-pointer h-11 px-4 flex items-center text-[13px] lg:text-[14px] font-medium text-white [&::-webkit-details-marker]:hidden">
              <span className="flex-1 min-w-0 truncate">{f.q}</span>
              <ChevronDown size={12} className="text-white/60 transition-transform group-open:rotate-180" />
            </summary>
            <div className="border-t border-white/5 px-4 py-[10px]">
              <p className={`${small ? "text-[12px] leading-[19px]" : "text-[13px] lg:text-[14px] leading-5"} text-white/50`}>{f.a}</p>
            </div>
          </details>
        ))}
      </div>
    </section>
  );
}

export function LoadMore({ hasMore, loading, onMore, endText = "— 已经到底啦 —" }: { hasMore: boolean; loading: boolean; onMore: () => void; endText?: string }) {
  return (
    <div className="flex justify-center py-8">
      {hasMore ? (
        <button onClick={onMore} disabled={loading}
          className="h-9 px-8 rounded-full bg-white/10 border border-white/10 hover:bg-white/15 text-[13px] text-white flex items-center gap-2 disabled:opacity-60">
          {loading ? <><Spinner size={16} />加载中…</> : "加载更多"}
        </button>
      ) : <span className="text-[12px] text-white/35">{endText}</span>}
    </div>
  );
}

/** Card surface used on the series page and 回家的路: #161616, 1 px white/10 border. */
export function Panel({ children, className = "" }: { children: ReactNode; className?: string }) {
  return <div className={`rounded-[9px] bg-[#161616] border border-white/10 ${className}`}>{children}</div>;
}

export function EmptyState({ title, sub }: { title: string; sub?: string }) {
  return (
    <div className="py-16 text-center">
      <div className="text-[15px] text-white">{title}</div>
      {sub && <div className="mt-1.5 text-[12.5px] text-white/40">{sub}</div>}
    </div>
  );
}
