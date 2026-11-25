import { unwrap } from "@reelguo/api-client";
import { Img, Spinner } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { BadgeCheck, Crown, Flame, Search as SearchIcon, Sparkles, Star, TrendingUp } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { Trans } from "react-i18next";
import { api, useCategories, useListingPage, useRanks, useSearch, useSeriesList, useSite, useTopic, useTopics, type ChannelSort, type RankKind } from "../api";
import { fmtCompact, fmtCount } from "../player/format";
import { href, Link, navigate, useLocation } from "../router";
import { t, tList, type Faq as FaqItem } from "../i18n";
import { useUi } from "../state/ui";
import { SearchPanel, useSearchSubmit, Wordmark } from "./chrome";
import { TopicTile } from "./Home";
import { Breadcrumb, Container, EmptyState, Faq, LoadMore, PageTitle, Pager, Panel, PillTabs, PosterGrid, PosterSkeleton, UnderlineTabs } from "./kit";
import { BRAND, BRAND_SHORT } from "../brand";

const SORTS = [["latest", t("channel.sortLatest")], ["hot", t("channel.sortHot")], ["picks", t("channel.sortPicks")]] as const;
type SortKey = (typeof SORTS)[number][0] | "original";
const PAGE_SIZE = 42;

const channelFaq = (name: string) => tList<FaqItem>("channel.faq", { name, brand: BRAND });

/* ---------------------------------------------------------------------------------------------
 * Channel: /c/:slug (/c/all = every channel, ?sort=original = 黄月原创): "【名称】 介绍", pill tabs, 7 × 6 grid,
 * numbered pages and an FAQ, as on the reference.
 * ------------------------------------------------------------------------------------------- */

export function ChannelPage({ slug }: { slug: string }) {
  const { query } = useLocation();
  const categories = useCategories();
  const raw = query.get("sort");
  const sort = (raw === "original" || SORTS.some(([k]) => k === raw) ? raw : "latest") as SortKey;
  const page = Math.max(1, Number(query.get("page")) || 1);
  const all = slug === "all";
  const channel = categories.data?.find((c) => c.slug === slug);
  const comingSoon = channel?.status === "coming_soon";
  const name = sort === "original" ? t("nav.original", { short: BRAND_SHORT }) : all ? t("nav.allDramas") : channel?.name ?? "";
  const list = useListingPage(slug, sort as ChannelSort, page, PAGE_SIZE, all || (!!channel && !comingSoon));
  const items = list.data?.items ?? [];
  const pg = list.data?.pagination;
  const to = (s: string, p = 1) => {
    const q = new URLSearchParams();
    if (s !== "latest") q.set("sort", s);
    if (p > 1) q.set("page", String(p));
    const qs = q.toString();
    return `/c/${slug}${qs ? `?${qs}` : ""}`;
  };

  if (!all && categories.data && !channel) {
    return <Container><Breadcrumb items={[{ label: t("nav.home"), to: "/" }, { label: t("channel.crumb") }]} /><EmptyState title={t("channel.missing")} /></Container>;
  }
  return (
    <Container>
      <Breadcrumb items={[{ label: t("nav.home"), to: "/" }, { label: name }]} />
      <h1 className="mt-3 lg:mt-[9px] text-[16px] lg:text-[20px] leading-[26px] lg:leading-[30px] text-white">
        {t("common.bracketed", { text: name })}<span className="text-white/65">{channel?.description || t("channel.intro")}</span>
      </h1>
      {comingSoon ? (
        <Panel className="mt-6 p-10 text-center">
          <Sparkles size={28} className="mx-auto text-primary" />
          <div className="mt-3 text-[18px] font-bold text-white">{t("common.comingSoon")}</div>
          <p className="mt-2 text-[13px] text-white/60">{t("channel.comingSoonSub")}</p>
        </Panel>
      ) : (
        <>
          {sort !== "original" && (
            <div className="mt-[7px]">
              <PillTabs tabs={SORTS} active={sort as Exclude<SortKey, "original">} onChange={(k) => navigate(to(k), { replace: true, keepScroll: true })} />
            </div>
          )}
          <div className="mt-4 lg:mt-[22px]">
            {list.isPending ? <PosterSkeleton count={14} /> : items.length === 0 ? <EmptyState title={t("common.empty")} sub={t("common.emptySub")} /> : <PosterGrid items={items} />}
          </div>
          {pg && <Pager page={pg.page} pages={pg.pages} onPage={(n) => { navigate(to(sort, n)); window.scrollTo(0, 0); }} />}
          <Faq items={channelFaq(name)} />
        </>
      )}
    </Container>
  );
}

/* ---------------------------------------------------------------------------------------------
 * 专题: "全部专题" with a 3-column tile grid, 相关推荐 and an FAQ; one topic as a card grid.
 * ------------------------------------------------------------------------------------------- */

const TOPIC_FAQ = tList<FaqItem>("topics.faq");

export function TopicsPage() {
  const topics = useTopics(48);
  const related = useSeriesList("hot", 7);
  const items = topics.data?.items ?? [];
  return (
    <Container>
      <Breadcrumb items={[{ label: t("nav.home"), to: "/" }, { label: t("nav.topics") }]} />
      <PageTitle right={topics.data ? t("common.countTitles", { count: topics.data.pagination.total }) : undefined}
        sub={t("topics.intro", { brand: BRAND })}>
        {t("topics.title")}
      </PageTitle>
      <div className="mt-[18px]">
        {topics.isPending ? <div className="flex justify-center py-12"><Spinner /></div>
          : items.length === 0 ? <EmptyState title={t("topics.empty")} sub={t("topics.emptySub")} />
          : <div className="grid grid-cols-1 lg:grid-cols-3 gap-3 lg:gap-x-[18px] lg:gap-y-[19px]">{items.map((t) => <TopicTile key={t.id} t={t} />)}</div>}
      </div>
      {(related.data?.items.length ?? 0) > 0 && (
        <section className="pt-8 lg:pt-10">
          <h2 className="mb-[14px] text-[16px] lg:text-[1.875rem] font-semibold text-white leading-[1.3]">{t("topics.related")}</h2>
          <PosterGrid items={related.data!.items.slice(0, 7)} />
        </section>
      )}
      <Faq items={TOPIC_FAQ} open />
    </Container>
  );
}

export function TopicPage({ id }: { id: number }) {
  const topic = useTopic(id);
  return (
    <Container>
      <Breadcrumb items={[{ label: t("nav.home"), to: "/" }, { label: t("nav.topics"), to: href.topics() }, { label: topic.data?.title ?? t("nav.topics") }]} />
      {topic.isError ? <EmptyState title={t("topics.missing")} /> : (
        <>
          <PageTitle sub={topic.data?.description} right={topic.data ? t("common.countTitles", { count: topic.data.items.length }) : undefined}>{topic.data?.title ?? " "}</PageTitle>
          <div className="mt-[18px]">{topic.isPending ? <PosterSkeleton /> : <PosterGrid items={topic.data!.items} />}</div>
        </>
      )}
    </Container>
  );
}

/* ---------------------------------------------------------------------------------------------
 * 排行榜: icon + title + one line, underline tabs, then 20 rows (cover with a TOP badge, meta, synopsis, heat).
 * ------------------------------------------------------------------------------------------- */

const BOARDS: { kind: RankKind; name: string; sub: string; metric: string; icon: typeof Flame }[] = [
  { kind: "hot", name: t("ranks.hot"), sub: t("ranks.hotSub"), metric: t("ranks.hotMetric"), icon: Flame },
  { kind: "recommend", name: t("ranks.recommend"), sub: t("ranks.recommendSub"), metric: t("ranks.recommendMetric"), icon: Crown },
  { kind: "potential", name: t("ranks.potential"), sub: t("ranks.potentialSub"), metric: t("ranks.potentialMetric"), icon: TrendingUp },
];

const RANK_FAQ = tList<FaqItem>("ranks.faq");

const fmtHeat = fmtCompact;

function RankBadge({ n }: { n: number }) {
  return (
    <span className="absolute top-0 left-0 w-[27px] h-9 flex flex-col items-center pt-[3px] text-[#1b1909] font-bold leading-none z-10"
      style={{ background: n <= 3 ? "linear-gradient(180deg,#ffe27a,#ffc21c)" : "linear-gradient(180deg,#e8e8e8,#a9a9a9)", clipPath: "polygon(0 0,100% 0,100% 100%,50% 84%,0 100%)" }}>
      <i className="not-italic text-[7.5px]">TOP</i>
      <span className="text-[16.5px] tabular-nums">{String(n).padStart(2, "0")}</span>
    </span>
  );
}

export function RanksPage({ kind }: { kind: RankKind }) {
  const board = BOARDS.find((b) => b.kind === kind) ?? BOARDS[0]!;
  const ranks = useRanks(board.kind);
  const items = (ranks.data?.items ?? []).slice(0, 20);
  const Icon = board.icon;
  return (
    <Container>
      <Breadcrumb items={[{ label: t("nav.home"), to: "/" }, { label: t("nav.ranks"), to: href.ranks() }, { label: board.name }]} />
      <div className="mt-3 lg:mt-[14px] flex flex-wrap items-center gap-x-[9px] gap-y-1">
        <span className="w-[30px] h-[30px] rounded-[10.5px] bg-[#ffd21c]/[.12] border border-[#ffd21c]/25 text-[#ffd21c] flex items-center justify-center"><Icon size={15} /></span>
        <h1 className="text-[20px] lg:text-[1.875rem] font-semibold leading-[39px] text-white">{board.name}</h1>
        <span className="text-[12px] lg:text-[14px] text-white/65">{board.sub}</span>
      </div>
      <div className="mt-[15px]">
        <UnderlineTabs big tabs={BOARDS.map((b) => [b.kind, b.name] as const)} active={board.kind} onChange={(k) => navigate(href.ranks(k), { replace: true })} />
      </div>
      <div className="mt-[15px] flex flex-col gap-[11px]">
        {ranks.isPending ? <div className="flex justify-center py-12"><Spinner /></div>
          : items.length === 0 ? <EmptyState title={t("ranks.empty")} sub={t("common.checkLater")} />
          : items.map((r, i) => (
            <div key={r.video_id} className="flex gap-3 lg:gap-4 p-[10px] lg:p-[13px] rounded-[12px] bg-white/5 border border-white/10">
              <Link to={href.video(r.video_id)} className="relative w-[90px] h-[120px] lg:w-[123px] lg:h-[165px] rounded-[12px] lg:rounded-[0.9rem] overflow-hidden bg-[#242424] shrink-0">
                <Img src={r.cover} />
                <RankBadge n={i + 1} />
                <span className="absolute right-[6px] bottom-[6px] h-6 lg:h-7 px-[5px] rounded-[4.8px] bg-black/80 text-[11px] lg:text-[14px] text-white flex items-center">
                  {r.is_finished ? t("series.progressAll", { count: r.episode_count }) : t("series.progressUpTo", { count: r.episode_count })}
                </span>
              </Link>
              <div className="flex-1 min-w-0 pt-0.5">
                <Link to={href.video(r.video_id)} className="block text-[15px] lg:text-[1.125rem] font-semibold leading-[21px] text-white truncate hover:text-primary">{r.title}</Link>
                <div className="mt-[7px] lg:mt-[9px] flex items-center flex-wrap gap-x-[14px] gap-y-1 text-[12px] lg:text-[14px] font-medium leading-4 text-white/80">
                  <span>{r.cate_name.replace(/^AI成人/, "")}</span>
                  {r.tags.slice(0, 3).map((t) => <Link key={t} to={href.search(t)} className="hover:text-white">{t}</Link>)}
                  {r.score > 0 && <span className="flex items-center gap-0.5 font-semibold text-[#ffd21c]">{t("common.score", { score: r.score.toFixed(1) })}<Star size={8} className="fill-current" /></span>}
                </div>
                {r.description && <p className="mt-[7px] text-[12px] lg:text-[14px] leading-[18px] lg:leading-[21px] text-white/90 lg:text-white clamp-2 lg:clamp-3 max-w-[944px]">{r.description}</p>}
              </div>
              <div className="hidden lg:flex items-center shrink-0">
                <span className="w-px h-[68px] bg-white/10" />
                <div className="w-[126px] pl-8 flex flex-col">
                  <span className="flex items-center gap-1 text-[21px] font-semibold leading-[30px] text-white tabular-nums"><Flame size={13} className="text-[#ff7a1a]" />{fmtHeat(r.metric_value)}</span>
                  <span className="text-[14px] leading-[18px] text-white/65">{board.metric}</span>
                </div>
              </div>
            </div>
          ))}
      </div>
      <Faq items={RANK_FAQ} open small />
    </Container>
  );
}

/* ---------------------------------------------------------------------------------------------
 * 博客 (热点吃瓜): title and line, a split tab bar, horizontal article cards, numbered pages, FAQ.
 * ------------------------------------------------------------------------------------------- */

const BLOG_FAQ = tList<FaqItem>("blog.faq");

export function PostsPage() {
  const { query } = useLocation();
  const tab = query.get("tab") === "hot" ? "hot" : "all";
  const page = Math.max(1, Number(query.get("page")) || 1);
  const size = 12;
  const posts = useQuery({
    queryKey: ["posts-page", page, size],
    queryFn: async () => unwrap(await api.GET("/api/v1/posts", { params: { query: { page, size } } })),
    placeholderData: (prev) => prev,
  });
  const items = [...(posts.data?.items ?? [])];
  if (tab === "hot") items.sort((a, b) => b.hot - a.hot);
  const pg = posts.data?.pagination;
  const go = (p: number, t = tab) => navigate(`/posts${p > 1 || t === "hot" ? `?${new URLSearchParams({ ...(t === "hot" ? { tab: "hot" } : {}), ...(p > 1 ? { page: String(p) } : {}) })}` : ""}`);

  return (
    <Container>
      <Breadcrumb items={[{ label: t("nav.home"), to: "/" }, { label: t("nav.blog") }]} />
      <h1 className="mt-3 lg:mt-[7px] text-[20px] lg:text-[26px] font-semibold leading-[39px] text-white">{t("nav.blog")}</h1>
      <p className="mt-2 lg:mt-[16px] text-[12px] lg:text-[14px] leading-[18px] text-white/65">{t("blog.intro")}</p>
      <nav className="mt-[17px] h-[46px] grid grid-cols-2 rounded-[10px] bg-[#141416] border border-[#242428]">
        {([["all", t("blog.all")], ["hot", t("blog.hot")]] as const).map(([k, label]) => (
          <button key={k} onClick={() => go(1, k)} aria-current={tab === k ? "page" : undefined}
            className={`text-[14px] font-medium ${tab === k ? "text-[#ffd21c]" : "text-[#909096] hover:text-white"}`}>{label}</button>
        ))}
      </nav>
      <div className="mt-6 flex flex-col gap-4 lg:gap-[20px]">
        {posts.isPending ? <div className="flex justify-center py-12"><Spinner /></div>
          : items.length === 0 ? <EmptyState title={t("blog.empty")} sub={t("common.checkLater")} />
          : items.map((p) => (
            <Link key={p.id} to={href.post(p.id)} className="group flex flex-col lg:flex-row gap-3 lg:gap-[15px] p-3 lg:p-4 rounded-[12px] bg-[#171719] border border-white/10 hover:border-white/20">
              <span className="relative w-full lg:w-[348px] aspect-video rounded-[9px] overflow-hidden bg-[#1a1918] shrink-0"><Img src={p.cover} /></span>
              <span className="flex-1 min-w-0 lg:pt-[10px]">
                <span className="flex items-center gap-2 text-[12px] lg:text-[14px] leading-[19px] text-[#a9aebc]">
                  <span className="w-[23px] h-[23px] rounded-full bg-[#ffd21c] text-black text-[13px] font-bold flex items-center justify-center">R</span>
                  {t("nav.gossip", { short: BRAND_SHORT })}<span>·</span>{p.published_at ? new Date(p.published_at).toLocaleDateString("sv-SE") : ""}{p.tags[0] && <><span>·</span>{p.tags[0]}</>}
                </span>
                <span className="block mt-[14px] text-[16px] lg:text-[21px] font-semibold leading-[24px] lg:leading-[30px] text-[#f1f2f5] clamp-2 group-hover:text-primary">{p.title}</span>
                {p.summary && <span className="block mt-[9px] text-[13px] lg:text-[14px] leading-5 text-[#a9aebc] clamp-2">{p.summary}</span>}
                <span className="mt-[9px] flex gap-5 text-[13px] lg:text-[14px] leading-[25px] text-[#a9aebc]">
                  <span>{t("blog.reads", { count: fmtCount(p.hot) })}</span>
                </span>
              </span>
            </Link>
          ))}
      </div>
      {pg && <Pager page={pg.page} pages={pg.pages} onPage={(n) => { go(n); window.scrollTo(0, 0); }} />}
      <Faq items={BLOG_FAQ} />
    </Container>
  );
}

/* ---------------------------------------------------------------------------------------------
 * 回家的路: hero with the three actions, then panels: addresses, official domains, ways to stay in touch,
 * troubleshooting, device advice and the official entry, as on the reference.
 * ------------------------------------------------------------------------------------------- */

const GOLD = "linear-gradient(90deg,#ffd21c 0px,#f5a623 100%)";

function WayPanel({ title, note, sub, children }: { title: string; note?: string; sub?: string; children: ReactNode }) {
  return (
    <section className="rounded-[8px] lg:rounded-[6px] bg-white/[.04] border border-white/10 p-[12px] lg:px-6 lg:py-8">
      <h2 className="flex items-center flex-wrap gap-x-[11px] gap-y-1">
        <span className="w-9 h-9 rounded-full bg-[#ffd21c]/10 text-[#ffd21c] flex items-center justify-center lg:mr-[2px]"><Sparkles size={16} /></span>
        <span className="text-[18px] lg:text-[1.625rem] font-semibold leading-[27px] text-[#eee]">{title}</span>
        {note && <span className="text-[14px] lg:text-[1.25rem] leading-[21px] text-[#ffd21c]">{note}</span>}
      </h2>
      {sub && <p className="mt-[14px] text-[13px] lg:text-[1.25rem] leading-[20px] lg:leading-[26px] text-[#888]">{sub}</p>}
      <div className="mt-[22px]">{children}</div>
    </section>
  );
}

const card = "rounded-[6px] bg-[#0d0d0d] border border-white/10";
/** This page keeps 16 px from the screen edges on phones (the site's column has 13 px). */
const WAY_GUTTER = "max-lg:px-[3px]";
/** The hero's three actions: one row of equal pills on phones (40 px, 15/600), 178.5 × 45 on desktop (21/600). */
const wayBtn = "h-[40px] lg:h-[45px] lg:w-[178.5px] rounded-full border text-[15px] lg:text-[21px] font-semibold flex items-center justify-center whitespace-nowrap";
const outlineBtn = "h-9 rounded-full border border-[#ffd21c]/50 text-[#ffd21c] text-[12px] font-semibold flex items-center justify-center hover:bg-[#ffd21c]/10";

export function WayHomePage() {
  const site = useSite();
  const toast = useUi((s) => s.toast);
  const s = site.data;
  const origin = window.location.origin;
  const bare = (u: string) => u.replace(/^https?:\/\//, "").replace(/\/$/, "");
  const lines = Array.from(new Set([origin, ...(s?.mirrors ?? [])]));
  const pageUrl = `${origin}${href.wayHome()}`;
  const copy = (text: string, done = t("way.copied")) => navigator.clipboard?.writeText(text).then(() => toast(done, "ok")).catch(() => toast(text));
  const domains: [string, string][] = [];
  if (s?.permanent_domain) domains.push([t("way.officialSite"), bare(s.permanent_domain)]);
  domains.push([t("way.addressPage"), bare(pageUrl)]);
  lines.slice(1, 3).forEach((m, i) => domains.push([i === 0 ? t("way.latest") : t("way.backup"), bare(m)]));

  const contacts: { title: string; text: string; action: ReactNode }[] = [];
  if (s?.email) contacts.push({ title: t("way.emailTitle"), text: t("way.emailText"), action: <button onClick={() => copy(s.email!)} className={`${outlineBtn} w-full`}>{s.email}</button> });
  if (s?.telegram) contacts.push({ title: t("way.tgTitle"), text: t("way.tgText"), action: <a href={s.telegram} target="_blank" rel="noreferrer" className={`${outlineBtn} w-full`}>{t("way.tgAction")}</a> });
  contacts.push({ title: t("way.bookmarkTitle"), text: t("way.bookmarkText"), action: <button onClick={() => copy(pageUrl, t("way.bookmarkCopied"))} className={`${outlineBtn} w-full`}>{t("way.bookmarkAction")}</button> });
  contacts.push({ title: t("way.appTitle"), text: t("way.appText"), action: <Link to={href.download()} className={`${outlineBtn} w-full`}>{t("way.appAction")}</Link> });

  return (
    <>
      <Container className={WAY_GUTTER}><Breadcrumb items={[{ label: t("nav.home"), to: "/" }, { label: t("nav.wayHome") }]} /></Container>
      <section className="relative overflow-hidden">
        <div className="absolute inset-0 pointer-events-none" style={{ background: "radial-gradient(45% 60% at 50% 45%, rgba(255,210,28,.12), transparent 70%)" }} />
        {/* Measured on the reference at 440 px (phone) and 1440 px: badge pill, the logo lockup (mark + name + domain),
            title, a narrow paragraph on phones, then the three actions. */}
        <div className={`rg-wrap ${WAY_GUTTER} relative pt-[24px] lg:pt-[42px] pb-[32px] lg:pb-[43px] text-center`}>
          <span className="inline-flex h-[40px] lg:h-[36px] px-[20px] lg:px-[12px] gap-[8px] rounded-[8px] lg:rounded-[12px] bg-[#111] border border-[#ffd21c]/50 text-[#ffd21c] text-[16px] lg:text-[14px] font-medium items-center">
            <BadgeCheck size={15} strokeWidth={2.2} className="shrink-0" />{t("way.badge")}
          </span>
          <div className="mt-[32px] lg:mt-[45px] flex justify-center">
            <span className="lg:hidden"><Wordmark scale={15} /></span>
            <span className="hidden lg:block"><Wordmark scale={11.25} /></span>
          </div>
          <h1 className="mt-[24px] lg:mt-[30px] text-[18px] lg:text-[2.25rem] font-semibold leading-[24.3px] lg:leading-[34px] text-white">{t("way.title", { brand: BRAND })}</h1>
          <p className="mt-[12px] lg:mt-[15px] mx-auto max-w-[320px] lg:max-w-[665px] text-[13px] lg:text-[1.25rem] lg:font-medium leading-[20px] lg:leading-[21px] text-[#888] lg:text-white/50">{t("way.intro", { brand: BRAND })}</p>
          <div className="mt-[28px] lg:mt-[75px] grid grid-cols-3 gap-[10px] lg:flex lg:items-center lg:justify-center lg:gap-[37.5px]">
            <Link to="/" className={`${wayBtn} border-transparent text-black`} style={{ background: GOLD }}>{t("way.enter")}</Link>
            <button onClick={() => copy(pageUrl, t("way.shareCopied"))} className={`${wayBtn} border-[#ffd21c] text-[#ffd21c]`}>{t("way.share")}</button>
            <button onClick={() => copy(pageUrl, t("way.saveCopied"))} className={`${wayBtn} border-transparent text-black`} style={{ background: GOLD }}>{t("way.save")}</button>
          </div>
        </div>
      </section>
      <Container className={`flex flex-col gap-[22px] ${WAY_GUTTER}`}>
        <WayPanel title={t("way.linesTitle")} note={t("way.linesNote")} sub={t("way.linesSub")}>
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-3 lg:gap-[27px]">
            {lines.map((a, i) => (
              <article key={a} className={`${card} p-4 lg:px-[19px] lg:py-4`}>
                <div className="flex items-center text-[14px] lg:text-[1.25rem] font-medium leading-[21px] text-white/50">
                  {t("way.line", { n: String(i + 1).padStart(2, "0") })}
                  <span className={`ml-auto text-[13px] lg:text-[14px] ${a === origin ? "text-[#ffd21c]" : "text-[#2ecc71]"}`}>{a === origin ? t("way.current") : t("way.available")}</span>
                </div>
                <p className="mt-4 lg:mt-[21px] text-[18px] lg:text-[1.625rem] font-semibold leading-[27px] text-[#eee] truncate">{bare(a)}</p>
                <div className="mt-[18px] lg:mt-[25px] flex gap-[15px]">
                  <a href={a} className="w-[150px] h-9 rounded-full text-[12px] font-semibold text-black flex items-center justify-center" style={{ background: GOLD }}>{t("way.go")}</a>
                  <button onClick={() => copy(a)} className={`${outlineBtn} w-[150px]`}>{t("way.copyAddress")}</button>
                </div>
              </article>
            ))}
          </div>
        </WayPanel>

        <WayPanel title={t("way.domainsTitle")} note={t("way.domainsNote")} sub={t("way.domainsSub")}>
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-3 lg:gap-x-[27px] lg:gap-y-[28px]">
            {domains.map(([label, value]) => (
              <article key={label} className={`${card} min-h-[68px] px-[19px] py-3 flex items-center gap-[7px]`}>
                <span className="text-[13px] lg:text-[14px] font-medium text-white/50 shrink-0">{label}</span>
                <span className="text-[13px] lg:text-[14px] font-semibold text-[#eee] truncate">{value}</span>
                <button onClick={() => copy(value)} className={`${outlineBtn} ml-auto w-[120px] lg:w-[150px] shrink-0`}>{t("way.copySave")}</button>
              </article>
            ))}
          </div>
        </WayPanel>

        <WayPanel title={t("way.contactTitle")} note={t("way.contactNote")} sub={t("way.contactSub")}>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3 lg:gap-[18px]">
            {contacts.map((c) => (
              <article key={c.title} className={`${card} p-[19px] flex flex-col`}>
                <h3 className="text-center text-[18px] lg:text-[1.625rem] font-semibold leading-[27px] text-[#eee]">{c.title}</h3>
                <p className="mt-3 text-[13px] lg:text-[14px] font-medium leading-5 text-[#888] flex-1">{c.text}</p>
                <div className="mt-6">{c.action}</div>
              </article>
            ))}
          </div>
        </WayPanel>

        <WayPanel title={t("way.troubleTitle")} note={t("way.troubleNote")} sub={t("way.troubleSub")}>
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-3 lg:gap-[27px]">
            {tList<{ title: string; text: string }>("way.trouble").map(({ title: h, text: p }, i) => ({ n: String(i + 1).padStart(2, "0"), h, p })).map(({ n, h, p }) => (
              <article key={n} className={`${card} px-[19px] py-[22px]`}>
                <h3 className="flex gap-[9px] text-[18px] lg:text-[1.625rem] font-semibold leading-[27px]"><span className="text-[#ffd21c]">{n}</span><span className="text-[#eee]">{h}</span></h3>
                <p className="mt-[19px] text-[13px] lg:text-[14px] font-medium leading-5 text-[#888]">{p}</p>
              </article>
            ))}
          </div>
        </WayPanel>

        <WayPanel title={t("way.devicesTitle")} sub={t("way.devicesSub")}>
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            {tList<{ title: string; ok: string; warn: string }>("way.devices", { brand: BRAND }).map(({ title: h, ok, warn }) => (
              <div key={h} className="lg:pl-[19px]">
                <h3 className="text-[16px] lg:text-[1.375rem] font-semibold text-[#eee]">{h}</h3>
                <p className="mt-6 text-[13px] lg:text-[1.0625rem] leading-[1.6] text-[#888]">{ok.split(/(Safari|Chrome \/ Edge|Chrome)/).map((x, i) => (i % 2 ? <span key={i} className="text-[#2ecc71]">{x}</span> : x))}</p>
                <p className="mt-4 text-[13px] lg:text-[1.0625rem] leading-[1.6] text-[#f5a623]">{warn}</p>
              </div>
            ))}
          </div>
        </WayPanel>

        <section className="rounded-[6px] bg-white/[.04] border border-white/10 p-4 lg:px-[13px] lg:py-6 text-[12px] leading-[1.8] text-white/85">
          <h2 className="text-[14px] lg:text-[1.0625rem] font-semibold text-white">{t("way.entryTitle", { brand: BRAND })}</h2>
          <p className="mt-4"><Trans i18nKey="way.entryText" values={{ brand: BRAND, host: s?.permanent_domain ? bare(s.permanent_domain) : window.location.host }} components={{ host: <span className="text-[#ffd21c]" /> }} /></p>
          <h3 className="mt-5 text-[13px] font-semibold text-white">{t("way.aboutTitle", { brand: BRAND })}</h3>
          <p className="mt-3">{t("way.aboutText", { brand: BRAND })}</p>
          <h3 className="mt-5 text-[13px] font-semibold text-white">{t("way.platformsTitle")}</h3>
          <p className="mt-3"><Trans i18nKey="way.platformsText" components={{ link: <Link to={href.download()} className="text-[#ffd21c]" /> }} /></p>
        </section>
      </Container>
    </>
  );
}

/* ---------------------------------------------------------------------------------------------
 * Search results.
 * ------------------------------------------------------------------------------------------- */

export function SearchPage() {
  const { query } = useLocation();
  const q = (query.get("q") ?? "").trim();
  const [value, setValue] = useState(q);
  useEffect(() => setValue(q), [q]);
  const submit = useSearchSubmit();
  const search = useSearch(q);
  const items = search.data?.pages.flatMap((p) => p.items) ?? [];
  const total = search.data?.pages[0]?.pagination.total ?? 0;
  return (
    <Container>
      <Breadcrumb items={[{ label: t("nav.home"), to: "/" }, { label: t("search.title") }]} />
      <form role="search" className="relative mt-4 max-w-[640px]" onSubmit={(e) => { e.preventDefault(); submit(value); }}>
        <input value={value} onChange={(e) => setValue(e.target.value)} maxLength={50} autoFocus={!q}
          placeholder={t("search.pagePlaceholder")} aria-label={t("search.title")}
          className="w-full h-12 pl-5 pr-28 rounded-full bg-[#161616] border border-white/[.08] focus:border-primary text-[15px] text-on placeholder:text-dim outline-none" />
        <button type="submit" className="absolute right-1.5 top-1.5 h-9 px-5 rounded-full bg-[#ffd321] text-[#14130e] text-[14px] font-semibold flex items-center gap-1.5">
          <SearchIcon size={15} />{t("search.title")}
        </button>
      </form>
      {q ? (
        <>
          <PageTitle sub={search.isPending ? t("search.searching") : t("search.results", { count: total })}>
            {t("search.resultsBefore")}<span className="text-primary">{q}</span>{t("search.resultsAfter")}
          </PageTitle>
          <div className="mt-7">
            {search.isPending ? <PosterSkeleton /> : items.length === 0
              ? <EmptyState title={t("search.noResults")} sub={t("search.noResultsSub")} />
              : <PosterGrid items={items} />}
          </div>
          {items.length > 0 && <LoadMore hasMore={!!search.hasNextPage} loading={search.isFetchingNextPage} onMore={() => void search.fetchNextPage()} />}
        </>
      ) : (
        <div className="mt-6 max-w-[640px] rounded-[16px] bg-[#141414] border border-white/[.06]">
          <SearchPanel onPick={submit} />
        </div>
      )}
    </Container>
  );
}
