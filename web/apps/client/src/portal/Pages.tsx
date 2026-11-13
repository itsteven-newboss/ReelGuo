import { unwrap } from "@reelguo/api-client";
import { Img, Spinner } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { Crown, Flame, Search as SearchIcon, Sparkles, Star, TrendingUp } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { api, useCategories, useListingPage, useRanks, useSearch, useSeriesList, useSite, useTopic, useTopics, type ChannelSort, type RankKind } from "../api";
import { fmtCount } from "../player/format";
import { href, Link, navigate, useLocation } from "../router";
import { useUi } from "../state/ui";
import { SearchPanel, useSearchSubmit } from "./chrome";
import { TopicTile } from "./Home";
import { Breadcrumb, Container, EmptyState, Faq, LoadMore, PageTitle, Pager, Panel, PillTabs, PosterGrid, PosterSkeleton, UnderlineTabs } from "./kit";

const SORTS = [["latest", "最近更新"], ["hot", "当前热播"], ["picks", "精选推荐"]] as const;
type SortKey = (typeof SORTS)[number][0] | "original";
const PAGE_SIZE = 42;

function channelFaq(name: string) {
  return [
    { q: `什么是${name}？`, a: `${name}是 ReelGuo 按题材整理的成人向短剧频道，每部作品分集播放，单集几分钟，适合碎片时间观看。` },
    { q: `${name}多久更新一次？`, a: "频道内容每天更新。「最近更新」按最新一集的上线时间排序，追更的作品会排在前面。" },
    { q: `${name}可以免费看吗？`, a: "大部分剧集的前几集可以免费观看，后续剧集可用金币解锁或开通 VIP 观看全部剧集。" },
    { q: "怎么找到想看的剧？", a: "可以切换「当前热播」「精选推荐」，也可以用页面顶部的搜索按剧名、标签查找，或到排行榜看最热门的作品。" },
  ];
}

/* ---------------------------------------------------------------------------------------------
 * Channel: /c/:slug (/c/all = every channel, ?sort=original = ReelGuo原创): "【名称】 介绍", pill tabs, 7 × 6 grid,
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
  const name = sort === "original" ? "ReelGuo原创" : all ? "全部短剧" : channel?.name ?? "";
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
    return <Container><Breadcrumb items={[{ label: "首页", to: "/" }, { label: "频道" }]} /><EmptyState title="频道不存在" /></Container>;
  }
  return (
    <Container>
      <Breadcrumb items={[{ label: "首页", to: "/" }, { label: name }]} />
      <h1 className="mt-3 lg:mt-[9px] text-[16px] lg:text-[20px] leading-[26px] lg:leading-[30px] text-white">
        【{name}】<span className="text-white/65">{channel?.description || "精选热播短剧，汇集独家原创、精选推荐与最新更新内容。"}</span>
      </h1>
      {comingSoon ? (
        <Panel className="mt-6 p-10 text-center">
          <Sparkles size={28} className="mx-auto text-primary" />
          <div className="mt-3 text-[18px] font-bold text-white">即将上线</div>
          <p className="mt-2 text-[13px] text-white/60">该频道正在筹备中，敬请期待。</p>
        </Panel>
      ) : (
        <>
          {sort !== "original" && (
            <div className="mt-[7px]">
              <PillTabs tabs={SORTS} active={sort as Exclude<SortKey, "original">} onChange={(k) => navigate(to(k), { replace: true, keepScroll: true })} />
            </div>
          )}
          <div className="mt-4 lg:mt-[22px]">
            {list.isPending ? <PosterSkeleton count={14} /> : items.length === 0 ? <EmptyState title="暂无内容" sub="内容上线后将在这里显示" /> : <PosterGrid items={items} />}
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

const TOPIC_FAQ = [
  { q: "专题页里有哪些类型的短剧？", a: "专题按题材和看点整理合集，涵盖 AI 成人短剧、AI 漫剧、原创短剧等，方便按主题集中观看。" },
  { q: "专题和频道有什么区别？", a: "频道是按内容类型长期固定的分类；专题是围绕特定主题挑选的合集，更聚焦、更新更快。" },
  { q: "专题里的短剧可以免费看吗？", a: "专题内的剧集与频道中的相同：大部分剧集的前几集免费，后续剧集可用金币解锁或开通 VIP。" },
  { q: "如何找到最新专题？", a: "专题页优先展示最新和最热门的合集，也可以在首页「专题推荐」进入。" },
];

export function TopicsPage() {
  const topics = useTopics(48);
  const related = useSeriesList("hot", 7);
  const items = topics.data?.items ?? [];
  return (
    <Container>
      <Breadcrumb items={[{ label: "首页", to: "/" }, { label: "专题" }]} />
      <PageTitle right={topics.data ? `共 ${topics.data.pagination.total} 部` : undefined}
        sub="【专题】专题集中展示 ReelGuo 各类精品专题合集，按主题整理，方便你快速找到想看的短剧。专题内容持续更新。">
        全部专题
      </PageTitle>
      <div className="mt-[18px]">
        {topics.isPending ? <div className="flex justify-center py-12"><Spinner /></div>
          : items.length === 0 ? <EmptyState title="暂无专题" sub="专题上线后将在这里显示" />
          : <div className="grid grid-cols-1 lg:grid-cols-3 gap-3 lg:gap-x-[18px] lg:gap-y-[19px]">{items.map((t) => <TopicTile key={t.id} t={t} />)}</div>}
      </div>
      {(related.data?.items.length ?? 0) > 0 && (
        <section className="pt-8 lg:pt-10">
          <h2 className="mb-[14px] text-[16px] lg:text-[1.875rem] font-semibold text-white leading-[1.3]">相关推荐</h2>
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
      <Breadcrumb items={[{ label: "首页", to: "/" }, { label: "专题", to: href.topics() }, { label: topic.data?.title ?? "专题" }]} />
      {topic.isError ? <EmptyState title="专题不存在或已下线" /> : (
        <>
          <PageTitle sub={topic.data?.description} right={topic.data ? `共 ${topic.data.items.length} 部` : undefined}>{topic.data?.title ?? " "}</PageTitle>
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
  { kind: "hot", name: "热播榜", sub: "按站内播放热度排序 · 每 10 分钟更新 · TOP20", metric: "实时热度", icon: Flame },
  { kind: "recommend", name: "推荐榜", sub: "编辑精选与完播率排序 · 每 10 分钟更新 · TOP20", metric: "推荐值", icon: Crown },
  { kind: "potential", name: "飙升榜", sub: "近 14 天上线、播放增长最快 · 每 10 分钟更新 · TOP20", metric: "飙升指数", icon: TrendingUp },
];

const RANK_FAQ = [
  { q: "什么是热播榜？", a: "热播榜综合近 7 天的独立播放、点赞、收藏、评论和完播计算热度，展示全站最热门的 20 部短剧。" },
  { q: "排行榜包含哪些榜单？", a: "排行榜包含热播榜、推荐榜和飙升榜：热播看热度，推荐看编辑精选与完播率，飙升看新剧的播放增长。" },
  { q: "榜单多久更新一次？", a: "三个榜单每 10 分钟按站内最新数据重新计算一次。" },
  { q: "排行榜里的作品可以直接播放吗？", a: "可以。点击任一榜单条目即可进入对应作品的播放页。" },
  { q: "榜单排序会影响分类页吗？", a: "不会。排行榜是独立入口，分类页仍按各频道自己的排序显示。" },
];

const fmtHeat = (n: number) => (n >= 10_000 ? `${(n / 10_000).toFixed(1).replace(/\.0$/, "")}W` : String(Math.round(n)));

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
      <Breadcrumb items={[{ label: "首页", to: "/" }, { label: "排行榜", to: href.ranks() }, { label: board.name }]} />
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
          : items.length === 0 ? <EmptyState title="榜单暂时为空" sub="稍后再来看看" />
          : items.map((r, i) => (
            <div key={r.video_id} className="flex gap-3 lg:gap-4 p-[10px] lg:p-[13px] rounded-[12px] bg-white/5 border border-white/10">
              <Link to={href.video(r.video_id)} className="relative w-[90px] h-[120px] lg:w-[123px] lg:h-[165px] rounded-[12px] lg:rounded-[0.9rem] overflow-hidden bg-[#242424] shrink-0">
                <Img src={r.cover} />
                <RankBadge n={i + 1} />
                <span className="absolute right-[6px] bottom-[6px] h-6 lg:h-7 px-[5px] rounded-[4.8px] bg-black/80 text-[11px] lg:text-[14px] text-white flex items-center">
                  {r.is_finished ? `全${r.episode_count}集` : `更新至${r.episode_count}集`}
                </span>
              </Link>
              <div className="flex-1 min-w-0 pt-0.5">
                <Link to={href.video(r.video_id)} className="block text-[15px] lg:text-[1.125rem] font-semibold leading-[21px] text-white truncate hover:text-primary">{r.title}</Link>
                <div className="mt-[7px] lg:mt-[9px] flex items-center flex-wrap gap-x-[14px] gap-y-1 text-[12px] lg:text-[14px] font-medium leading-4 text-white/80">
                  <span>{r.cate_name.replace(/^AI成人/, "")}</span>
                  {r.tags.slice(0, 3).map((t) => <Link key={t} to={href.search(t)} className="hover:text-white">{t}</Link>)}
                  {r.score > 0 && <span className="flex items-center gap-0.5 font-semibold text-[#ffd21c]">{r.score.toFixed(1)}分<Star size={8} className="fill-current" /></span>}
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

const BLOG_FAQ = [
  { q: "博客频道主要更新什么内容？", a: "博客更新圈内热点、短剧剧评和幕后内容，部分文章会关联对应的短剧，可以直接观看正片。" },
  { q: "文章内容多久更新一次？", a: "编辑每天更新，最新的文章排在最前面。" },
  { q: "怎么找到自己感兴趣的内容？", a: "可以切换「热门文章」查看阅读量最高的内容，也可以用页面顶部的搜索查找。" },
];

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
      <Breadcrumb items={[{ label: "首页", to: "/" }, { label: "博客" }]} />
      <h1 className="mt-3 lg:mt-[7px] text-[20px] lg:text-[26px] font-semibold leading-[39px] text-white">博客</h1>
      <p className="mt-2 lg:mt-[16px] text-[12px] lg:text-[14px] leading-[18px] text-white/65">浏览最新博客文章，发现圈内热点与短剧剧评。</p>
      <nav className="mt-[17px] h-[46px] grid grid-cols-2 rounded-[10px] bg-[#141416] border border-[#242428]">
        {([["all", "全部文章"], ["hot", "热门文章"]] as const).map(([k, label]) => (
          <button key={k} onClick={() => go(1, k)} aria-current={tab === k ? "page" : undefined}
            className={`text-[14px] font-medium ${tab === k ? "text-[#ffd21c]" : "text-[#909096] hover:text-white"}`}>{label}</button>
        ))}
      </nav>
      <div className="mt-6 flex flex-col gap-4 lg:gap-[20px]">
        {posts.isPending ? <div className="flex justify-center py-12"><Spinner /></div>
          : items.length === 0 ? <EmptyState title="暂无文章" sub="稍后再来看看" />
          : items.map((p) => (
            <Link key={p.id} to={href.post(p.id)} className="group flex flex-col lg:flex-row gap-3 lg:gap-[15px] p-3 lg:p-4 rounded-[12px] bg-[#171719] border border-white/10 hover:border-white/20">
              <span className="relative w-full lg:w-[348px] aspect-video rounded-[9px] overflow-hidden bg-[#1a1918] shrink-0"><Img src={p.cover} /></span>
              <span className="flex-1 min-w-0 lg:pt-[10px]">
                <span className="flex items-center gap-2 text-[12px] lg:text-[14px] leading-[19px] text-[#a9aebc]">
                  <span className="w-[23px] h-[23px] rounded-full bg-[#ffd21c] text-black text-[13px] font-bold flex items-center justify-center">R</span>
                  ReelGuo吃瓜<span>·</span>{p.published_at ? new Date(p.published_at).toLocaleDateString("sv-SE") : ""}{p.tags[0] && <><span>·</span>{p.tags[0]}</>}
                </span>
                <span className="block mt-[14px] text-[16px] lg:text-[21px] font-semibold leading-[24px] lg:leading-[30px] text-[#f1f2f5] clamp-2 group-hover:text-primary">{p.title}</span>
                {p.summary && <span className="block mt-[9px] text-[13px] lg:text-[14px] leading-5 text-[#a9aebc] clamp-2">{p.summary}</span>}
                <span className="mt-[9px] flex gap-5 text-[13px] lg:text-[14px] leading-[25px] text-[#a9aebc]">
                  <span>{fmtCount(p.hot)}阅读</span>
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
    <section className="rounded-[6px] bg-white/[.04] border border-white/10 p-4 lg:px-6 lg:py-8">
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
const outlineBtn = "h-9 rounded-full border border-[#ffd21c]/50 text-[#ffd21c] text-[12px] font-semibold flex items-center justify-center hover:bg-[#ffd21c]/10";

export function WayHomePage() {
  const site = useSite();
  const toast = useUi((s) => s.toast);
  const s = site.data;
  const origin = window.location.origin;
  const bare = (u: string) => u.replace(/^https?:\/\//, "").replace(/\/$/, "");
  const lines = Array.from(new Set([origin, ...(s?.mirrors ?? [])]));
  const pageUrl = `${origin}${href.wayHome()}`;
  const copy = (text: string, done = "已复制") => navigator.clipboard?.writeText(text).then(() => toast(done, "ok")).catch(() => toast(text));
  const domains: [string, string][] = [];
  if (s?.permanent_domain) domains.push(["官方网站", bare(s.permanent_domain)]);
  domains.push(["地址发布页", bare(pageUrl)]);
  lines.slice(1, 3).forEach((m, i) => domains.push([i === 0 ? "最新地址" : "备用入口", bare(m)]));

  const contacts: { title: string; text: string; action: ReactNode }[] = [];
  if (s?.email) contacts.push({ title: "邮箱自动回复", text: "发送任意邮件到下方地址，自动回复当前所有可用入口的最新汇总。", action: <button onClick={() => copy(s.email!)} className={`${outlineBtn} w-full`}>{s.email}</button> });
  if (s?.telegram) contacts.push({ title: "TG 官方交流群", text: "获取域名变更通知的首选渠道，实时播报、用户互助。", action: <a href={s.telegram} target="_blank" rel="noreferrer" className={`${outlineBtn} w-full`}>立即订阅最新动态</a> });
  contacts.push({ title: "收藏本页", text: "把回家的路加入浏览器收藏夹，地址变更时打开本页即可找到最新入口。", action: <button onClick={() => copy(pageUrl, "已复制，请添加到收藏夹")} className={`${outlineBtn} w-full`}>复制本页地址</button> });
  contacts.push({ title: "下载 App", text: "App 会自动切换可用线路，不受域名变更影响。", action: <Link to={href.download()} className={`${outlineBtn} w-full`}>前往下载</Link> });

  return (
    <>
      <Container><Breadcrumb items={[{ label: "首页", to: "/" }, { label: "回家的路" }]} /></Container>
      <section className="relative overflow-hidden">
        <div className="absolute inset-0 pointer-events-none" style={{ background: "radial-gradient(45% 60% at 50% 45%, rgba(255,210,28,.12), transparent 70%)" }} />
        <div className="rg-wrap relative pt-10 lg:pt-[42px] pb-10 lg:pb-[43px] text-center">
          <span className="inline-flex h-9 px-[34px] rounded-[12px] bg-[#111] border border-[#ffd21c]/50 text-[#ffd21c] text-[13px] lg:text-[14px] font-medium items-center">官方权威发布 · 永久有效入口</span>
          <div className="mt-10 lg:mt-[56px] flex justify-center"><img src="/icon.svg" alt="" className="w-[72px] h-[72px] rounded-[20px] shadow-[0_0_60px_var(--p-glow)]" /></div>
          <h1 className="mt-6 lg:mt-[33px] text-[22px] lg:text-[2.25rem] font-semibold leading-[34px] text-white">ReelGuo · 官方最新地址发布页</h1>
          <p className="mt-[15px] mx-auto max-w-[665px] text-[14px] lg:text-[1.25rem] font-medium leading-[21px] text-white/50">本页面由 ReelGuo 官方维护，实时同步最新可用线路与备用入口，方便在不同网络环境下快速返回。</p>
          <div className="mt-8 lg:mt-[76px] flex flex-wrap items-center justify-center gap-4 lg:gap-[37px]">
            <Link to="/" className="w-[179px] h-[45px] rounded-full text-[18px] lg:text-[21px] font-semibold text-black flex items-center justify-center" style={{ background: GOLD }}>立即进入主站</Link>
            <button onClick={() => copy(pageUrl, "分享链接已复制")} className="w-[179px] h-[45px] rounded-full border border-[#ffd21c] text-[18px] lg:text-[21px] font-semibold text-[#ffd21c]">复制分享链接</button>
            <button onClick={() => copy(pageUrl, "已复制，请添加到收藏夹或保存到备忘录")} className="w-[179px] h-[45px] rounded-full text-[17px] font-bold text-black" style={{ background: GOLD }}>保存回家的路</button>
          </div>
        </div>
      </section>
      <Container className="flex flex-col gap-[22px]">
        <WayPanel title="最新可用地址" note="（直连无需代理）" sub="以下线路均经过可用性检查，任一地址无法打开时换下一个。">
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-3 lg:gap-[27px]">
            {lines.map((a, i) => (
              <article key={a} className={`${card} p-4 lg:px-[19px] lg:py-4`}>
                <div className="flex items-center text-[14px] lg:text-[1.25rem] font-medium leading-[21px] text-white/50">
                  备用地址 {String(i + 1).padStart(2, "0")}
                  <span className={`ml-auto text-[13px] lg:text-[14px] ${a === origin ? "text-[#ffd21c]" : "text-[#2ecc71]"}`}>{a === origin ? "当前" : "可用"}</span>
                </div>
                <p className="mt-4 lg:mt-[21px] text-[18px] lg:text-[1.625rem] font-semibold leading-[27px] text-[#eee] truncate">{bare(a)}</p>
                <div className="mt-[18px] lg:mt-[25px] flex gap-[15px]">
                  <a href={a} className="w-[150px] h-9 rounded-full text-[12px] font-semibold text-black flex items-center justify-center" style={{ background: GOLD }}>立即进入</a>
                  <button onClick={() => copy(a)} className={`${outlineBtn} w-[150px]`}>复制地址</button>
                </div>
              </article>
            ))}
          </div>
        </WayPanel>

        <WayPanel title="官方域名与地址" note="建议收藏" sub="同时保存永久入口和备用入口，主域名变更时仍可返回。">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-3 lg:gap-x-[27px] lg:gap-y-[28px]">
            {domains.map(([label, value]) => (
              <article key={label} className={`${card} min-h-[68px] px-[19px] py-3 flex items-center gap-[7px]`}>
                <span className="text-[13px] lg:text-[14px] font-medium text-white/50 shrink-0">{label}</span>
                <span className="text-[13px] lg:text-[14px] font-semibold text-[#eee] truncate">{value}</span>
                <button onClick={() => copy(value)} className={`${outlineBtn} ml-auto w-[120px] lg:w-[150px] shrink-0`}>复制并保存</button>
              </article>
            ))}
          </div>
        </WayPanel>

        <WayPanel title="防失联方式" note="建议至少保存 2 种" sub="通过以下方式获取最新地址或接收线路变更提醒。">
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

        <WayPanel title="访问异常排查" note="安全提示" sub="遇到打不开、跳转慢或入口失效时，按顺序完成排查。">
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-3 lg:gap-[27px]">
            {[["01", "打不开页面", "先切换网络，再尝试本页列出的其他地址。"], ["02", "地址跳转异常", "确认地址拼写完整，不要从陌生弹窗或链接进入。"], ["03", "入口失效", "回到本页查看最新可用地址，或从收藏夹重新进入。"]].map(([n, h, p]) => (
              <article key={n} className={`${card} px-[19px] py-[22px]`}>
                <h3 className="flex gap-[9px] text-[18px] lg:text-[1.625rem] font-semibold leading-[27px]"><span className="text-[#ffd21c]">{n}</span><span className="text-[#eee]">{h}</span></h3>
                <p className="mt-[19px] text-[13px] lg:text-[14px] font-medium leading-5 text-[#888]">{p}</p>
              </article>
            ))}
          </div>
        </WayPanel>

        <WayPanel title="设备使用与兼容性建议" sub="建议使用主流浏览器（Chrome、Safari 等）访问，以获得更好的页面兼容性与加载速度；遇到加载缓慢时，可切换线路或刷新页面。">
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            {[["iPhone 用户", "建议使用 Safari 浏览器，并可添加到主屏幕当作 App 使用", "避免在微信 / QQ 等 App 内部打开，以防页面加载失败"],
              ["Android 用户", "推荐使用 Chrome 浏览器，或直接下载 ReelGuo App", "部分手机自带浏览器兼容性较差，可能出现无法播放"],
              ["电脑 / 桌面端", "首选 Chrome / Edge 浏览器，或使用桌面版 App", "若无法加载，请尝试清除浏览器缓存后重试"]].map(([h = "", ok = "", warn = ""]) => (
              <div key={h} className="lg:pl-[19px]">
                <h3 className="text-[16px] lg:text-[1.375rem] font-semibold text-[#eee]">{h}</h3>
                <p className="mt-6 text-[13px] lg:text-[1.0625rem] leading-[1.6] text-[#888]">{ok.split(/(Safari|Chrome \/ Edge|Chrome)/).map((x, i) => (i % 2 ? <span key={i} className="text-[#2ecc71]">{x}</span> : x))}</p>
                <p className="mt-4 text-[13px] lg:text-[1.0625rem] leading-[1.6] text-[#f5a623]">{warn}</p>
              </div>
            ))}
          </div>
        </WayPanel>

        <section className="rounded-[6px] bg-white/[.04] border border-white/10 p-4 lg:px-[13px] lg:py-6 text-[12px] leading-[1.8] text-white/85">
          <h2 className="text-[14px] lg:text-[1.0625rem] font-semibold text-white">ReelGuo 官方入口</h2>
          <p className="mt-4">ReelGuo 官方网站为 <span className="text-[#ffd21c]">{s?.permanent_domain ? bare(s.permanent_domain) : window.location.host}</span>，可在此查看已上线作品，或核对官方域名与账号。</p>
          <h3 className="mt-5 text-[13px] font-semibold text-white">关于 ReelGuo</h3>
          <p className="mt-3">ReelGuo 提供 AI 短剧、原创短剧与 AI 漫剧在线观看，每日更新，支持手机与电脑在线观看。仅限年满 18 周岁的用户访问。</p>
          <h3 className="mt-5 text-[13px] font-semibold text-white">支持平台</h3>
          <p className="mt-3">网页版：手机 / 电脑浏览器直接观看；App：可在 <Link to={href.download()} className="text-[#ffd21c]">下载页</Link> 获取桌面版与安卓版。</p>
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
      <Breadcrumb items={[{ label: "首页", to: "/" }, { label: "搜索" }]} />
      <form role="search" className="relative mt-4 max-w-[640px]" onSubmit={(e) => { e.preventDefault(); submit(value); }}>
        <input value={value} onChange={(e) => setValue(e.target.value)} maxLength={50} autoFocus={!q}
          placeholder="搜索剧名、作者或题材" aria-label="搜索"
          className="w-full h-12 pl-5 pr-28 rounded-full bg-[#161616] border border-white/[.08] focus:border-primary text-[15px] text-on placeholder:text-dim outline-none" />
        <button type="submit" className="absolute right-1.5 top-1.5 h-9 px-5 rounded-full bg-[#ffd321] text-[#14130e] text-[14px] font-semibold flex items-center gap-1.5">
          <SearchIcon size={15} />搜索
        </button>
      </form>
      {q ? (
        <>
          <PageTitle sub={search.isPending ? "搜索中…" : `共 ${total} 条结果`}>
            搜索 “<span className="text-primary">{q}</span>”
          </PageTitle>
          <div className="mt-7">
            {search.isPending ? <PosterSkeleton /> : items.length === 0
              ? <EmptyState title="没有找到相关内容" sub="换个关键词试试，比如「都市」「逆袭」" />
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
