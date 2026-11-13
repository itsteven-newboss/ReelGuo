import { Img } from "@reelguo/ui";
import { ArrowUpToLine, ChevronDown, ChevronRight, History, Menu, Search as SearchIcon, Trash2, UserRound, X } from "lucide-react";
import { useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { useCategories, useRanks, useSeries, useSite, useSuggest } from "../api";
import { fmtCount } from "../player/format";
import { href, Link, navigate, useLocation } from "../router";
import { Avatar } from "../social/Avatar";
import { useAuth } from "../state/auth";
import { useLibrary } from "../state/library";
import { closeOverlay, openOverlay } from "../state/nav";
import { LEGAL_PATHS, type LegalPath } from "../pages/Legal";
import { useTranslation } from "react-i18next";
import { nativeApp } from "../native";

/** Desktop layout from 1024 px (Tailwind `lg`); below that the phone header, as on the reference. */
export const WIDE_HEADER = "(min-width: 1024px)";

export function useMedia(query: string) {
  const [on, setOn] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const mq = window.matchMedia(query);
    const update = () => setOn(mq.matches);
    mq.addEventListener("change", update);
    return () => mq.removeEventListener("change", update);
  }, [query]);
  return on;
}

type NavItem = { label: string; to: string; active: (path: string, sort: string | null) => boolean; caret?: boolean };

/** 首页 · 原创 · the open channels (short names) · 专题 · 排行榜 · 博客 · 回家的路 · APP, as on the reference. */
function useNavItems(): NavItem[] {
  const categories = useCategories();
  // On a series page the series' channel is highlighted (from the query cache: no extra request).
  const { path } = useLocation();
  const videoId = /^\/video\/(\d+)/.exec(path)?.[1];
  const seriesSlug = useSeries(videoId ? Number(videoId) : undefined).data?.category_slug;
  const channels = (categories.data ?? []).filter((c) => c.status === "active").map((c) => ({
    label: c.name.replace(/^AI成人/, ""),
    to: href.channel(c.slug),
    active: (p: string) => p === `/c/${c.slug}` || (p.startsWith("/video/") && seriesSlug === c.slug),
  }));
  return [
    { label: "首页", to: "/", active: (p) => p === "/" },
    { label: "ReelGuo原创", to: href.channel("all", "original"), active: (p, sort) => p === "/c/all" && sort === "original" },
    ...channels,
    { label: "专题", to: href.topics(), active: (p) => p.startsWith("/topics") },
    { label: "排行榜", to: href.ranks(), active: (p) => p.startsWith("/ranks") },
    { label: "博客", to: href.posts(), active: (p) => p.startsWith("/posts") || p.startsWith("/post/"), caret: true },
    { label: "回家的路", to: href.wayHome(), active: (p) => p === "/go-home" },
    { label: "ReelGuo APP", to: href.download(), active: (p) => p === "/download" },
  ];
}

/** Logo block: 112 × 41 on desktop (115 × 42 centred on phones). */
export function Logo({ small = false }: { small?: boolean }) {
  return (
    <Link to="/" className={`flex items-center gap-1.5 shrink-0 h-[41px] ${small ? "" : "w-[112px]"}`} aria-label="ReelGuo 首页">
      <img src="/icon.svg" alt="" className={small ? "w-8 h-8 rounded-[9px]" : "w-8 h-8 rounded-[9px]"} />
      <span className="flex flex-col leading-none">
        <span className="text-[16px] font-extrabold text-white tracking-wide">ReelGuo</span>
        <span className="text-[10px] text-white/60 mt-1 tracking-[.18em]">AI 短剧</span>
      </span>
    </Link>
  );
}

/* ---------------------------------------------------------------------------------------------
 * Search: history, hot list and suggestions (desktop dropdown, phone overlay).
 * ------------------------------------------------------------------------------------------- */

export function SearchPanel({ onPick }: { onPick: (q: string) => void }) {
  const { searches, clearSearches } = useLibrary();
  const hot = useRanks("hot");
  const suggest = useSuggest(0);
  return (
    <div className="p-5 lg:p-6">
      <div className="flex items-center">
        <h3 className="text-[16px] font-bold text-on">搜索记录</h3>
        {searches.length > 0 && (
          <button onClick={clearSearches} className="ml-auto text-[12px] text-dim hover:text-mut flex items-center gap-1"><Trash2 size={12} />清空</button>
        )}
      </div>
      {searches.length ? (
        <div className="mt-3 flex flex-wrap gap-2">
          {searches.map((q) => (
            <button key={q} onClick={() => onPick(q)} className="h-8 px-3.5 rounded-full bg-white/[.06] hover:bg-white/10 text-[13px] text-on/85">{q}</button>
          ))}
        </div>
      ) : <p className="mt-3 text-[13px] text-dim">暂无搜索记录</p>}

      <h3 className="mt-6 text-[16px] font-bold text-on">热搜榜</h3>
      <ol className="mt-2">
        {(hot.data?.items ?? []).slice(0, 6).map((r, i) => (
          <li key={r.video_id}>
            <Link to={href.video(r.video_id)} className="flex items-center gap-4 h-11 text-[14px] text-on hover:text-primary">
              <span className={`w-5 text-[16px] font-extrabold italic tabular-nums ${i < 3 ? "text-primary" : "text-on/80"}`}>{i + 1}</span>
              <span className="flex-1 truncate">{r.title}</span>
              <span className="text-[12px] text-dim tabular-nums">{fmtCount(r.metrics.play)}</span>
            </Link>
          </li>
        ))}
        {hot.data && hot.data.items.length === 0 && <li className="text-[13px] text-dim py-2">榜单更新中</li>}
      </ol>

      {(suggest.data?.recommended.length ?? 0) > 0 && (
        <>
          <h3 className="mt-6 text-[16px] font-bold text-on">猜你喜欢</h3>
          <div className="mt-3 grid grid-cols-3 gap-3">
            {suggest.data!.recommended.slice(0, 3).map((v) => (
              <Link key={v.id} to={href.video(v.id)} className="min-w-0 group">
                <div className="relative rounded-[10px] overflow-hidden bg-s2" style={{ aspectRatio: "3 / 4" }}>
                  <Img src={v.cover} />
                  {v.score > 0 && <span className="absolute top-1.5 right-1.5 px-1.5 h-5 rounded bg-black/65 text-primary text-[11px] font-bold flex items-center">{v.score.toFixed(1)}分</span>}
                </div>
                <div className="mt-1.5 text-[12.5px] text-on truncate group-hover:text-primary">{v.title}</div>
              </Link>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

export function useSearchSubmit(close: () => void = () => {}) {
  const addSearch = useLibrary((s) => s.addSearch);
  return (raw: string) => {
    const q = raw.trim().slice(0, 50);
    if (!q) return;
    addSearch(q);
    close();
    navigate(href.search(q));
  };
}

/** 210 × 38 frosted search box (an icon button below 1366 px), with the history / hot dropdown. */
function DesktopSearch() {
  const { query } = useLocation();
  const [value, setValue] = useState(query.get("q") ?? "");
  const [open, setOpen] = useState(false);
  const roomy = useMedia("(min-width: 1366px)");
  const box = useRef<HTMLDivElement>(null);
  const submit = useSearchSubmit(() => setOpen(false));
  useEffect(() => {
    if (!open) return undefined;
    const onDown = (e: PointerEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("pointerdown", onDown);
    document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("pointerdown", onDown); document.removeEventListener("keydown", onKey); };
  }, [open]);
  const frost = "bg-[rgba(221,221,221,.15)] border border-white/[.094] backdrop-blur-md";
  if (!roomy) {
    return (
      <Link to={href.search()} aria-label="搜索" className={`w-9 h-9 rounded-full ${frost} flex items-center justify-center text-white shrink-0`}>
        <SearchIcon size={16} />
      </Link>
    );
  }
  return (
    <div ref={box} className="relative w-[210px] shrink-0">
      <form role="search" onSubmit={(e: FormEvent) => { e.preventDefault(); submit(value); }}
        className={`h-[38px] rounded-[24px] ${frost} flex items-center pl-4 pr-[11px]`}>
        <input value={value} onChange={(e) => setValue(e.target.value)} onFocus={() => setOpen(true)} maxLength={50}
          placeholder="搜索你感兴趣的内容" aria-label="搜索"
          className="flex-1 min-w-0 h-9 bg-transparent text-[14px] font-bold text-white placeholder:text-white/50 placeholder:font-bold outline-none" />
        <button type="submit" aria-label="搜索" className="w-4 h-4 flex items-center justify-center text-white/80 hover:text-white"><SearchIcon size={16} /></button>
      </form>
      {open && (
        <div className="absolute right-0 top-[calc(100%+10px)] w-[400px] max-h-[75vh] overflow-y-auto rounded-[16px] bg-[#1a1a1a] border border-white/[.08] elev-3 a-scale-in z-50">
          <SearchPanel onPick={submit} />
        </div>
      )}
    </div>
  );
}

/** "登录/注册" pill (97 × 38, accent), or the avatar when signed in. */
function AccountButton({ wide }: { wide: boolean }) {
  const user = useAuth((s) => (s.status === "signed_in" ? s.user : null));
  const openLogin = useAuth((s) => s.openLogin);
  if (user) {
    return (
      <Link to={href.me()} className="flex items-center gap-2 shrink-0 hover:opacity-90" aria-label="个人中心">
        <Avatar size={wide ? 32 : 28} name={user.nickname} color={user.avatar_color} />
        {wide && <span className="text-[14px] font-semibold text-[#d9d9d9] max-w-[96px] truncate">{user.nickname}</span>}
      </Link>
    );
  }
  return (
    <button onClick={openLogin}
      className="shrink-0 h-9 min-[1366px]:h-[38px] px-[13px] min-[1366px]:px-[14px] rounded-[24px] bg-[#ffd321] text-[#14130e] text-[14px] font-semibold">
      登录/注册
    </button>
  );
}

function DesktopHeader() {
  const items = useNavItems();
  const { path, query } = useLocation();
  const sort = query.get("sort");
  return (
    <div className="rg-wrap h-[76px] flex items-center">
      <Logo />
      <nav aria-label="主导航" className="ml-4 flex items-center gap-1 min-w-0">
        {items.map((it) => {
          const on = it.active(path, sort);
          return (
            <Link key={it.to} to={it.to} aria-current={on ? "page" : undefined}
              className={`h-[41px] px-[3px] min-[1366px]:px-[5.5px] min-[1680px]:px-[9px] flex items-center gap-1.5 text-[14px] font-semibold leading-[21px] whitespace-nowrap transition-colors ${on ? "text-[#ffd321]" : "text-[#d9d9d9] hover:text-white"}`}>
              {it.label}{it.caret && <ChevronDown size={9} className="opacity-70" />}
            </Link>
          );
        })}
      </nav>
      <span className="flex-1 min-w-4" />
      <div className="flex items-center gap-2 min-[1366px]:gap-4">
        <DesktopSearch />
        <AccountButton wide />
      </div>
    </div>
  );
}

function Drawer({ open, side, onClose, children }: { open: boolean; side: "left" | "top"; onClose: () => void; children: ReactNode }) {
  const [rendered, setRendered] = useState(open);
  useEffect(() => {
    if (open) { setRendered(true); return undefined; }
    const t = window.setTimeout(() => setRendered(false), 220);
    return () => window.clearTimeout(t);
  }, [open]);
  useEffect(() => {
    if (!open) return undefined;
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => { document.body.style.overflow = prev; };
  }, [open]);
  if (!rendered) return null;
  return (
    <div className="fixed inset-0 z-[60]" role="dialog" aria-modal="true">
      <div className={`absolute inset-0 bg-black/60 transition-opacity duration-200 ${open ? "opacity-100" : "opacity-0"}`} onClick={onClose} />
      <div className={`absolute bg-[#121212] transition-transform duration-200 overflow-y-auto ${side === "left"
        ? `left-0 top-0 bottom-0 w-[280px] ${open ? "translate-x-0" : "-translate-x-full"}`
        : `left-0 right-0 top-0 max-h-[100dvh] ${open ? "translate-y-0" : "-translate-y-full"}`}`}>
        {children}
      </div>
    </div>
  );
}

function MobileHeader() {
  const items = useNavItems();
  const { path } = useLocation();
  const [menu, setMenu] = useState(false);
  const [search, setSearch] = useState(false);
  const [value, setValue] = useState("");
  const user = useAuth((s) => (s.status === "signed_in" ? s.user : null));
  const openLogin = useAuth((s) => s.openLogin);
  const submit = useSearchSubmit(() => closeOverlay());
  const openMenu = () => { setMenu(true); openOverlay(() => setMenu(false)); };
  const openSearch = () => { setSearch(true); openOverlay(() => setSearch(false)); };

  return (
    <>
      <div className="relative h-14 flex items-center px-[18px]">
        <button onClick={openMenu} aria-label="菜单" className="w-7 h-7 flex items-center justify-center text-white"><Menu size={24} /></button>
        <div className="absolute left-1/2 top-[7px] -translate-x-1/2"><Logo small /></div>
        <button onClick={openSearch} aria-label="搜索" className="ml-auto w-7 h-7 flex items-center justify-center text-white"><SearchIcon size={22} /></button>
      </div>

      <Drawer open={menu} side="left" onClose={closeOverlay}>
        <div className="p-4 flex items-center justify-between"><Logo small /><button onClick={closeOverlay} aria-label="关闭" className="w-9 h-9 rounded-full flex items-center justify-center text-mut"><X size={18} /></button></div>
        <button onClick={() => { closeOverlay(); if (user) navigate(href.me()); else window.setTimeout(openLogin, 50); }}
          className="mx-4 mb-3 w-[calc(100%-32px)] flex items-center gap-3 p-3 rounded-[12px] bg-white/[.05] text-left">
          {user ? <Avatar size={36} name={user.nickname} color={user.avatar_color} />
            : <span className="w-9 h-9 rounded-full bg-white/10 flex items-center justify-center text-mut"><UserRound size={18} /></span>}
          <span className="flex-1 min-w-0">
            <span className="block text-[14px] font-semibold text-on truncate">{user ? user.nickname : "登录 / 注册"}</span>
            <span className="block text-[11.5px] text-dim">{user ? "个人中心" : "登录后可收藏、评论和同步记录"}</span>
          </span>
          <ChevronRight size={16} className="text-dim" />
        </button>
        <nav className="px-2 pb-6">
          {items.map((it) => (
            <Link key={it.to} to={it.to} className={`flex items-center h-12 px-4 rounded-[10px] text-[15px] ${it.active(path, null) ? "bg-primary/15 text-primary font-semibold" : "text-white/85"}`}>
              {it.label}
            </Link>
          ))}
        </nav>
      </Drawer>

      <Drawer open={search} side="top" onClose={closeOverlay}>
        <div className="p-3 border-b border-white/[.06] flex items-center gap-2" style={{ paddingTop: "max(env(safe-area-inset-top), 12px)" }}>
          <form role="search" className="flex-1" onSubmit={(e) => { e.preventDefault(); submit(value); }}>
            <input autoFocus value={value} onChange={(e) => setValue(e.target.value)} maxLength={50} placeholder="搜索你感兴趣的内容" aria-label="搜索"
              className="w-full h-11 px-5 rounded-full bg-[#1a1a1a] border border-primary text-[14px] text-on placeholder:text-dim outline-none" />
          </form>
          <button onClick={closeOverlay} className="h-11 px-3 text-[14px] text-mut">取消</button>
        </div>
        <SearchPanel onPick={submit} />
      </Drawer>
    </>
  );
}

/** Desktop: a 76 px frosted bar laid over the page (it scrolls away). Phone: a fixed 56 px solid bar. */
export function SiteHeader() {
  const wide = useMedia(WIDE_HEADER);
  return wide
    ? <header className="absolute top-0 inset-x-0 z-40 bg-black/30 backdrop-blur-[18.75px]"><DesktopHeader /></header>
    : <header className="fixed top-0 inset-x-0 z-40 bg-[#0a0a0a]"><MobileHeader /></header>;
}

/* ---------------------------------------------------------------------------------------------
 * Footer: logo, addresses and © on the left; 关于 · 政策 · 其他 columns on the right.
 * ------------------------------------------------------------------------------------------- */

export function SiteFooter() {
  const { t } = useTranslation();
  const site = useSite();
  const s = site.data;
  const bare = (u: string) => u.replace(/^https?:\/\//, "").replace(/\/$/, "");
  const lines: [string, string, string][] = [];
  if (s?.mirrors[1]) lines.push(["备用入口：", bare(s.mirrors[1]), s.mirrors[1]]);
  if (s?.mirrors[0]) lines.push(["最新地址：", bare(s.mirrors[0]), s.mirrors[0]]);
  lines.push(["永久地址发布页：", s?.permanent_domain ? bare(s.permanent_domain) : `${window.location.host}${href.wayHome()}`, s?.permanent_domain || href.wayHome()]);
  const col = (title: string, links: { label: string; to: string; external?: boolean }[]) => (
    <div className="w-[110px]">
      <div className="text-[12.25px] font-medium leading-[17px] text-white">{title}</div>
      <ul className="mt-[9px] space-y-[9px]">
        {links.map((l) => (
          <li key={l.label} className="leading-4">
            {l.external
              ? <a href={l.to} target="_blank" rel="noreferrer" className="text-[12.25px] text-white/35 hover:text-white">{l.label}</a>
              : <Link to={l.to} className="text-[12.25px] text-white/35 hover:text-white">{l.label}</Link>}
          </li>
        ))}
      </ul>
    </div>
  );
  return (
    <footer className="border-t border-white/10 bg-[#0d0c0c]">
      <div className="rg-wrap pt-[23px] pb-7 flex flex-col lg:flex-row gap-8">
        <div className="flex-1 min-w-0">
          <Logo small />
          <div className="mt-[10px] pl-[6px] space-y-[5px]">
            {lines.map(([label, text, to]) => (
              <div key={label} className="text-[12px] font-medium leading-[14px]">
                <span className="text-[#999]">{label}</span>
                {to.startsWith("/") ? <Link to={to} className="text-[#ffd21c]">{text}</Link> : <a href={to} className="text-[#ffd21c]">{text}</a>}
              </div>
            ))}
          </div>
          <p className="mt-[14px] text-[12px] leading-[19px] text-white/35">© ReelGuo · ALL RIGHTS RESERVED · 本站内容仅面向年满 18 周岁的成年人</p>
        </div>
        <div className="flex gap-6 lg:gap-[73px]">
          {col("关于", [
            { label: "回家的路", to: href.wayHome() },
            { label: "下载 App", to: href.download() },
            { label: "最近上新", to: href.channel("all") },
            { label: "热门推荐", to: href.ranks() },
            { label: "专题合集", to: href.topics() },
          ])}
          {col("政策", (Object.keys(LEGAL_PATHS) as LegalPath[]).map((p) => ({ label: t(LEGAL_PATHS[p]), to: p, external: true })))}
          {col("其他", [
            { label: "个人中心", to: href.me() },
            { label: "设置", to: href.settings() },
            { label: "搜索", to: href.search() },
          ])}
        </div>
      </div>
    </footer>
  );
}

/* ---------------------------------------------------------------------------------------------
 * Floating dock at the right edge: 下载 APP card (dismissible), then 最近浏览 / 回到顶部.
 * ------------------------------------------------------------------------------------------- */

const DOCK_KEY = "rg.appCard.hidden";

export function FloatDock() {
  const [card, setCard] = useState(() => {
    try { return !nativeApp && sessionStorage.getItem(DOCK_KEY) !== "1"; } catch { return !nativeApp; }
  });
  const hide = () => { setCard(false); try { sessionStorage.setItem(DOCK_KEY, "1"); } catch { /* ignore */ } };
  const btn = "w-14 h-14 rounded-[9px] flex flex-col items-center justify-center gap-0.5 text-[12px] font-medium leading-[18px]";
  return (
    <div className="fixed right-2 lg:right-[19px] bottom-6 lg:bottom-[30px] z-30 flex flex-col items-end gap-[30px] scale-[.82] lg:scale-100 origin-bottom-right">
      {card && (
        <div className="relative">
          <Link to={href.download()} className="w-[88px] h-[92px] rounded-[10px] border border-white/10 flex flex-col items-center pt-[5px]"
            style={{ background: "linear-gradient(149.42deg, rgba(71,66,46,.72) 19.3%, rgba(46,43,30,.72) 46.4%, rgba(43,40,25,.72) 80.5%)" }}>
            <img src="/icon.svg" alt="" className="w-[60px] h-[59px] rounded-[12px]" />
            <span className="-mt-[5px] h-6 px-2 rounded-full text-[12px] font-medium text-black flex items-center"
              style={{ background: "linear-gradient(90deg,#ffd21c,#f5a623)" }}>下载 APP</span>
          </Link>
          <button onClick={hide} aria-label="关闭" className="absolute -top-1 -right-4 w-5 h-5 rounded-full bg-black/60 text-white/80 flex items-center justify-center"><X size={12} /></button>
        </div>
      )}
      <div className="w-[68px] p-[6px] rounded-[12px] bg-white/5 border border-white/10 backdrop-blur-md flex flex-col gap-1">
        <Link to={href.me()} className={`${btn} bg-[#ffd321] text-black`}><History size={18} />最近浏览</Link>
        <button onClick={() => window.scrollTo({ top: 0, behavior: "smooth" })} className={`${btn} text-[#a1a1aa] hover:text-white`}><ArrowUpToLine size={18} />回到顶部</button>
      </div>
    </div>
  );
}
