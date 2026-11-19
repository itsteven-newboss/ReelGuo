import { unwrap, type Schemas } from "@reelguo/api-client";
import { faApple } from "@fortawesome/free-brands-svg-icons";
import { BrandMark, Img } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { ArrowDown, ChevronDown, Copy, Crown, Download, Flame, Lightbulb, MonitorSmartphone, Play, Settings, Share, ShieldCheck, Smartphone, SquarePlus, X } from "lucide-react";
import { useEffect, useId, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties, type ReactNode } from "react";
import { renderSVG } from "uqr";
import { api, useCategories, useListingPage, useRanks, useSeriesList, useSite, type VideoCard } from "../api";
import { fmtCount } from "../player/format";
import { Link } from "../router";
import { Wordmark } from "../portal/chrome";
import { useUi } from "../state/ui";
import { BRAND } from "../brand";
import { installHint, macChip, thisDevice, type Device } from "./device";

/*
 * App download landing page at /app/{section}, laid out like the reference product's download site (measured at
 * 1440 px and 440 px): a header with section tabs and a 客户端 button, the hero with the download controls, then
 * 内容亮点 · 剧场类型 · 沉浸式体验 · 心愿榜 · 创作者计划 · 关于我们 and a footer. The reference draws its sections as
 * artwork; here they are built from our own logo, series covers and CSS, with our own copy, and any picture can be
 * replaced in Admin → 下载页图片 (useArt). Sizes are designed at
 * 1440 px (desktop) and 440 px (phones) and scale with the window, as the reference's artwork does.
 */

type Release = Schemas["AppReleaseDto"];

export const LANDING_SECTIONS = ["home", "highlights", "genres", "experience", "wishlist", "creator", "about"] as const;
export type LandingSection = (typeof LANDING_SECTIONS)[number];
const TABS: { id: LandingSection; label: string }[] = [
  { id: "home", label: "首页" },
  { id: "highlights", label: "内容亮点" },
  { id: "genres", label: "剧场类型" },
  { id: "wishlist", label: "心愿榜" },
  { id: "creator", label: "创作者" },
  { id: "about", label: "关于我们" },
];

/* ------------------------------------------------------------------ sizing and style tokens */

const WIDE_QUERY = "(min-width: 1024px)";
const subscribeResize = (cb: () => void) => { window.addEventListener("resize", cb); return () => window.removeEventListener("resize", cb); };
const viewportWidth = () => document.documentElement.clientWidth || window.innerWidth;

/** `u(desktopPx, phonePx)`: a size designed at 1440 px (desktop) or 440 px (phones), scaled to the window. */
function useScale() {
  const w = useSyncExternalStore(subscribeResize, viewportWidth);
  const wide = window.matchMedia(WIDE_QUERY).matches;
  const k = wide ? Math.min(w / 1440, 1.5) : Math.min(w / 440, 1.3);
  const u = (d: number, p: number) => (wide ? d : p) * k;
  return { wide, u };
}

const SERIF = '"Noto Serif SC","Source Han Serif SC","Songti SC","STSong","SimSun",serif';
const GOLD_TEXT: CSSProperties = {
  backgroundImage: "linear-gradient(180deg,#fff7da 0%,#f9d98a 35%,#e2a944 68%,#a86f1c 100%)",
  WebkitBackgroundClip: "text", backgroundClip: "text", color: "transparent",
  filter: "drop-shadow(0 2px 8px rgba(255,186,72,.28))",
};
const GOLD_FILL = "linear-gradient(180deg,#f8d68e 0%,#ebb763 45%,#c98a35 100%)";
const GOLD_LINE = "linear-gradient(90deg,transparent,rgba(240,190,100,.75),transparent)";
const glow = (x: string, y: string, a = 0.22, size = "60%") => `radial-gradient(ellipse at ${x} ${y}, rgba(255,176,56,${a}), transparent ${size})`;

/* ------------------------------------------------------------------ data */

function useReleases() {
  return useQuery({
    queryKey: ["app-releases"],
    queryFn: async () => unwrap(await api.GET("/api/v1/app/releases")),
    staleTime: 5 * 60_000,
  });
}

/** Covers for the artwork: editor picks, then hot series; repeats when the catalogue is small. */
function useCovers() {
  const picks = useSeriesList("picks", 14);
  const hot = useSeriesList("hot", 14);
  return useMemo(() => {
    const seen = new Set<number>();
    const list: VideoCard[] = [];
    for (const v of [...(picks.data?.items ?? []), ...(hot.data?.items ?? [])]) {
      if (v.cover && !seen.has(v.id)) { seen.add(v.id); list.push(v); }
    }
    return list;
  }, [picks.data, hot.data]);
}
const nth = <T,>(list: T[], i: number): T | undefined => (list.length ? list[i % list.length] : undefined);

function useLandingImages() {
  return useQuery({
    queryKey: ["landing-images"],
    queryFn: async () => unwrap(await api.GET("/api/v1/app/landing-images")),
    staleTime: 60_000,
  });
}

/**
 * Pictures uploaded in Admin → 下载页图片. `one(slot)` is a picture inside a section; `bg(name)` a section background for
 * this screen size (name_d on desktop, name_p on phones), falling back to the other size. Both are empty when nothing
 * was uploaded, and the page keeps its default from series covers.
 */
function useArt() {
  const { wide } = useScale();
  const images = useLandingImages().data?.images ?? {};
  const one = (slot: string) => (images[slot] ? { cover: images[slot]!, title: "" } : undefined);
  const bg = (name: string) => {
    const d = images[`${name}_d`], p = images[`${name}_p`];
    return (wide ? d ?? p : p ?? d) ?? null;
  };
  return { one, bg };
}

/** A picture filling its section behind the content (the section needs `isolate`), with an optional shade on top. */
function ArtBackground({ url, shade, warm }: { url: string | null | undefined; shade?: string; warm?: boolean }) {
  return (
    <div className="absolute inset-0 -z-10 pointer-events-none overflow-hidden">
      {url && <div className="absolute inset-0" style={warm ? { filter: "sepia(.35) saturate(.95) brightness(.9)" } : undefined}><Img src={url} alt="" /></div>}
      {shade && <div className="absolute inset-0" style={{ background: shade }} />}
    </div>
  );
}

/** The Android build through another of our domains, for when the main one is blocked; the same file otherwise. */
function backupUrl(url: string, mirrors: string[]) {
  try {
    const u = new URL(url, window.location.origin);
    if (u.origin === window.location.origin) {
      const other = mirrors.find((m) => { try { return new URL(m).origin !== window.location.origin; } catch { return false; } });
      if (other) return new URL(u.pathname + u.search, other).href;
    }
  } catch { /* keep the original */ }
  return url;
}

/* ------------------------------------------------------------------ small pieces */

/** A cover image; positioned `relative` unless the caller places it with `absolute`. */
function Cover({ v, className = "", style }: { v?: VideoCard | { cover: string | null; title: string }; className?: string; style?: CSSProperties }) {
  return (
    <div className={`${/\babsolute\b/.test(className) ? "" : "relative "}overflow-hidden bg-[#1a140c] ${className}`} style={style}>
      {v?.cover ? <Img src={v.cover} alt={v.title} /> : <div className="absolute inset-0" style={{ background: "linear-gradient(160deg,#3a2a12,#120c06)" }} />}
    </div>
  );
}

/** An id usable in SVG `url(#…)` references. */
const useSvgId = (prefix: string) => `${prefix}${useId().replace(/[^a-zA-Z0-9]/g, "")}`;

/** Pale-gold fill of the phone download buttons' logos, as on the reference. */
function LogoFill({ id }: { id: string }) {
  return (
    <defs>
      <linearGradient id={id} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#fffdf0" /><stop offset="1" stopColor="#f6d77c" /></linearGradient>
    </defs>
  );
}

/** The Apple logo (Font Awesome Free brand icon, CC BY 4.0). `plain` draws it in the text colour. */
function AppleLogo({ size, plain }: { size: number; plain?: boolean }) {
  const id = useSvgId("al");
  const [w, h, , , d] = faApple.icon;
  return (
    <svg viewBox={`0 0 ${w} ${h}`} width={size} height={size} aria-hidden="true">
      {!plain && <LogoFill id={id} />}
      <path d={String(d)} fill={plain ? "currentColor" : `url(#${id})`} />
    </svg>
  );
}

/** The Android robot, whole (head, body, arms, legs), drawn after Google's robot (CC BY 3.0). */
function AndroidRobot({ size }: { size: number }) {
  const id = useSvgId("ar");
  return (
    <svg viewBox="0 0 24 24" width={size} height={size} aria-hidden="true">
      <LogoFill id={id} />
      <g fill={`url(#${id})`}>
        <path fillRule="evenodd" d="M6 9.6a6 6 0 0 1 12 0Zm3.6-3.1a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm4.8 0a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Z" />
        <path d="M6 10.4h12v7.3a1.3 1.3 0 0 1-1.3 1.3H7.3A1.3 1.3 0 0 1 6 17.7Z" />
        <rect x="3" y="10.4" width="2.2" height="6.2" rx="1.1" />
        <rect x="18.8" y="10.4" width="2.2" height="6.2" rx="1.1" />
        <rect x="8.2" y="18.2" width="2.2" height="4" rx="1.1" />
        <rect x="13.6" y="18.2" width="2.2" height="4" rx="1.1" />
      </g>
      <g stroke={`url(#${id})`} strokeWidth="0.9" strokeLinecap="round"><path d="M8.4 4.9 7.2 3" /><path d="M15.6 4.9 16.8 3" /></g>
    </svg>
  );
}
function WindowsGlyph({ size }: { size: number }) {
  return (
    <svg viewBox="0 0 24 24" width={size} height={size} fill="currentColor" aria-hidden="true">
      <path d="M3 5.2 10.5 4v7.3H3V5.2Zm8.5-1.3L21 2.5v8.8h-9.5V3.9ZM3 12.7h7.5V20L3 18.8v-6.1Zm8.5 0H21v8.8l-9.5-1.4v-7.4Z" />
    </svg>
  );
}

function GoldTitle({ children, size, className = "", style }: { children: ReactNode; size: number; className?: string; style?: CSSProperties }) {
  return <h2 className={`font-black tracking-[.12em] leading-[1.15] ${className}`} style={{ ...GOLD_TEXT, fontFamily: SERIF, fontSize: size, ...style }}>{children}</h2>;
}

/** A centred section title between two gold rules with diamonds, as on the reference. */
function OrnateTitle({ children, size, lineW }: { children: ReactNode; size: number; lineW: number }) {
  const rule = (flip: boolean) => (
    <span className="flex items-center shrink-0" style={{ width: lineW, flexDirection: flip ? "row-reverse" : "row" }}>
      <span className="flex-1 h-px" style={{ background: `linear-gradient(${flip ? "270deg" : "90deg"},transparent,rgba(240,190,100,.9))` }} />
      <span className="rotate-45 bg-[#f0c070]" style={{ width: size * 0.12, height: size * 0.12 }} />
    </span>
  );
  return (
    <div className="flex items-center justify-center" style={{ gap: size * 0.25 }}>
      {rule(false)}
      <GoldTitle size={size}>{children}</GoldTitle>
      {rule(true)}
    </div>
  );
}

/** Gold pill button with a dark round icon, like the reference's 立即下载 / 我要许愿. */
function GoldButton({ icon, label, onClick, to, href, w, h, font }: { icon: ReactNode; label: string; onClick?: () => void; to?: string; href?: string; w: number; h: number; font: number }) {
  const cls = "relative flex items-center justify-center font-bold text-[#1b1206] shadow-[0_6px_24px_rgba(232,170,72,.28)] hover:brightness-105 active:brightness-95";
  const style: CSSProperties = { width: w, height: h, fontSize: font, gap: font * 0.5, borderRadius: h * 0.18, background: GOLD_FILL, border: "1px solid rgba(255,236,190,.65)" };
  const inner = (
    <>
      <span className="rounded-full bg-[#1b1206] text-[#f2c56d] flex items-center justify-center shrink-0" style={{ width: h * 0.55, height: h * 0.55 }}>{icon}</span>
      {label}
    </>
  );
  if (href) return <a href={href} target="_blank" rel="noreferrer" className={cls} style={style}>{inner}</a>;
  return to ? <Link to={to} className={cls} style={style}>{inner}</Link> : <button type="button" onClick={onClick} className={cls} style={style}>{inner}</button>;
}

/* ------------------------------------------------------------------ the page */

export function AppLanding({ section }: { section: LandingSection }) {
  const { wide, u } = useScale();
  const site = useSite().data;
  const releases = useReleases();
  const covers = useCovers();
  const toast = useUi((s) => s.toast);
  const [active, setActive] = useState<LandingSection>(section);
  const [macOpen, setMacOpen] = useState(false);
  const [iosHelp, setIosHelp] = useState(false);
  const [inApp, setInApp] = useState(false);
  const device = useMemo(thisDevice, []);
  const [chip, setChip] = useState<"arm" | "x86" | null>(null);
  const refs = useRef<Partial<Record<LandingSection, HTMLElement | null>>>({});
  const spyLock = useRef(0);
  const headerH = wide ? u(104, 0) : u(0, 58) + u(0, 54);

  useEffect(() => {
    const prev = document.title;
    document.title = `下载 App - ${BRAND}`;
    return () => { document.title = prev; };
  }, []);

  const release = (p: string) => releases.data?.find((r: Release) => r.platform === p);
  const open = (url: string) => { window.location.href = url; };
  const SOON = "该版本即将上线，敬请期待";
  // WeChat, QQ and the like block downloads: ask to open the page in a real browser first.
  const get = (p: string) => {
    if (device.inApp) return setInApp(true);
    const r = release(p);
    if (r) open(r.url);
    else toast(releases.isPending ? "正在获取下载地址，请稍候" : SOON);
  };
  const androidBackup = () => {
    if (device.inApp) return setInApp(true);
    const r = release("android");
    if (r) open(backupUrl(r.url, site?.mirrors ?? []));
    else toast(SOON);
  };
  const ios = () => (device.inApp ? setInApp(true) : setIosHelp(true));

  const scrollTo = (id: LandingSection, smooth = true) => {
    const el = refs.current[id];
    if (!el) return;
    const top = id === "home" ? 0 : el.getBoundingClientRect().top + window.scrollY - headerH + 1;
    window.scrollTo({ top, behavior: smooth ? "smooth" : "auto" });
  };
  const goTab = (id: LandingSection) => {
    spyLock.current = Date.now() + 900;
    setActive(id);
    window.history.replaceState({}, "", `/app/${id}`);
    scrollTo(id);
  };
  /** Asks a Mac's browser for its chip, to download the right build at once or mark it in the chooser. */
  const mac = async () => {
    const c = chip ?? (await macChip());
    setChip(c);
    return c;
  };
  const chooseMac = () => { scrollTo("home"); setMacOpen(true); };
  /** The visitor's own device: download its file, or show the iPhone steps / the Mac chip choice. */
  const primary = async () => {
    if (device.inApp) return setInApp(true);
    switch (device.os) {
      case "android": return get("android");
      case "ios": return setIosHelp(true);
      case "windows": return get("windows-x86_64");
      case "linux": return get("linux-x86_64");
      case "harmony": return toast("鸿蒙 NEXT 暂不支持安装安卓 App，可直接用浏览器观看，或把本站添加到桌面");
      case "mac": {
        const c = await mac();
        return c ? get(c === "arm" ? "darwin-aarch64" : "darwin-x86_64") : chooseMac();
      }
      default: return scrollTo("home");
    }
  };

  // Sent here by /app/download (the QR code, a shared link) with ?install=… when there was no file to hand over.
  useEffect(() => {
    const hint = installHint(window.location.search);
    if (!hint) return;
    window.history.replaceState({}, "", window.location.pathname);
    if (hint === "browser" || device.inApp) setInApp(true);
    else if (hint === "ios" || device.os === "ios") setIosHelp(true);   // an iPad asking for the desktop site comes as "mac"
    else if (hint === "mac") void mac().then(() => setMacOpen(true));
    else if (hint === "auto") { if (device.os === "harmony") void primary(); }
    else toast(SOON);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Open at the section in the URL, then keep the tab and the URL in step with the scroll position.
  useEffect(() => {
    if (section !== "home") requestAnimationFrame(() => scrollTo(section, false));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => {
    let frame = 0;
    const onScroll = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        if (Date.now() < spyLock.current) return;
        let cur: LandingSection = "home";
        for (const id of LANDING_SECTIONS) {
          const el = refs.current[id];
          if (el && el.getBoundingClientRect().top - headerH <= 60) cur = id;
        }
        const tab = cur === "experience" ? "genres" : cur;
        setActive((a) => {
          if (a !== tab) window.history.replaceState({}, "", `/app/${cur}`);
          return tab;
        });
      });
    };
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => { window.removeEventListener("scroll", onScroll); cancelAnimationFrame(frame); };
  }, [headerH]);

  const reg = (id: LandingSection) => (el: HTMLElement | null) => { refs.current[id] = el; };
  const actions = { get, androidBackup, primary, ios, chip, macOpen, setMacOpen: (v: boolean) => { if (v) void mac(); setMacOpen(v); } };

  return (
    <div className="min-h-dvh bg-[#050505] text-white overflow-x-clip" style={{ fontFamily: '"PingFang SC","Microsoft YaHei",Arial,sans-serif' }}>
      <LandingHeader active={active} onTab={goTab} onClient={primary} />
      <section ref={reg("home")} id="home" aria-label="首页"><Hero {...actions} /></section>
      <section ref={reg("highlights")} id="highlights" aria-label="内容亮点"><Highlights /></section>
      <section ref={reg("genres")} id="genres" aria-label="剧场类型"><Genres covers={covers} /></section>
      <section ref={reg("experience")} id="experience" aria-label="沉浸式体验"><Experience covers={covers} /></section>
      <section ref={reg("wishlist")} id="wishlist" aria-label="心愿榜"><Wishlist covers={covers} onWish={primary} /></section>
      <section ref={reg("creator")} id="creator" aria-label="创作者计划"><Creator covers={covers} contact={site?.telegram || (site?.email ? `mailto:${site.email}` : "/go-home")} /></section>
      <section ref={reg("about")} id="about" aria-label="关于我们"><About covers={covers} /></section>
      <LandingFooter onAbout={() => goTab("about")} />
      {iosHelp && <IosHelp device={device} external={!!release("ios")} onClose={() => setIosHelp(false)} />}
      {inApp && <InAppGuide device={device} onClose={() => setInApp(false)} />}
    </div>
  );
}

/* ------------------------------------------------------------------ header */

function LandingHeader({ active, onTab, onClient }: { active: LandingSection; onTab: (id: LandingSection) => void; onClient: () => void }) {
  const { wide, u } = useScale();
  const logo = (
    <a href="/app/home" onClick={(e) => { e.preventDefault(); onTab("home"); }} aria-label={`${BRAND} 下载`} className="shrink-0 flex items-center">
      <Wordmark scale={u(12.27, 7.15)} />
    </a>
  );
  const client = (
    <button type="button" onClick={onClient} className="ml-auto shrink-0 flex items-center justify-center font-bold text-[#050505] hover:brightness-105"
      style={{ width: u(225, 113), height: u(66, 36), borderRadius: u(12, 8), fontSize: u(26, 15), gap: u(14, 7), background: GOLD_FILL }}>
      <span className="rounded-full bg-[#0b0b0b] text-[#f2b544] flex items-center justify-center" style={{ width: u(40, 22), height: u(40, 22) }}><ArrowDown size={u(24, 14)} strokeWidth={2.6} /></span>
      客户端
    </button>
  );
  const tabs = (
    <nav aria-label="页面导航" className={wide ? "flex items-stretch h-full" : "flex items-stretch overflow-x-auto no-bar"}
      style={wide ? { marginLeft: u(64, 0), gap: u(48, 0) } : { height: u(0, 54), gap: u(0, 26), paddingInline: u(0, 13) }}>
      {TABS.map((t) => {
        const on = t.id === active;
        return (
          <a key={t.id} href={`/app/${t.id}`} onClick={(e) => { e.preventDefault(); onTab(t.id); }} aria-current={on ? "true" : undefined}
            className={`relative shrink-0 flex items-center whitespace-nowrap ${on ? "font-bold text-[#f8ca06]" : "text-white hover:text-[#f8ca06]"}`}
            style={{ fontSize: u(26, 16) }}>
            {t.label}
            {on && <span className="absolute left-1/2 -translate-x-1/2 rounded-full bg-[#f8ca06]" style={{ bottom: u(14, 6), width: u(52, 26), height: u(5, 3) }} />}
          </a>
        );
      })}
    </nav>
  );
  return wide ? (
    <header className="sticky top-0 z-40 bg-[#0a0a0a] flex items-center" style={{ height: u(104, 0), paddingInline: u(40, 0) }}>
      {logo}{tabs}{client}
    </header>
  ) : (
    <header className="sticky top-0 z-40">
      <div className="bg-[#0a0a0a] flex items-center" style={{ height: u(0, 58), paddingInline: u(0, 12) }}>{logo}{client}</div>
      <div className="bg-[#0a0a0a]/80 backdrop-blur-md">{tabs}</div>
    </header>
  );
}

/* ------------------------------------------------------------------ hero and downloads */

type Actions = {
  get: (p: string) => void; androidBackup: () => void; primary: () => void; ios: () => void;
  chip: "arm" | "x86" | null; macOpen: boolean; setMacOpen: (v: boolean) => void;
};

/**
 * Default first-screen pictures: our own brand artwork, made by D:/reelguo-work/tools/hero-art/make.mjs (desktop
 * 1440 × 608, phone 440 × 620, at 2×). Admin → 下载页图片 → 首页主视觉 replaces them.
 */
const HERO_DEFAULT = { desktop: "/landing/home-pc.webp", phone: "/landing/home-mobile.webp" };

/**
 * The first screen, as on the reference: one picture (title and slogan are part of it), with the QR code and the
 * download buttons laid over it on desktop and under it on phones (overlapping its bottom 64 px).
 */
function Hero(a: Actions) {
  const { wide, u } = useScale();
  const uploaded = useArt().bg("home");
  const src = uploaded ?? (wide ? HERO_DEFAULT.desktop : HERO_DEFAULT.phone);
  const picture = (h: number) => (
    <div className="absolute inset-x-0 top-0 overflow-hidden" style={{ height: h }}>
      <img src={src} alt={`${BRAND} App`} className="w-full h-full object-cover" draggable={false} />
      <div className="absolute inset-x-0 bottom-0 pointer-events-none" style={{ height: h * 0.16, background: "linear-gradient(180deg,transparent,#050505)" }} />
    </div>
  );

  if (wide) {
    return (
      <div className="relative bg-[#050505]" style={{ height: u(657, 0) }}>
        {picture(u(608, 0))}
        <QrCard />
        <div className="absolute grid grid-cols-2" style={{ left: u(743, 0), top: u(485, 0), columnGap: u(42, 0), rowGap: u(20, 0) }}>
          <GoldButton icon={<ArrowDown size={u(20, 0)} strokeWidth={2.6} />} label="立即下载" onClick={a.primary} w={u(237, 0)} h={u(67, 0)} font={u(20, 0)} />
          <Link to="/" className="flex items-center justify-center font-bold text-[#f2c56d] hover:bg-[#f2c56d]/10"
            style={{ width: u(239, 0), height: u(67, 0), fontSize: u(20, 0), gap: u(10, 0), borderRadius: u(12, 0), border: "1px solid rgba(242,197,109,.55)", background: "linear-gradient(180deg,rgba(60,42,16,.85),rgba(20,14,6,.85))" }}>
            <span className="rounded-full border border-[#f2c56d] flex items-center justify-center" style={{ width: u(34, 0), height: u(34, 0) }}><Play size={u(15, 0)} className="fill-current ml-[1px]" /></span>探索更多
          </Link>
          <DeskPlatformButton icon={<WindowsGlyph size={u(22, 0)} />} label="Windows 下载" onClick={() => a.get("windows-x86_64")} />
          <div className="relative">
            <DeskPlatformButton icon={<AppleLogo size={u(22, 0)} plain />} label="macOS 下载" caret onClick={() => a.setMacOpen(!a.macOpen)} />
            {a.macOpen && <MacChooser chip={a.chip} onPick={(p) => { a.setMacOpen(false); a.get(p); }} onClose={() => a.setMacOpen(false)} style={{ position: "absolute", right: 0, top: u(78, 0), zIndex: 30 }} />}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="relative bg-[#050505]" style={{ marginTop: -u(0, 54) }}>
      {picture(u(0, 620))}
      <div style={{ height: u(0, 556) }} />
      <div className="relative" style={{ paddingInline: u(0, 17.6), paddingBottom: u(0, 12) }}>
        <GroupTitle icon={<Smartphone size={u(0, 22)} strokeWidth={1.8} />} label="手机客户端下载" />
        <div className="grid grid-cols-2" style={{ gap: `${u(0, 8.8)}px ${u(0, 10)}px`, marginTop: u(0, 11) }}>
          <PhoneButton icon={<AppleLogo size={u(0, 24.6)} />} title="苹果手机下载" sub="使用Safari浏览器下载" onClick={a.ios} />
          <PhoneButton icon={<AndroidRobot size={u(0, 24.6)} />} title="安卓手机下载" sub="使用手机浏览器下载" onClick={() => a.get("android")} />
          <PhoneButton icon={<AndroidRobot size={u(0, 24.6)} />} title="安卓备用下载" sub="无法下载时使用此渠道" onClick={a.androidBackup} />
        </div>
        <div style={{ marginTop: u(0, 10) }}><GroupTitle icon={<MonitorSmartphone size={u(0, 22)} strokeWidth={1.8} />} label="电脑客户端下载" /></div>
        <div className="grid grid-cols-2 relative" style={{ gap: u(0, 10), marginTop: u(0, 10) }}>
          <PhonePlatformButton icon={<WindowsGlyph size={u(0, 16)} />} label="Windows 下载" onClick={() => a.get("windows-x86_64")} />
          <PhonePlatformButton icon={<AppleLogo size={u(0, 16)} plain />} label="macOS 下载" caret onClick={() => a.setMacOpen(!a.macOpen)} />
          {a.macOpen && <MacChooser chip={a.chip} onPick={(p) => { a.setMacOpen(false); a.get(p); }} onClose={() => a.setMacOpen(false)} style={{ position: "absolute", left: "50%", transform: "translateX(-50%)", top: u(0, 52), zIndex: 30 }} />}
        </div>
      </div>
    </div>
  );
}

function GroupTitle({ icon, label }: { icon: ReactNode; label: string }) {
  const { u } = useScale();
  return (
    <div className="flex items-center font-bold text-[#fff8ed]" style={{ gap: u(0, 8), fontSize: u(0, 16.7) }}>
      <span className="text-[#f3c675]">{icon}</span>{label}
      <span className="flex-1 h-px" style={{ marginLeft: u(0, 6), background: "linear-gradient(90deg,rgba(240,190,100,.8),transparent)" }} />
    </div>
  );
}

/** 手机客户端下载 buttons, measured on the reference: 197 × 55, radius 13.5, a warm radial fill and a pale-gold border. */
function PhoneButton({ icon, title, sub, onClick }: { icon: ReactNode; title: string; sub: string; onClick: () => void }) {
  const { u } = useScale();
  return (
    <button type="button" onClick={onClick} className="flex items-center text-left active:brightness-110"
      style={{ height: u(0, 55.2), borderRadius: u(0, 13.5), padding: `${u(0, 7)}px ${u(0, 10.6)}px`, gap: u(0, 8.8), border: "1px solid #ffdea6",
        background: "radial-gradient(112% 162% at 98% 0%, #5b3009 0%, #331c05 50%, #1f1104 75%, #0b0702 100%)" }}>
      <span className="shrink-0 flex items-center justify-center" style={{ width: u(0, 24.6), height: u(0, 24.6) }}>{icon}</span>
      <span className="min-w-0">
        <span className="block font-extrabold truncate" style={{ backgroundImage: "linear-gradient(180deg,#fff3b0,#f6c94e)", WebkitBackgroundClip: "text", backgroundClip: "text", color: "transparent", fontSize: u(0, 16), lineHeight: 1.35 }}>{title}</span>
        <span className="block text-white truncate" style={{ fontSize: u(0, 12.8), lineHeight: 1.4 }}>{sub}</span>
      </span>
    </button>
  );
}

function PhonePlatformButton({ icon, label, caret, onClick }: { icon: ReactNode; label: string; caret?: boolean; onClick: () => void }) {
  const { u } = useScale();
  return (
    <button type="button" onClick={onClick} className="flex items-center justify-center font-bold text-[#1b1206]"
      style={{ height: u(0, 42), borderRadius: u(0, 10), gap: u(0, 6), fontSize: u(0, 15), background: GOLD_FILL, border: "1px solid rgba(255,236,190,.65)" }}>
      {icon}{label}{caret && <ChevronDown size={u(0, 15)} />}
    </button>
  );
}

function DeskPlatformButton({ icon, label, caret, onClick }: { icon: ReactNode; label: string; caret?: boolean; onClick: () => void }) {
  const { u } = useScale();
  return (
    <button type="button" onClick={onClick} className="flex items-center justify-center font-bold text-[#1b1206] hover:brightness-105"
      style={{ width: u(238, 0), height: u(68, 0), borderRadius: u(12, 0), gap: u(10, 0), fontSize: u(20, 0), background: GOLD_FILL, border: "1px solid rgba(255,236,190,.65)" }}>
      {icon}{label}{caret && <ChevronDown size={u(18, 0)} />}
    </button>
  );
}

/** "选择 Mac 芯片版本": Apple silicon or Intel; `chip` marks this Mac's when its browser told. */
function MacChooser({ chip, onPick, onClose, style }: { chip: "arm" | "x86" | null; onPick: (p: string) => void; onClose: () => void; style?: CSSProperties }) {
  const { u } = useScale();
  const s = (d: number) => u(d, d);
  const option = (badge: ReactNode, title: string, sub: string, p: string, mine: boolean) => (
    <button type="button" onClick={() => onPick(p)} className="w-full flex items-center text-left rounded-[12px] border border-white/10 bg-white/[.04] hover:border-[#f2c56d]/60"
      style={{ height: s(64), paddingInline: s(14), gap: s(12) }}>
      <span className="rounded-[10px] bg-[#1d1d1f] border border-white/10 flex items-center justify-center font-bold text-[#d0cfcb] shrink-0" style={{ width: s(38), height: s(38), fontSize: s(14) }}>{badge}</span>
      <span className="min-w-0">
        <span className="flex items-center font-bold text-[#fff8ed]" style={{ fontSize: s(14), gap: s(6) }}>
          {title}{mine && <span className="rounded-full bg-[#f2c56d] text-[#1b1206] font-bold" style={{ fontSize: s(10.5), padding: `${s(1)}px ${s(6)}px` }}>本机</span>}
        </span>
        <span className="block text-[#c3bdb3] truncate" style={{ fontSize: s(12) }}>{sub}</span>
      </span>
    </button>
  );
  return (
    <div role="dialog" aria-label="选择 Mac 芯片版本" className="rounded-[16px] border border-[#f2c56d]/40 bg-[#14110c] shadow-[0_20px_60px_rgba(0,0,0,.6)]"
      style={{ width: s(366), padding: s(16), ...style }}>
      <div className="flex items-center justify-between">
        <h2 className="font-bold text-[#fff8ed]" style={{ fontSize: s(16) }}>选择 Mac 芯片版本</h2>
        <button type="button" onClick={onClose} aria-label="关闭" className="rounded-full flex items-center justify-center text-white/70 hover:text-white" style={{ width: s(32), height: s(32) }}><X size={s(18)} /></button>
      </div>
      <div className="flex flex-col" style={{ marginTop: s(10), gap: s(8) }}>
        {option("M", "Apple 芯片（M 系列）", "适用于 M1 / M2 / M3 / M4 及更新的 Mac", "darwin-aarch64", chip === "arm")}
        {option(<span style={{ fontSize: s(10) }}>x86</span>, "Intel 芯片", "适用于搭载 Intel 处理器的 Mac", "darwin-x86_64", chip === "x86")}
      </div>
    </div>
  );
}

/**
 * "扫码下载": a QR code of /app/download, which hands the phone that scans it its own file (the APK on Android, the
 * install steps on an iPhone).
 */
function QrCard() {
  const { u } = useScale();
  const svg = useMemo(() => renderSVG(`${window.location.origin}/app/download`, { border: 1 }), []);
  return (
    <div className="absolute flex items-center" style={{ left: u(75, 0), top: u(400, 0), width: u(288, 0), height: u(150, 0), padding: u(12, 0), gap: u(16, 0), borderRadius: u(16, 0),
      border: "1px solid rgba(240,196,110,.6)", background: "linear-gradient(135deg,rgba(74,50,20,.9),rgba(22,15,6,.92))" }}>
      <div className="bg-white shrink-0 [&_svg]:w-full [&_svg]:h-full" style={{ width: u(112, 0), height: u(112, 0), borderRadius: u(10, 0), padding: u(6, 0) }}
        role="img" aria-label="下载页二维码" dangerouslySetInnerHTML={{ __html: svg }} />
      <div>
        <div className="font-bold text-[#fffb79]" style={{ fontSize: u(22, 0) }}>扫码下载</div>
        <div className="text-white" style={{ marginTop: u(6, 0), fontSize: u(13, 0), lineHeight: 1.6 }}>手机扫码<br />立即开启精彩App</div>
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ 内容亮点 */

const HIGHLIGHTS: { kind: EmblemKind; title: string; sub: string }[] = [
  { kind: "clapper", title: "高能剧情", sub: "秒速反转，上头不停" },
  { kind: "flame", title: "持续上新", sub: "热门内容，天天有新" },
  { kind: "heart", title: "个性推荐", sub: "越看越懂你，刷剧更爽" },
];

type EmblemKind = "clapper" | "flame" | "heart" | "tap" | "reel";

/**
 * A gold medal like the reference's icons: a glossy gold symbol in a gold ring. `orbit` (内容亮点) adds an orbit and a
 * sparkle; `ticks` (沉浸式体验) breaks the ring at the four points and adds two sparkles.
 */
function GoldEmblem({ kind, size, frame = "orbit" }: { kind: EmblemKind; size: number; frame?: "orbit" | "ticks" }) {
  const id = useSvgId("em");
  const gold = `url(#${id}g)`, ring = `url(#${id}r)`;
  const ink = "#3a2205";
  return (
    <svg viewBox="0 0 64 64" width={size} height={size} aria-hidden="true" className="shrink-0 overflow-visible" style={{ filter: "drop-shadow(0 0 6px rgba(255,186,72,.45))" }}>
      <defs>
        <linearGradient id={`${id}g`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#fff4c4" /><stop offset=".45" stopColor="#f7c84f" /><stop offset="1" stopColor="#b8741a" /></linearGradient>
        <linearGradient id={`${id}r`} x1="0" y1="0" x2="1" y2="1"><stop offset="0" stopColor="#fff0b0" /><stop offset=".5" stopColor="#d9962e" /><stop offset="1" stopColor="#fbe08e" /></linearGradient>
      </defs>
      <circle cx="32" cy="32" r="26" fill="#0b0703" />
      {frame === "orbit" ? (
        <>
          <circle cx="32" cy="32" r="26" fill="none" stroke={ring} strokeWidth="3.2" />
          <circle cx="32" cy="32" r="22.6" fill="none" stroke={ring} strokeWidth="0.7" opacity="0.5" />
          <ellipse cx="32" cy="32" rx="32" ry="9" fill="none" stroke={ring} strokeWidth="1.4" transform="rotate(-32 32 32)" strokeDasharray="26 43.5" strokeDashoffset="13" />
          <path d="M53 6.5l1 3.4 3.4 1-3.4 1-1 3.4-1-3.4-3.4-1 3.4-1Z" fill="#fff6d6" />
          <circle cx="9.5" cy="50.5" r="1.3" fill="#ffe7a6" />
        </>
      ) : (
        <>
          <circle cx="32" cy="32" r="27" fill="none" stroke={ring} strokeWidth="2.8" strokeDasharray="39.4 3" strokeDashoffset="-1.5" />
          <circle cx="32" cy="32" r="23" fill="none" stroke={ring} strokeWidth="0.7" opacity="0.45" />
          <path d="M51.5 12.5l.8 2.7 2.7.8-2.7.8-.8 2.7-.8-2.7-2.7-.8 2.7-.8Z" fill="#fff6d6" />
          <path d="M12.5 46.5l.7 2.2 2.2.7-2.2.7-.7 2.2-.7-2.2-2.2-.7 2.2-.7Z" fill="#ffe7a6" />
        </>
      )}
      <g transform={kind === "tap" ? undefined : "translate(32 33) scale(1.18) translate(-32 -33)"}>
      {kind === "clapper" && (
        <>
          <g transform="rotate(-13.4 31 25)">
            <rect x="18.3" y="22.4" width="25.4" height="5.2" rx="1" fill={gold} />
            <path d="M22 22.4h3.2l-2.6 5.2h-3.2Zm7.6 0h3.2l-2.6 5.2h-3.2Zm7.6 0h3.2l-2.6 5.2h-3.2Z" fill={ink} opacity="0.85" />
          </g>
          <rect x="19" y="30.8" width="26" height="17" rx="2.6" fill={gold} />
          <path d="M22.4 30.8h3.2l-2.4 4.2H20Zm7.4 0H33l-2.4 4.2h-3.2Zm7.4 0h3.2L38 35h-3.2Z" fill={ink} opacity="0.6" />
          <path d="M29.4 36.8 37.4 41.6 29.4 46.4Z" fill={ink} />
        </>
      )}
      {kind === "flame" && (
        <>
          <path d="M32 14C33.4 21.5 42 25 42 35.5 42 43 37.6 48.5 32 48.5S22 43.6 22 37C22 31.6 25.2 28.2 28 25.6 28 29.8 29.6 32 31.4 32.6 30.2 26.8 30.8 19.8 32 14Z" fill={gold} />
          <path d="M32.6 31.5C33.8 35.8 37.6 37.3 37.6 41.6 37.6 44.8 35.4 47.2 32.6 47.2S27.6 45.1 27.6 42.3C27.6 39.8 29.3 38.4 30.6 37.4 30.9 39.1 31.6 40.1 32.5 40.4 31.8 37.4 32 34.2 32.6 31.5Z" fill="#a8620f" opacity="0.85" />
        </>
      )}
      {kind === "tap" && (
        <>
          {/* Traced from the reference's 一滑即播 icon (final medal coordinates, not scaled): one thick tap arc over
              a slim upright index finger, three folded fingers stepping down to the right, the thumb as a rounded
              lobe pointing down-left, and a round palm. */}
          <path d="M22.15 21.58A7.4 7.4 0 0 1 36.59 22.22" fill="none" stroke={gold} strokeWidth="2.3" strokeLinecap="round" />
          <path d="M26.6 32.9V23A2.7 2.7 0 0 1 32 23V28.9C32 27.3 36.1 27.3 36.1 29.4C36.2 28.7 39.9 28.7 39.9 31C40 31.1 43.3 31.1 43.3 33.4V40.5C43.3 44.6 40 47.3 35.6 47.3C31.4 47.3 28.2 45.4 26.4 42.6L21.9 38.2C20.6 36.2 22.2 33.6 24.6 34.4Z" fill={gold} />
          <path d="M32 29.4V33M36.1 30.2V33.6M39.9 31.6V34.6" stroke={ink} strokeWidth="0.6" strokeLinecap="round" opacity="0.3" />
          <path d="M28 23.8V30.5" stroke="#fff" strokeWidth="0.9" strokeLinecap="round" opacity="0.38" />
        </>
      )}
      {kind === "reel" && (
        <>
          <path d="M41.5 43.5c4 3.2 8 3.6 11 1.6" fill="none" stroke={gold} strokeWidth="2.2" strokeLinecap="round" />
          <circle cx="32" cy="33" r="13" fill={gold} />
          {[-90, -18, 54, 126, 198].map((a) => (
            <circle key={a} cx={32 + 7 * Math.cos((a * Math.PI) / 180)} cy={33 + 7 * Math.sin((a * Math.PI) / 180)} r="3.1" fill={ink} />
          ))}
          <circle cx="32" cy="33" r="1.6" fill={ink} />
        </>
      )}
      {kind === "heart" && (
        <>
          <path d="M32 47.5C21.6 40.3 17 34.4 17 28.3 17 23.6 20.6 20 25.1 20 28.2 20 30.7 21.8 32 24.3 33.3 21.8 35.8 20 38.9 20 43.4 20 47 23.6 47 28.3 47 34.4 42.4 40.3 32 47.5Z" fill={gold} />
          <ellipse cx="24.5" cy="25.5" rx="4.2" ry="2.4" transform="rotate(-35 24.5 25.5)" fill="#fff" opacity="0.5" />
        </>
      )}
      </g>
    </svg>
  );
}

/**
 * 内容亮点, placed as on the reference. Desktop (1440 × 186): three medals at x 209 / 603 / 1004 with their text 24 px
 * to the right, gold dividers at x 513 / 911, a black band between darker margins. Phones (440 × 258): three rows
 * 83 px apart, medal at x 131, text at x 188, faint gold rules between rows.
 */
function Highlights() {
  const { wide, u } = useScale();
  const title: CSSProperties = { backgroundImage: "linear-gradient(180deg,#fff1c2,#e8b45a)", WebkitBackgroundClip: "text", backgroundClip: "text", color: "transparent" };
  const item = (h: (typeof HIGHLIGHTS)[number]) => (
    <>
      <GoldEmblem kind={h.kind} size={u(68, 54)} />
      <div>
        <div className="font-bold whitespace-nowrap" style={{ ...title, fontSize: u(22.5, 17.5), lineHeight: 1.3 }}>{h.title}</div>
        <div className="text-[#c9c6c0] whitespace-nowrap" style={{ fontSize: u(15, 12.5), marginTop: u(6, 4) }}>{h.sub}</div>
      </div>
    </>
  );
  const edge = <div className="absolute inset-x-0 h-px" style={{ background: GOLD_LINE }} />;
  if (wide) {
    return (
      <div className="relative bg-[#0d0d0d]" style={{ height: u(186, 0) }}>
        <div className="absolute inset-y-0 bg-black" style={{ left: u(62, 0), right: u(65, 0) }} />
        <div className="absolute inset-x-0 top-0">{edge}</div>
        {HIGHLIGHTS.map((h, i) => (
          <div key={h.title} className="absolute flex items-center" style={{ left: u([204.5, 598.5, 999.5][i]!, 0), top: 0, bottom: 0, gap: u(19.5, 0) }}>{item(h)}</div>
        ))}
        {[513, 911].map((x) => (
          <div key={x} className="absolute w-px" style={{ left: u(x, 0), top: u(60, 0), height: u(66, 0), background: "linear-gradient(180deg,transparent,rgba(240,190,100,.65),transparent)" }} />
        ))}
        <div className="absolute inset-x-0 bottom-0">{edge}</div>
      </div>
    );
  }
  return (
    <div className="relative bg-black" style={{ height: u(0, 258) }}>
      <div className="absolute inset-x-0 top-0">{edge}</div>
      {HIGHLIGHTS.map((h, i) => (
        <div key={h.title} className="absolute flex items-center" style={{ left: u(0, 126), top: u(0, 43 + 83 * i) - u(0, 30), height: u(0, 60), gap: u(0, 8) }}>{item(h)}</div>
      ))}
      {[87.5, 170].map((y) => (
        <div key={y} className="absolute h-px" style={{ top: u(0, y), left: u(0, 75), width: u(0, 292), background: "linear-gradient(90deg,transparent,rgba(240,190,100,.35),transparent)" }} />
      ))}
      <div className="absolute inset-x-0 bottom-0">{edge}</div>
    </div>
  );
}

/* ------------------------------------------------------------------ 剧场类型 */

const sectionBg = (a = 0.18): CSSProperties => ({
  background: `${glow("50%", "0%", a, "55%")}, ${glow("0%", "30%", a * 0.6, "35%")}, ${glow("100%", "30%", a * 0.6, "35%")}, radial-gradient(ellipse at 50% 100%, rgba(150,95,20,.25), transparent 55%), #050505`,
});

function Genres({ covers }: { covers: VideoCard[] }) {
  const { wide, u } = useScale();
  const categories = useCategories();
  const art = useArt();
  const list = (categories.data ?? []).filter((c) => c.status === "active").slice(0, 4);
  return (
    <div className="relative isolate text-center" style={{ ...sectionBg(), height: u(864, 676), paddingTop: u(70, 26) }}>
      <ArtBackground url={art.bg("genres_bg")} shade={art.bg("genres_bg") ? "rgba(0,0,0,.25)" : undefined} />
      <OrnateTitle size={u(72, 40)} lineW={u(110, 48)}>剧场类型</OrnateTitle>
      <div className="font-bold" style={{ ...GOLD_TEXT, filter: "none", fontFamily: SERIF, fontSize: u(30, 16), marginTop: u(22, 10) }}>多种题材，一站看全</div>
      <p className="text-white/70" style={{ fontSize: u(17, 12), marginTop: u(16, 8) }}>总有一部，正合你的口味</p>
      {/* Up to four channels, centred (four in a row on desktop, two per row on phones). */}
      <div className="mx-auto flex flex-wrap justify-center" style={{ width: u(1076, 404), columnGap: u(32, 10), rowGap: u(0, 12), marginTop: u(64, 22) }}>
        {list.map((c, i) => (
          <div key={c.slug} style={{ width: wide ? u(245, 0) : u(0, 197) }}>
            <GenreCard slug={c.slug} name={c.name} description={c.description} art={art.one(`genre_${i + 1}`)} fallback={nth(covers, i)} />
          </div>
        ))}
      </div>
    </div>
  );
}

function GenreCard({ slug, name, description, art, fallback }: {
  slug: string; name: string; description: string | null; art?: { cover: string; title: string }; fallback?: VideoCard;
}) {
  const { u } = useScale();
  const top = useListingPage(slug, "hot", 1, 1);
  const v = art ?? top.data?.items[0] ?? fallback;
  return (
    <Link to={`/c/${slug}`} className="group relative block overflow-hidden text-center"
      style={{ height: u(340, 262), borderRadius: u(12, 10), border: "1.5px solid rgba(240,196,110,.55)", background: "linear-gradient(180deg,#1b1309,#0a0704)", boxShadow: "0 0 24px rgba(255,170,60,.12)" }}>
      <Cover v={v} className="w-full transition-transform duration-300 group-hover:scale-[1.03]" style={{ height: u(236, 180) }} />
      <div className="absolute inset-x-0" style={{ top: u(150, 112), height: u(90, 70), background: "linear-gradient(180deg,transparent,#0f0a05)" }} />
      <div className="relative font-black" style={{ ...GOLD_TEXT, fontFamily: SERIF, fontSize: u(26, 18), marginTop: -u(30, 22) }}>{name}</div>
      <p className="text-white/80 line-clamp-2 mx-auto" style={{ fontSize: u(14, 11.5), lineHeight: 1.6, marginTop: u(10, 6), paddingInline: u(18, 10) }}>
        {description || `精选${name}，更新不停`}
      </p>
    </Link>
  );
}

/* ------------------------------------------------------------------ 沉浸式体验 */

const EXPERIENCE: { kind: EmblemKind; title: string; sub: string }[] = [
  { kind: "tap", title: "一滑即播", sub: "上下滑动切换剧情，丝滑流畅" },
  { kind: "heart", title: "猜你喜欢", sub: "根据喜好智能推荐，越看越合口味" },
  { kind: "reel", title: "多元题材", sub: "都市、古风、悬疑、穿越、逆袭……多种题材任你探索" },
];

/**
 * 沉浸式体验. The text column as on the reference, desktop (1440 px) / phones (440 px): the title at x 185 / 18 with a
 * small gold circle and rule after it; the gold subtitle; the white description (two lines on desktop); three medals
 * (60 / 50 px) 90 / 70 px apart with their title and line at x 272 / 80.
 */
function Experience({ covers }: { covers: VideoCard[] }) {
  const { wide, u } = useScale();
  const art = useArt();
  const lightGold: CSSProperties = { backgroundImage: "linear-gradient(180deg,#fff0c0,#e9b85a)", WebkitBackgroundClip: "text", backgroundClip: "text", color: "transparent" };
  const text = (
    <>
      <div className="absolute flex items-center" style={{ left: u(185, 18), top: u(72, 6), gap: u(18, 9) }}>
        <GoldTitle size={u(72, 30)} className="italic">沉浸式体验</GoldTitle>
        <span className="rounded-full shrink-0" style={{ width: u(16, 10), height: u(16, 10), border: `${u(2.5, 2)}px solid #e9b85a` }} />
        <span className="h-px" style={{ marginLeft: -u(18, 9), width: u(120, 90), background: "linear-gradient(90deg,#e9b85a,transparent)" }} />
      </div>
      <div className="absolute whitespace-nowrap text-[#e9b85d]" style={{ left: u(181, 16), top: u(184, 54), fontSize: u(32, 13), lineHeight: 1.3 }}>上下滑动，沉浸刷剧</div>
      <p className="absolute whitespace-nowrap font-bold text-[#dcdcdc]" style={{ left: u(181, 16), top: u(244, 80), fontSize: u(21, 13.5), lineHeight: 1.65, letterSpacing: wide ? "0.2em" : "0.06em" }}>
        {wide ? <>轻松滑动切换，拒绝等待<br />精彩剧情，一刷就停不下来</> : "轻松滑动切换，拒绝等待精彩剧情，一刷就停不下来"}
      </p>
      {EXPERIENCE.map((f, i) => {
        // Medal rings 60 / 50 px across (the ring fills 54/64 of the medal), centred where the reference's are.
        const cy = u(410 + 90 * i, 149 + 70 * i), size = u(71, 59), cx = u(215, 43);
        return (
          <div key={f.title}>
            <div className="absolute" style={{ left: cx - size / 2, top: cy - size / 2 }}><GoldEmblem kind={f.kind} size={size} frame="ticks" /></div>
            <div className="absolute" style={{ left: u(272, 80), top: cy - u(26, 23) }}>
              <div className="font-bold whitespace-nowrap" style={{ ...lightGold, fontSize: u(19, 17), lineHeight: 1.3 }}>{f.title}</div>
              <div className="text-[#c9c6c0] whitespace-nowrap" style={{ fontSize: u(15, 12), lineHeight: 1.45, marginTop: u(8, 5) }}>
                {wide && f.kind === "reel" ? <>都市、古风、悬疑、穿越、逆<br />袭……多种题材任你探索</> : f.sub}
              </div>
            </div>
          </div>
        );
      })}
    </>
  );
  // A phone showing a series, flanked by two tilted posters each side, on a glowing stage.
  const cx = u(925, 220), base = u(680, 560);
  const pw = u(222, 104), ph = u(442, 206);
  const side = (dx: number, w: number, h: number, i: number, tilt: number) => (
    <Cover key={dx} v={art.one(`experience_poster_${i}`) ?? nth(covers, i)} className="absolute" style={{ left: cx + dx - w / 2, top: base - h - u(34, 16), width: w, height: h, borderRadius: u(12, 6),
      border: "1.5px solid rgba(240,196,110,.6)", transform: `perspective(${u(900, 400)}px) rotateY(${tilt}deg)`, boxShadow: "0 0 30px rgba(255,170,60,.25)" }} />
  );
  return (
    <div className="relative isolate overflow-hidden" style={{ ...sectionBg(0.14), height: u(864, 598) }}>
      <ArtBackground url={art.bg("experience_bg")} shade={art.bg("experience_bg") ? "linear-gradient(90deg, rgba(5,5,5,.55), transparent 55%)" : undefined} />
      {text}
      <div className="absolute pointer-events-none" style={{ left: cx - u(460, 210), top: base - u(70, 34), width: u(920, 420), height: u(180, 84), borderRadius: "50%",
        background: "radial-gradient(ellipse, rgba(255,186,72,.45), rgba(255,150,40,.12) 45%, transparent 70%)", boxShadow: "0 0 0 2px rgba(255,200,110,.18) inset" }} />
      {[1, 0.78, 0.58].map((r) => (
        <div key={r} className="absolute rounded-[50%] pointer-events-none" style={{ left: cx - u(420, 190) * r, top: base - u(30, 14) - u(70, 32) * r, width: u(840, 380) * r, height: u(140, 64) * r, border: "1px solid rgba(255,200,110,.35)" }} />
      ))}
      {side(-u(270, 120), u(92, 42), u(290, 128), 1, 28)}
      {side(-u(180, 80), u(86, 38), u(280, 122), 2, 22)}
      {side(u(180, 80), u(86, 38), u(280, 122), 3, -22)}
      {side(u(270, 120), u(92, 42), u(290, 128), 4, -28)}
      <div className="absolute bg-[#0d0d0d]" style={{ left: cx - pw / 2, top: base - ph - u(10, 6), width: pw, height: ph, borderRadius: u(32, 16), padding: u(6, 3),
        border: "1.5px solid rgba(240,196,110,.75)", boxShadow: "0 0 50px rgba(255,170,60,.35)" }}>
        <Cover v={art.one("experience_screen") ?? nth(covers, 0)} className="w-full h-full" style={{ borderRadius: u(26, 13) }} />
        <div className="absolute left-1/2 -translate-x-1/2 bg-black rounded-full" style={{ top: u(14, 7), width: u(70, 32), height: u(16, 7) }} />
        <div className="absolute flex flex-col items-center text-white" style={{ right: u(16, 8), bottom: u(80, 38), gap: u(14, 7) }}>
          {[Flame, Play, Share].map((Icon, i) => <Icon key={i} size={u(20, 10)} className="drop-shadow" />)}
        </div>
      </div>
      {!wide && <div className="absolute inset-x-0 bottom-0 h-px" style={{ background: GOLD_LINE }} />}
    </div>
  );
}

/* ------------------------------------------------------------------ 心愿榜 */

type WishItem = { id: number; title: string; cover: string | null; heat: number };

function Wishlist({ covers, onWish }: { covers: VideoCard[]; onWish: () => void }) {
  const { wide, u } = useScale();
  const wishArt = useArt().bg("wishlist_bg");
  const ranks = useRanks("recommend");
  const items: WishItem[] = useMemo(() => {
    const ranked = (ranks.data?.items ?? []).map((r) => ({ id: r.video_id, title: r.title, cover: r.cover, heat: r.metrics.play }));
    const extra = covers.filter((v) => !ranked.some((r) => r.id === v.id)).map((v) => ({ id: v.id, title: v.title, cover: v.cover, heat: v.hot }));
    const all = [...ranked, ...extra];
    return Array.from({ length: Math.min(7, Math.max(all.length, 0)) }, (_, i) => all[i]!).filter(Boolean);
  }, [ranks.data, covers]);
  // Rank 1 in the middle, 2 and 3 either side, smaller ones outwards.
  const slots = [
    { rank: 6, dx: -502, w: 75, h: 160, top: 478 }, { rank: 4, dx: -368, w: 90, h: 190, top: 450 }, { rank: 2, dx: -224, w: 155, h: 270, top: 410 },
    { rank: 1, dx: 0, w: 240, h: 370, top: 330 }, { rank: 3, dx: 224, w: 155, h: 270, top: 410 }, { rank: 5, dx: 368, w: 90, h: 190, top: 450 }, { rank: 7, dx: 502, w: 75, h: 160, top: 478 },
  ];
  const pk = wide ? 1 : 0.43; // phones: the same row at 43 %
  const cx = u(720, 220);
  const crown = ["#ffd75e", "#e7ecf5", "#f0a271"];
  return (
    <div className="relative isolate overflow-hidden text-center" style={{ ...sectionBg(0.2), height: u(956, 403), paddingTop: u(24, 18) }}>
      <ArtBackground url={wishArt} shade={wishArt ? "rgba(0,0,0,.2)" : undefined} />
      <GoldTitle size={u(84, 30)}>《心愿榜》</GoldTitle>
      <div className="font-bold" style={{ ...GOLD_TEXT, filter: "none", fontFamily: SERIF, fontSize: u(30, 13), marginTop: u(14, 6) }}>你想看的故事，由你来定</div>
      <p className="text-white/80 mx-auto" style={{ fontSize: u(17, 11), marginTop: u(16, 6), maxWidth: u(1000, 380), lineHeight: 1.6 }}>
        为喜欢的题材投票，人气最高的故事优先制作上线；你的每一次期待，都会被看见。
      </p>
      <div className="absolute pointer-events-none rounded-[50%]" style={{ left: cx - u(560, 200), top: u(640, 300), width: u(1120, 400), height: u(130, 50), background: "radial-gradient(ellipse, rgba(255,120,60,.35), rgba(255,170,60,.12) 50%, transparent 72%)" }} />
      {slots.map((s) => {
        const it = items[s.rank - 1];
        if (!it) return null;
        const w = u(s.w, s.w * pk), h = u(s.h, s.h * pk), top = u(s.top, (s.top - 150) * pk + 105), left = cx + u(s.dx, s.dx * pk) - w / 2;
        return (
          <div key={s.rank} className="absolute" style={{ left, top, width: w, height: h, zIndex: 10 - s.rank }}>
            {s.rank <= 3 && (
              <Crown className="absolute left-1/2 -translate-x-1/2" size={u(s.rank === 1 ? 70 : 46, s.rank === 1 ? 30 : 20)} strokeWidth={1.6}
                style={{ top: -u(s.rank === 1 ? 62 : 42, s.rank === 1 ? 27 : 18), color: crown[s.rank - 1], fill: `${crown[s.rank - 1]}55`, filter: `drop-shadow(0 0 8px ${crown[s.rank - 1]})` }} />
            )}
            <div className="relative w-full h-full overflow-hidden" style={{ borderRadius: u(12, 6), border: `${s.rank === 1 ? 2 : 1.5}px solid rgba(246,200,110,.85)`, boxShadow: "0 0 30px rgba(255,170,60,.3)" }}>
              <Cover v={it} className="absolute inset-0 w-full h-full" />
              <div className="absolute inset-x-0 bottom-0 text-center" style={{ padding: u(10, 4), background: "linear-gradient(180deg,transparent,rgba(0,0,0,.85))" }}>
                {(s.rank <= 3 || wide) && <div className="font-bold text-[#fff1cf] truncate" style={{ fontSize: u(s.rank === 1 ? 17 : 12, s.rank === 1 ? 9 : 7) }}>{it.title}</div>}
                <div className="flex items-center justify-center text-[#f2c56d]" style={{ gap: u(4, 2), fontSize: u(s.rank === 1 ? 14 : 11, s.rank === 1 ? 8 : 6), marginTop: u(3, 1) }}>
                  <Flame size={u(s.rank === 1 ? 14 : 11, 7)} />{fmtCount(it.heat)}
                </div>
              </div>
            </div>
          </div>
        );
      })}
      <div className="absolute left-1/2 -translate-x-1/2" style={{ top: u(782, 344) }}>
        <GoldButton icon={<Crown size={u(18, 14)} />} label="我要许愿" onClick={onWish} w={u(300, 327)} h={u(61, 48)} font={u(20, 16)} />
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ 创作者计划 */

function Creator({ covers, contact }: { covers: VideoCard[]; contact: string }) {
  const { wide, u } = useScale();
  const uploaded = useArt().bg("creator");
  const external = /^(https?:|mailto:)/.test(contact);
  // The picture fills the section, as on the reference: an uploaded one, or a series cover warmed towards gold.
  // The text sits over its darker left side.
  const shade = uploaded
    ? `linear-gradient(90deg, rgba(5,5,5,.75) 0%, rgba(5,5,5,.35) ${wide ? 40 : 55}%, transparent ${wide ? 65 : 85}%)`
    : `linear-gradient(90deg, #050505 10%, rgba(5,5,5,.82) ${wide ? 34 : 45}%, rgba(5,5,5,.2) ${wide ? 68 : 85}%, rgba(5,5,5,.05)), radial-gradient(ellipse at 75% 45%, rgba(255,176,56,.18), transparent 55%), linear-gradient(0deg, rgba(5,5,5,.7), transparent 30%)`;
  return (
    <div className="relative isolate overflow-hidden" style={{ height: u(768, 356), background: "#050505" }}>
      <ArtBackground url={uploaded ?? nth(covers, 5)?.cover} warm={!uploaded} shade={shade} />
      <div style={{ paddingLeft: u(205, 34), paddingTop: u(95, 48) }}>
        <GoldTitle size={u(66, 28)} className="italic">创作者计划</GoldTitle>
        <div className="font-bold text-white" style={{ fontFamily: SERIF, fontSize: u(36, 15), marginTop: u(26, 14) }}>把你的故事，拍成下一部热剧</div>
        <p className="text-white/80" style={{ fontSize: u(20, 12), lineHeight: 1.8, marginTop: u(26, 12) }}>投稿剧本、创意或作品，获得更多曝光与分成；<br />让更多人看到你的故事。</p>
        <div style={{ marginTop: u(56, 30) }}>
          <GoldButton icon={<Lightbulb size={u(18, 12)} />} label="了解创作者计划" href={external ? contact : undefined} to={external ? undefined : contact}
            w={u(300, 164)} h={u(67, 35)} font={u(20, 13)} />
        </div>
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ 关于我们 */

function About({ covers }: { covers: VideoCard[] }) {
  const { u } = useScale();
  const uploaded = useArt().bg("about");
  return (
    <div className="relative isolate overflow-hidden flex items-center justify-center" style={{ height: u(347, 106) }}>
      {uploaded ? <ArtBackground url={uploaded} shade="rgba(10,6,0,.3)" /> : (
        <>
          <div className="absolute inset-0 -z-10 flex" style={{ filter: "sepia(.75) saturate(1.1) brightness(.5)" }}>
            {Array.from({ length: 6 }, (_, i) => <Cover key={i} v={nth(covers, i + 3)} className="flex-1 h-full" />)}
          </div>
          <div className="absolute inset-0 -z-10" style={{ background: "radial-gradient(ellipse at 50% 50%, rgba(255,180,60,.2), transparent 65%)" }} />
        </>
      )}
      {/* Logo, name and slogan in a dark box, as on the reference. */}
      <div className="flex items-center" style={{ gap: u(26, 9), padding: `${u(26, 9)}px ${u(46, 14)}px`, borderRadius: u(14, 6),
        background: "rgba(8,6,3,.8)", border: "1px solid rgba(240,196,110,.3)", boxShadow: "0 10px 40px rgba(0,0,0,.5)" }}>
        <BrandMark fit={u(112, 44)} />
        <div>
          <div className="font-black leading-none whitespace-nowrap" style={{ ...GOLD_TEXT, fontFamily: SERIF, fontSize: u(84, 28), letterSpacing: "0.05em" }}>{BRAND}</div>
          <div className="italic text-right" style={{ ...GOLD_TEXT, fontFamily: SERIF, fontSize: u(22, 8), marginTop: u(6, 2) }}>AI · Short Drama</div>
        </div>
        <span className="self-stretch w-px" style={{ background: "linear-gradient(180deg,transparent,rgba(240,190,100,.7),transparent)" }} />
        <p className="font-bold text-[#f6e6c4] whitespace-nowrap" style={{ fontFamily: SERIF, fontSize: u(24, 10.5), lineHeight: 1.7 }}>打开{BRAND}，<br />遇见更多好故事</p>
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ footer */

function LandingFooter({ onAbout }: { onAbout: () => void }) {
  const { wide, u } = useScale();
  const site = useSite().data;
  const origin = window.location.origin;
  const contact = site?.telegram || (site?.email ? `mailto:${site.email}` : "/go-home");
  const addresses: [string, string][] = [];
  if (site?.mirrors[0]) addresses.push(["最新地址：", site.mirrors[0]]);
  addresses.push(["官网地址：", site?.permanent_domain || origin]);
  addresses.push(["永久发布页：", `${origin}/go-home`]);
  const linkCls = "text-white/35 hover:text-white";
  const cols: { title: string; links: { label: string; href?: string; onClick?: () => void }[] }[] = [
    { title: "服务支持", links: [{ label: "常见问题", href: "/go-home" }, { label: "用户反馈", href: contact }] },
    { title: "关于我们", links: [{ label: "公司简介", onClick: onAbout }, { label: "联系我们", href: contact }] },
    { title: "法律信息", links: [{ label: "用户协议", href: "/terms" }, { label: "隐私政策", href: "/privacy" }] },
  ];
  return (
    <footer className="bg-[#121212] border-t border-white/10" style={{ padding: wide ? `${u(38, 0)}px ${u(40, 0)}px ${u(30, 0)}px` : `${u(0, 22)}px ${u(0, 12)}px ${u(0, 22)}px` }}>
      <div className={wide ? "flex justify-between" : ""}>
        <div>
          <Wordmark scale={u(7.5, 7.15)} />
          <p className="text-white/35" style={{ fontSize: u(12, 12), marginTop: u(12, 10) }}>随时随地，畅看好剧</p>
          {wide && (
            <div className="flex flex-col" style={{ marginTop: u(14, 0), gap: u(8, 0), fontSize: u(12, 0) }}>
              {addresses.map(([label, url]) => (
                <div key={label} className="text-white/45">{label}<a href={url} className="text-[#ffd21c] hover:underline">{url.replace(/\/$/, "")}</a></div>
              ))}
            </div>
          )}
        </div>
        <div className="grid grid-cols-3" style={{ width: wide ? u(720, 0) : "100%", marginTop: wide ? 0 : u(0, 22) }}>
          {cols.map((c) => (
            <div key={c.title}>
              <div className="font-bold text-white" style={{ fontSize: u(14, 12) }}>{c.title}</div>
              <div className="flex flex-col" style={{ marginTop: u(16, 12), gap: u(13, 9), fontSize: u(14, 12) }}>
                {c.links.map((l) => l.onClick
                  ? <button key={l.label} type="button" onClick={l.onClick} className={`${linkCls} text-left`}>{l.label}</button>
                  : <a key={l.label} href={l.href} target={/^(https?:|mailto:)/.test(l.href!) ? "_blank" : undefined} rel="noreferrer" className={linkCls}>{l.label}</a>)}
              </div>
            </div>
          ))}
        </div>
      </div>
      <p className={`text-white/35 ${wide ? "text-right" : "text-center"}`} style={{ fontSize: u(12, 11), marginTop: u(46, 30) }}>Copyright © {new Date().getFullYear()} {BRAND}. All Rights Reserved.</p>
    </footer>
  );
}

/* ------------------------------------------------------------------ iPhone steps */

/** iPhone has no store build: install the web app from Safari's share sheet. */
/**
 * 苹果手机安装. iPhones can't install app files from a website, so 立即下载 (/app/download/ios) gives a profile that puts
 * the site on the home screen as a full-screen app, and the steps walk through installing it in Settings. When App 版本
 * has a published iPhone install link (`external`, from an outside signing service), the same button leads there.
 * Profiles install only from Safari; on a computer the sheet shows a QR code to scan with the iPhone instead.
 */
function IosHelp({ device, external, onClose }: { device: Device; external: boolean; onClose: () => void }) {
  const toast = useUi((s) => s.toast);
  const onIphone = device.os === "ios";
  const page = `${window.location.origin}/app/home`;
  const qr = useMemo(() => (onIphone ? "" : renderSVG(`${window.location.origin}/app/download`, { border: 1 })), [onIphone]);
  const copy = () => navigator.clipboard?.writeText(page).then(() => toast("网址已复制，请在 Safari 中打开", "ok")).catch(() => toast(page));
  const steps: [ReactNode, string][] = external
    ? [
        [<Download key="a" size={18} />, "点击「立即下载」，按页面提示完成安装"],
        [<Settings key="b" size={18} />, "如提示「未受信任的开发者」：设置 → 通用 → VPN与设备管理，点开开发者并信任"],
        [<Play key="c" size={18} />, `回到桌面，点开「${BRAND}」即可观看`],
      ]
    : [
        [<Download key="a" size={18} />, "点击「立即下载」，在弹出的提示中选择「允许」"],
        [<Settings key="b" size={18} />, "打开手机「设置」，点击顶部的「已下载描述文件」"],
        [<ShieldCheck key="c" size={18} />, "点击右上角「安装」，输入锁屏密码后确认"],
        [<Play key="d" size={18} />, `回到桌面，点开「${BRAND}」即可全屏观看`],
      ];
  const button = "h-11 rounded-full text-[14px] font-bold flex items-center justify-center gap-1.5";
  return (
    <div className="fixed inset-0 z-[80] flex items-end sm:items-center justify-center bg-black/70" role="dialog" aria-modal="true" aria-label="苹果手机安装说明" onClick={onClose}>
      <div className="w-full sm:w-[420px] max-h-[92dvh] overflow-y-auto rounded-t-[20px] sm:rounded-[20px] border border-[#f2c56d]/40 bg-[#14110c] p-5 pb-7" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between">
          <h2 className="text-[17px] font-bold" style={{ ...GOLD_TEXT, filter: "none" }}>苹果手机安装</h2>
          <button type="button" onClick={onClose} aria-label="关闭" className="w-8 h-8 rounded-full flex items-center justify-center text-white/70"><X size={18} /></button>
        </div>
        <p className="mt-1 text-[12.5px] leading-relaxed text-white/60">
          {external ? "按提示安装苹果版 App。" : `iPhone 无需安装包：装好描述文件后，主屏幕会出现「${BRAND}」图标，点开就像 App 一样全屏观看。`}
        </p>
        {!onIphone && (
          <div className="mt-4 flex items-center gap-4 rounded-[14px] border border-[#f2c56d]/30 bg-white/[.03] p-3">
            <div className="w-[104px] h-[104px] shrink-0 rounded-[10px] bg-white p-1.5 [&_svg]:w-full [&_svg]:h-full" role="img" aria-label="苹果手机下载二维码" dangerouslySetInnerHTML={{ __html: qr }} />
            <p className="text-[13px] leading-relaxed text-white/85">用 iPhone 的<b className="text-[#f2c56d]">相机</b>扫描二维码，在 Safari 中打开后按下面的步骤安装。</p>
          </div>
        )}
        {onIphone && !device.safari && (
          <p className="mt-3 rounded-[12px] border border-[#f2c56d]/40 bg-[#f2c56d]/10 px-3 py-2 text-[13px] leading-relaxed text-[#f6d48c]">
            请先复制网址，用 <b>Safari 浏览器</b>打开本页再下载：其他浏览器无法安装。
          </p>
        )}
        <ol className="mt-4 space-y-3">
          {steps.map(([icon, text], i) => (
            <li key={i} className="flex items-center gap-3 text-[14px] text-white/90">
              <span className="w-9 h-9 rounded-full flex items-center justify-center text-[#f6cf7a] shrink-0" style={{ border: "1px solid rgba(240,196,110,.7)", background: "#1f170b" }}>{icon}</span>
              <span><b className="text-[#f2c56d] mr-1">{i + 1}.</b>{text}</span>
            </li>
          ))}
        </ol>
        {onIphone && (
          <div className="mt-5 grid grid-cols-2 gap-3">
            <button type="button" onClick={copy} className={`${button} border border-[#f2c56d]/70 text-[#f2c56d]`}><Copy size={16} />复制网址</button>
            {device.safari
              ? <a href="/app/download/ios" className={`${button} text-[#1b1206]`} style={{ background: GOLD_FILL }}><Download size={16} />立即下载</a>
              : <button type="button" onClick={onClose} className={`${button} text-[#1b1206]`} style={{ background: GOLD_FILL }}>知道了</button>}
          </div>
        )}
        {!external && (
          <div className="mt-4 space-y-1.5 text-[11.5px] leading-relaxed text-white/45">
            <p>描述文件只添加这一个主屏幕图标，不读取手机里的任何数据，可随时在 设置 → 通用 → VPN与设备管理 中删除。</p>
            <p className="flex items-center gap-1 flex-wrap">也可以在 Safari 中点<Share size={12} className="inline" />分享 →<SquarePlus size={12} className="inline" />「添加到主屏幕」。</p>
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * WeChat, QQ and other apps' built-in browsers block downloads, so this points at their ··· menu and its
 * 在浏览器打开 (在 Safari 中打开 on iPhones). Tapping anywhere closes it.
 */
function InAppGuide({ device, onClose }: { device: Device; onClose: () => void }) {
  const toast = useUi((s) => s.toast);
  const link = `${window.location.origin}/app/download`;
  const browser = device.os === "ios" ? "Safari" : "浏览器";
  return (
    <div className="fixed inset-0 z-[90] bg-black/90" role="dialog" aria-modal="true" aria-label={`请在${browser}中打开`} onClick={onClose}>
      <svg className="absolute right-[14px] top-[4px]" width="110" height="128" viewBox="0 0 110 128" fill="none" stroke="#f2c56d" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M16 122C22 74 52 38 92 14" strokeDasharray="8 8" />
        <path d="M74 9 94 13 88 33" />
      </svg>
      <div className="mx-auto mt-[150px] w-[min(86vw,360px)] text-center text-white">
        <p className="text-[19px] font-bold leading-relaxed">
          点击右上角 <span className="inline-flex items-center justify-center align-middle rounded-full border border-white/70 px-2 h-[24px] text-[16px] leading-none tracking-[2px]">···</span>
        </p>
        <p className="mt-2 text-[17px]">选择「<b className="text-[#f8ca06]">{device.os === "ios" ? "在 Safari 中打开" : "在浏览器打开"}</b>」</p>
        <p className="mt-5 text-[13px] leading-relaxed text-white/65">{device.weChat ? "微信" : "当前应用"}内无法下载安装包，在{browser}中打开后，会自动为你的手机匹配对应的安装包。</p>
        <button type="button" onClick={(e) => { e.stopPropagation(); navigator.clipboard?.writeText(link).then(() => toast(`链接已复制，请粘贴到${browser}打开`, "ok")).catch(() => toast(link)); }}
          className="mt-6 h-11 px-6 rounded-full text-[14px] font-bold text-[#1b1206] inline-flex items-center gap-1.5" style={{ background: GOLD_FILL }}>
          <Copy size={16} />复制下载链接
        </button>
      </div>
    </div>
  );
}
