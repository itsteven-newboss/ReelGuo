import { Img } from "@reelguo/ui";
import { Megaphone, Trophy } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useRanks, useSeriesList, useSite, useSuggest, useTopics, type TopicCard, type VideoCard } from "../api";
import { href, Link, navigate } from "../router";
import { useMedia } from "./chrome";
import { Container, fmtPlays, PosterGrid, PosterSkeleton, progressLabel, SectionTitle } from "./kit";

/* ---------------------------------------------------------------------------------------------
 * Hero, as on the reference: the cover full width under the header, its bottom edge cut by a wide ellipse with a
 * thin light line; text at the left of the content column; six thumbnails centred on the curve. Phones: a short
 * banner with the text at the bottom and dots on the right.
 * ------------------------------------------------------------------------------------------- */

const CURVE = { borderBottomLeftRadius: "50% 2.45vw", borderBottomRightRadius: "50% 2.45vw" } as const;

function Hero({ slides }: { slides: VideoCard[] }) {
  const wide = useMedia("(min-width: 1024px)");
  const site = useSite();
  const hot = useRanks("hot");
  const [index, setIndex] = useState(0);
  const paused = useRef(false);
  const count = slides.length;

  useEffect(() => {
    if (count < 2) return undefined;
    const t = window.setInterval(() => { if (!paused.current && document.visibilityState === "visible") setIndex((i) => (i + 1) % count); }, 5000);
    return () => window.clearInterval(t);
  }, [count]);

  if (!count) return <div className={wide ? "h-[37.8vw] shimmer" : "h-[40vw] shimmer"} style={wide ? CURVE : undefined} />;
  const cur = slides[Math.min(index, count - 1)]!;
  const rank = (hot.data?.items ?? []).findIndex((r) => r.video_id === cur.id) + 1;
  const tagline = site.data?.tagline ?? "AI短剧与原创短剧在线观看";

  if (!wide) {
    return (
      <section aria-roledescription="carousel" aria-label="推荐" className="relative h-[40vw] overflow-hidden bg-[#0b0b0d]"
        onTouchStart={() => { paused.current = true; }} onTouchEnd={() => { paused.current = false; }}>
        <Link to={href.video(cur.id)} className="absolute inset-0" aria-label={cur.title}>
          {slides.map((s, i) => (
            <div key={s.id} className={`absolute inset-0 transition-opacity duration-500 ${i === index ? "opacity-100" : "opacity-0"}`}><Img src={s.cover} /></div>
          ))}
          <div className="absolute inset-0 bg-black/10" style={{ backgroundImage: "linear-gradient(rgba(0,0,0,0) 50%, rgba(0,0,0,.7) 100%)" }} />
        </Link>
        <div className="absolute left-[13px] right-[60px] bottom-[14px] pointer-events-none">
          <p className="text-[12px] font-medium leading-[17px] text-[#ffd21c] truncate">ReelGuo · {tagline}</p>
          <div className="mt-1 text-[24px] font-semibold leading-[34px] text-white truncate">{cur.title}</div>
          <p className="mt-1.5 text-[12px] font-medium leading-[17px] text-white flex gap-[9px]">
            {cur.score > 0 && <span><span>{cur.score.toFixed(1)}</span>分</span>}
            <span><span>{fmtPlays(cur.hot)}</span>次播放</span>
          </p>
        </div>
        <div className="absolute right-[13px] bottom-[14px] flex items-center gap-[2px]">
          {slides.map((s, i) => (
            <button key={s.id} onClick={() => setIndex(i)} aria-label={`第 ${i + 1} 张`}
              className={`h-[5px] rounded-full ${i === index ? "w-[11px] bg-[#ffd21c]" : "w-[5px] bg-white/30"}`} />
          ))}
        </div>
      </section>
    );
  }

  // Six thumbnails around the current one.
  const start = Math.max(0, Math.min(index - 1, count - 6));
  const strip = slides.slice(start, start + 6);
  return (
    <section aria-roledescription="carousel" aria-label="推荐" className="relative h-[37.8vw]"
      onMouseEnter={() => { paused.current = true; }} onMouseLeave={() => { paused.current = false; }}>
      <div className="absolute inset-x-0 top-0 h-[35.2vw] overflow-hidden bg-[#0b0b0d]" style={CURVE}>
        <Link to={href.video(cur.id)} className="absolute inset-0" aria-label={cur.title}>
          {slides.map((s, i) => (
            <div key={s.id} className={`absolute inset-0 transition-opacity duration-700 ${i === index ? "opacity-100" : "opacity-0"}`}><Img src={s.cover} /></div>
          ))}
        </Link>
        <div className="absolute inset-0 pointer-events-none"
          style={{ backgroundImage: "linear-gradient(90deg, rgba(0,0,0,.6), rgba(0,0,0,0) 60%), linear-gradient(0deg, #0b0b0d 0%, rgba(0,0,0,0) 30%)" }} />
        {/* The thin light line along the curved bottom edge. */}
        <div className="absolute inset-0 pointer-events-none border-b-[1.5px] border-[rgba(255,226,170,.55)]"
          style={{ ...CURVE, maskImage: "linear-gradient(90deg, rgba(0,0,0,.35), #000 30%, #000 70%, rgba(0,0,0,.35))" }} />
        <div className="absolute inset-x-0 bottom-[23%] pointer-events-none">
          <div className="rg-wrap">
            <p className="text-[1.125rem] leading-[1.52] text-[#ffd321]">ReelGuo · {tagline}</p>
            <div className="mt-[3px] max-w-[60%] text-[3.0625rem] font-semibold leading-[1.4] text-white truncate">{cur.title}</div>
            <p className="mt-[2px] max-w-[25rem] text-[1.0625rem] leading-[1.48] text-white/80 truncate">{cur.description || "暂无简介"}</p>
            <p className="mt-[14px] flex items-center text-[1.1875rem] leading-[1.5]">
              {rank > 0 && <span className="flex items-center gap-1 text-[#ffd321]"><Trophy size={15} />热播总榜No. {rank}</span>}
              {rank > 0 && cur.score > 0 && <span className="mx-[9px] h-[14px] w-px bg-white/40" />}
              {cur.score > 0 && <span className="text-white">{cur.score.toFixed(1)}分</span>}
              <span className="mx-[9px] h-[14px] w-px bg-white/40" />
              <span className="text-[#ededee]">{fmtPlays(cur.hot)}次播放</span>
            </p>
            <div className="mt-[13px] flex gap-2">
              {[...cur.tags.slice(0, 2), progressLabel(cur)].map((t) => (
                <span key={t} className="h-[2rem] px-[6px] rounded-[4.5px] bg-white/20 text-[0.875rem] leading-[1.45] text-[#ededee] flex items-center">{t}</span>
              ))}
            </div>
          </div>
        </div>
      </div>
      <div className="absolute bottom-0 left-1/2 -translate-x-1/2 flex gap-[.73vw]">
        {strip.map((s) => {
          const i = slides.indexOf(s);
          return (
            <button key={s.id} onClick={() => setIndex(i)} aria-label={s.title} aria-current={i === index ? "true" : undefined}
              className={`relative w-[7.13vw] aspect-[3/4] rounded-[.63vw] overflow-hidden border min-[1680px]:border-2 bg-[#171616] transition-colors ${i === index ? "border-[#ffd321]" : "border-transparent hover:border-[#ffd321]/40"}`}>
              <Img src={s.cover} />
            </button>
          );
        })}
      </div>
    </section>
  );
}

/* ---------------------------------------------------------------------------------------------
 * 公告 bar and 热门标签.
 * ------------------------------------------------------------------------------------------- */

function AnnouncementBar() {
  const site = useSite();
  const notices = site.data?.announcements.length
    ? site.data.announcements
    : [{ text: "欢迎来到 ReelGuo：请收藏「回家的路」，地址变更时也能找到我们", url: href.wayHome() }];
  const line = (
    <span className="flex items-center shrink-0">
      {notices.map((n, i) => (
        <span key={i} className="flex items-center">
          <span className="mx-[10px] text-white/60">•</span>
          <span>{n.text}</span>
          {n.url && (n.url.startsWith("/")
            ? <Link to={n.url} className="ml-1 text-[#ffd321] hover:underline">查看详情</Link>
            : <a href={n.url} target="_blank" rel="noreferrer" className="ml-1 text-[#ffd321] hover:underline">查看详情</a>)}
        </span>
      ))}
    </span>
  );
  return (
    <div className="mt-[10px] lg:mt-[30px] h-[37px] lg:h-9 rounded-[8px] lg:rounded-[4.5px] flex items-center overflow-hidden" role="region" aria-label="公告">
      <span className="h-full w-[63px] lg:w-[67px] pl-[11px] gap-[5px] flex items-center bg-[#242321] rounded-l-[8px] lg:rounded-l-[4.5px] text-[12px] font-medium lg:font-normal text-white shrink-0">
        <Megaphone size={12} />公告
      </span>
      <div className="flex-1 overflow-hidden">
        <div className="rg-marquee flex w-max text-[12px] text-white lg:text-[#bbb] whitespace-nowrap">{line}{line}</div>
      </div>
    </div>
  );
}

function HotTags() {
  const suggest = useSuggest(0);
  const tags = suggest.data?.hot_tags ?? [];
  if (!tags.length) return null;
  return (
    <section className="pt-[23px] lg:pt-[21px]">
      <h2 className="text-[16px] lg:text-[1.625rem] font-semibold leading-[22px] lg:leading-8 text-white">热门标签</h2>
      <div className="mt-[7px] lg:mt-4 flex gap-[5px] lg:gap-[7.5px] overflow-x-auto no-bar lg:flex-wrap lg:overflow-visible">
        {tags.map((t) => (
          <Link key={t} to={href.search(t)}
            className="h-8 lg:h-[29px] px-5 lg:px-[13.5px] rounded-[30px] lg:rounded-[18px] border border-white/10 lg:border-white/[.043] text-[14px] lg:text-[12.25px] font-medium lg:font-normal text-white/80 lg:text-[#bbb] hover:text-white flex items-center shrink-0 bg-[linear-gradient(270deg,rgba(255,255,255,.1),rgba(255,255,255,.05))] lg:bg-none lg:bg-white/5">
            {t}
          </Link>
        ))}
      </div>
    </section>
  );
}

/* ---------------------------------------------------------------------------------------------
 * 专题 tile and the 官方入口 block.
 * ------------------------------------------------------------------------------------------- */

export function TopicTile({ t }: { t: TopicCard }) {
  return (
    <Link to={href.topic(t.id)} className="group relative block rounded-[12px] overflow-hidden bg-[#171616]" style={{ aspectRatio: "16 / 9" }}>
      <div className="absolute inset-0 transition-transform duration-300 group-hover:scale-[1.04]"><Img src={t.cover} /></div>
      <div className="absolute inset-0 bg-gradient-to-t from-black/85 via-black/30 to-transparent" />
      <div className="absolute left-3 lg:left-[11px] right-3 lg:right-4 bottom-[10px]">
        <div className="text-[11px] lg:text-[12px] lg:font-medium leading-4 text-white">{t.count}部剧集</div>
        <h3 className="mt-1 lg:mt-[5px] text-[14px] lg:text-[1.125rem] font-semibold leading-5 lg:leading-[22px] text-white truncate">{t.title}</h3>
        {t.description && <p className="mt-1 lg:mt-[3px] text-[11px] lg:text-[0.875rem] leading-[17px] lg:leading-[18px] text-white/60 clamp-2 lg:clamp-1">{t.description}</p>}
      </div>
    </Link>
  );
}

function EntryBlock() {
  const site = useSite();
  const s = site.data;
  const host = s?.permanent_domain?.replace(/^https?:\/\//, "") || window.location.host;
  return (
    <section className="mt-10 lg:mt-[31px] border-t border-white/10 pt-6 lg:pt-[31px] pb-6 lg:pb-[31px]">
      <h2 className="text-[16px] lg:text-[1.25rem] font-semibold leading-[25px] text-[#e6e6e6]">ReelGuo 官方入口</h2>
      <div className="mt-[11px] space-y-[10px] text-[13px] lg:text-[14px] font-medium leading-[18px] text-[#999]">
        <p>ReelGuo 官方网站为 <strong className="font-semibold text-[#ffd321]">{host}</strong>。地址如有变更，请到「回家的路」查看最新入口。</p>
        {(s?.telegram || s?.email) && (
          <p>
            官方渠道：
            {s.telegram && <a href={s.telegram} target="_blank" rel="noreferrer" className="text-[#ffd321]">Telegram 频道</a>}
            {s.telegram && s.email && " · "}
            {s.email && <>客服邮箱 <span className="text-[#ffd321]">{s.email}</span></>}
          </p>
        )}
        <p>
          <Link to={href.wayHome()} className="text-[#ffd321]">回家的路</Link>{" · "}
          <Link to={href.download()} className="text-[#ffd321]">下载 App</Link>
        </p>
      </div>
    </section>
  );
}

/* ---------------------------------------------------------------------------------------------
 * The page.
 * ------------------------------------------------------------------------------------------- */

function CardSection({ title, more, items, pending, count }: { title: string; more: { label: string; to: string }; items: VideoCard[]; pending: boolean; count: number }) {
  if (!pending && items.length === 0) return null;
  return (
    <section className="pt-[29px]">
      <SectionTitle title={title} more={more} />
      {pending ? <PosterSkeleton count={count} /> : <PosterGrid items={items.slice(0, count)} />}
    </section>
  );
}

export function HomePage() {
  const picks = useSeriesList("picks", 14);
  const updated = useSeriesList("updated", 14);
  const hot = useSeriesList("hot", 14);
  const topics = useTopics(3);
  const wide = useMedia("(min-width: 1024px)");
  const slides = (picks.data?.items ?? []).slice(0, 12);

  return (
    <>
      <Hero slides={slides} />
      <Container>
        <AnnouncementBar />
        <HotTags />
        <CardSection title="精选推荐" more={{ label: "更多推荐", to: href.ranks("recommend") }}
          items={picks.data?.items ?? []} pending={picks.isPending} count={wide ? 7 : 6} />
        <CardSection title="最近上新" more={{ label: "全部上新", to: href.channel("all") }}
          items={updated.data?.items ?? []} pending={updated.isPending} count={14} />
        <CardSection title="热播短剧" more={{ label: "更多热播", to: href.ranks("hot") }}
          items={hot.data?.items ?? []} pending={hot.isPending} count={14} />
        {(topics.data?.items.length ?? 0) > 0 && (
          <section className="pt-[29px]">
            <SectionTitle title="专题推荐" more={{ label: "查看全部", to: href.topics() }} />
            <div className="grid grid-cols-1 lg:grid-cols-3 gap-3 lg:gap-[18px]">
              {topics.data!.items.slice(0, 3).map((t) => <TopicTile key={t.id} t={t} />)}
            </div>
          </section>
        )}
        <EntryBlock />
      </Container>
    </>
  );
}

/** Opens a series from anywhere (e.g. "立即播放"). */
export const openSeries = (id: number) => navigate(href.video(id));
