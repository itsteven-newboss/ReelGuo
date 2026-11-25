import { Img } from "@reelguo/ui";
import { Bell, Trophy } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { Trans } from "react-i18next";
import { useRanks, useSeriesList, useSite, useSuggest, useTopics, type TopicCard, type VideoCard } from "../api";
import { href, Link, navigate } from "../router";
import { useMedia } from "./chrome";
import { Container, fmtPlays, PosterGrid, PosterSkeleton, progressLabel, SectionTitle } from "./kit";
import { BRAND } from "../brand";
import { t } from "../i18n";

/* ---------------------------------------------------------------------------------------------
 * Hero, as on the reference: the cover full width under the header, its bottom edge cut by a wide ellipse with a
 * thin light line; text at the left of the content column; six thumbnails centred on the curve. Phones: a swipeable
 * strip of rounded 16:9 slides with the neighbours peeking in at the edges.
 * ------------------------------------------------------------------------------------------- */

const CURVE = { borderBottomLeftRadius: "50% 2.45vw", borderBottomRightRadius: "50% 2.45vw" } as const;

/** Phone slides: 16:9, 20 px in from each side (so 12 px of each neighbour shows), 8 px apart. */
const PHONE_SLIDE = "h-[calc((100vw-40px)*0.5625)]";
const PHONE_GAP = 8;
/** How far a swipe must travel (px) to change slide; shorter ones spring back. */
const SWIPE_MIN = 50;

/**
 * Phone hero, measured on the reference (390 px wide): a 350 x 197 slide with radius 8, the previous and next slides
 * 8 px either side; a bottom fade with "brand·tagline" (11/500 accent), the title (19/700) and "tags · progress"
 * (13 px, white 80 %); white dots at the bottom right. Slides loop. Swiping drags the strip with the finger and
 * snaps to the next or previous slide on release; vertical scrolling is left to the page.
 */
function PhoneHero({ slides, index, go, paused, tagline }: {
  slides: VideoCard[]; index: number; go: (i: number) => void; paused: { current: boolean }; tagline: string;
}) {
  const count = slides.length;
  const [drag, setDrag] = useState(0);
  const [dragging, setDragging] = useState(false);
  const touch = useRef<{ x: number; y: number; id: number; axis: "x" | "y" | null } | null>(null);
  const swiped = useRef(false);
  // Each slide's position in the previous render: a slide that wraps round (e.g. from +2 to -1) moves without
  // animating, so it never sweeps across the visible one.
  const last = useRef(new Map<number, number>());

  // Position relative to the current slide: 0 current, -1 previous, +1 next, further ones off screen.
  const offsetOf = (i: number) => {
    const d = (i - index + count) % count;
    if (count === 2 && d === 1 && drag > 0) return -1; // two slides: the other one follows the swipe direction
    return d <= count / 2 ? d : d - count;
  };
  useEffect(() => { last.current = new Map(slides.map((s, i) => [s.id, count === 1 ? 0 : offsetOf(i)])); });

  const release = (commit: boolean, dx: number) => {
    touch.current = null;
    setDragging(false);
    paused.current = false;
    if (commit && dx <= -SWIPE_MIN) go(index + 1);
    else if (commit && dx >= SWIPE_MIN) go(index - 1);
    setDrag(0);
  };

  return (
    <section aria-roledescription="carousel" aria-label={t("home.carousel")} className={`relative mt-[9px] ${PHONE_SLIDE} overflow-hidden select-none`}
      style={{ touchAction: "pan-y pinch-zoom" }}
      onDragStart={(e) => e.preventDefault()}
      onPointerDown={(e) => {
        if (count < 2 || (e.pointerType === "mouse" && e.button !== 0)) return;
        touch.current = { x: e.clientX, y: e.clientY, id: e.pointerId, axis: null };
        swiped.current = false;
      }}
      onPointerMove={(e) => {
        const t = touch.current;
        if (!t || t.id !== e.pointerId) return;
        const dx = e.clientX - t.x, dy = e.clientY - t.y;
        if (!t.axis) {
          if (Math.abs(dx) < 6 && Math.abs(dy) < 6) return;
          t.axis = Math.abs(dx) > Math.abs(dy) ? "x" : "y";
          if (t.axis === "y") { touch.current = null; return; }
          e.currentTarget.setPointerCapture(e.pointerId);
          paused.current = true;
          setDragging(true);
        }
        swiped.current = true;
        setDrag(dx);
      }}
      onPointerUp={(e) => { const t = touch.current; if (t?.axis === "x") release(true, e.clientX - t.x); else touch.current = null; }}
      onPointerCancel={() => { if (touch.current?.axis === "x") release(false, 0); else touch.current = null; }}
      onClickCapture={(e) => { if (swiped.current) { e.preventDefault(); e.stopPropagation(); swiped.current = false; } }}>
      {slides.map((s, i) => {
        const off = count === 1 ? 0 : offsetOf(i);
        const before = last.current.get(s.id);
        const wrapped = before !== undefined && Math.abs(before - off) > 1;
        const meta = [...s.tags.slice(0, 2), progressLabel(s)].join(" · ");
        return (
          <Link key={s.id} to={href.video(s.id)} aria-label={s.title} aria-hidden={off !== 0 || undefined} tabIndex={off === 0 ? 0 : -1}
            className="absolute top-0 left-[20px] w-[calc(100%-40px)] h-full rounded-[8px] overflow-hidden bg-[#171616]"
            style={{
              transform: `translateX(calc(${off} * (100% + ${PHONE_GAP}px) + ${drag}px))`,
              transition: dragging || wrapped ? "none" : "transform 320ms cubic-bezier(.25,.8,.25,1)",
              visibility: Math.abs(off) > 1 && (before === undefined || Math.abs(before) > 1) ? "hidden" : undefined,
            }}>
            <Img src={s.cover} />
            <div className="absolute inset-0 pointer-events-none" style={{ backgroundImage: "linear-gradient(rgba(0,0,0,0) 40%, rgba(0,0,0,.65) 100%)" }} />
            <div className="absolute left-[15px] bottom-[15px] w-[calc(100%-72px)] pointer-events-none">
              <p className="text-[11px] font-medium leading-[16px] text-[#ffd21c] truncate">{BRAND}·{tagline}</p>
              <div className="mt-[4px] text-[19px] font-bold leading-[24.7px] text-white truncate">{s.title}</div>
              <p className="mt-[4px] text-[13px] leading-[18px] text-white/80 truncate">{meta}</p>
            </div>
          </Link>
        );
      })}
      {count > 1 && (
        <div className="absolute right-[32px] bottom-[14.5px] flex items-center gap-[4px]">
          {slides.map((s, i) => (
            <button key={s.id} onClick={() => go(i)} aria-label={t("home.slide", { n: i + 1 })} aria-current={i === index || undefined}
              className={`h-[4px] ${i === index ? "w-[10px] rounded-[2px] bg-white" : "w-[4px] rounded-full bg-white/40"} transition-all`} />
          ))}
        </div>
      )}
    </section>
  );
}

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

  if (!count) return wide ? <div className="h-[37.8vw] shimmer" style={CURVE} /> : <div className={`mx-[20px] mt-[9px] ${PHONE_SLIDE} rounded-[8px] shimmer`} />;
  const cur = slides[Math.min(index, count - 1)]!;
  const rank = (hot.data?.items ?? []).findIndex((r) => r.video_id === cur.id) + 1;
  const tagline = site.data?.tagline ?? t("home.tagline");

  if (!wide) return <PhoneHero slides={slides} index={Math.min(index, count - 1)} go={(i) => setIndex(((i % count) + count) % count)} paused={paused} tagline={tagline} />;

  // Six thumbnails around the current one.
  const start = Math.max(0, Math.min(index - 1, count - 6));
  const strip = slides.slice(start, start + 6);
  return (
    <section aria-roledescription="carousel" aria-label={t("home.carousel")} className="relative h-[37.8vw]"
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
            <p className="text-[1.125rem] leading-[1.52] text-[#ffd321]">{BRAND} · {tagline}</p>
            <div className="mt-[3px] max-w-[60%] text-[3.0625rem] font-semibold leading-[1.4] text-white truncate">{cur.title}</div>
            <p className="mt-[2px] max-w-[25rem] text-[1.0625rem] leading-[1.48] text-white/80 truncate">{cur.description || t("series.noSynopsis")}</p>
            <p className="mt-[14px] flex items-center text-[1.1875rem] leading-[1.5]">
              {rank > 0 && <span className="flex items-center gap-1 text-[#ffd321]"><Trophy size={15} />{t("home.rank", { rank })}</span>}
              {rank > 0 && cur.score > 0 && <span className="mx-[9px] h-[14px] w-px bg-white/40" />}
              {cur.score > 0 && <span className="text-white">{t("common.score", { score: cur.score.toFixed(1) })}</span>}
              <span className="mx-[9px] h-[14px] w-px bg-white/40" />
              <span className="text-[#ededee]">{t("home.plays", { count: fmtPlays(cur.hot) })}</span>
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
    : [{ text: t("home.welcome", { brand: BRAND }), url: href.wayHome() }];
  const line = (
    <span className="flex items-center shrink-0">
      {notices.map((n, i) => (
        <span key={i} className="flex items-center">
          <span className="mx-[10px] text-white/60">•</span>
          <span>{n.text}</span>
          {n.url && (n.url.startsWith("/")
            ? <Link to={n.url} className="ml-1 text-[#ffd21c] lg:text-[#ffd321] hover:underline">{t("home.details")}</Link>
            : <a href={n.url} target="_blank" rel="noreferrer" className="ml-1 text-[#ffd21c] lg:text-[#ffd321] hover:underline">{t("home.details")}</a>)}
        </span>
      ))}
    </span>
  );
  return (
    <div className="mt-[9.5px] lg:mt-[2.0833vw] h-[37px] lg:h-[max(36px,2.0833vw)] rounded-[8px] lg:rounded-[.3125vw] flex overflow-hidden bg-[#19191b] lg:bg-[#131315]"
      role="region" aria-label={t("home.notice")}>
      <span className="min-w-[63px] lg:min-w-[67px] pl-[11px] pr-[10px] lg:px-[.75rem] gap-[5px] lg:gap-[.3125rem] flex items-center bg-[#242321] text-[12px] lg:text-[max(12px,.677vw)] font-medium lg:font-normal text-white shrink-0">
        <Bell size={13} strokeWidth={2.2} className="text-[#ffd321] shrink-0" />{t("home.notice")}
      </span>
      <div className="flex-1 overflow-hidden flex items-center">
        <div className="rg-marquee flex w-max text-[12px] lg:text-[max(12px,.677vw)] text-white lg:text-[#bbb] whitespace-nowrap">{line}{line}</div>
      </div>
    </div>
  );
}

function HotTags() {
  const suggest = useSuggest(0);
  const tags = suggest.data?.hot_tags ?? [];
  if (!tags.length) return null;
  return (
    <section className="pt-[23px] lg:pt-[1.4583vw]">
      <div className="lg:h-[2.6rem] lg:mb-[.78125vw]">
        <h2 className="text-[16px] lg:text-[1.625rem] font-semibold leading-[22.4px] lg:leading-[2.25rem] text-white">{t("home.hotTags")}</h2>
      </div>
      {/* Phones: the row runs to the screen's right edge. */}
      <div className="mt-[7.5px] lg:mt-0 -mr-[13px] pr-[13px] lg:mr-0 lg:pr-0 lg:pb-[.10417vw] flex gap-[5px] lg:gap-[.52083vw] overflow-x-auto no-bar">
        {tags.map((t) => (
          <Link key={t} to={href.search(t)}
            className="h-[32px] lg:h-[2.0625rem] px-[19px] lg:px-[.88542vw] lg:py-[.26042vw] rounded-full lg:rounded-[1.25vw] border border-white/10 lg:border-white/[.043] text-[14px] lg:text-[.875rem] leading-[20px] lg:leading-[1.5] font-medium lg:font-normal text-white/80 lg:text-[#bbb] hover:text-white flex items-center shrink-0 whitespace-nowrap bg-[linear-gradient(270deg,rgba(255,255,255,.1),rgba(255,255,255,.05))] lg:bg-none lg:bg-white/5">
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

export function TopicTile({ t: topic }: { t: TopicCard }) {
  return (
    <Link to={href.topic(topic.id)} className="group relative block rounded-[12px] overflow-hidden bg-[#171616]" style={{ aspectRatio: "16 / 9" }}>
      <div className="absolute inset-0 transition-transform duration-300 group-hover:scale-[1.04]"><Img src={topic.cover} /></div>
      <div className="absolute inset-0 bg-gradient-to-t from-black/85 via-black/30 to-transparent" />
      <div className="absolute left-3 lg:left-[11px] right-3 lg:right-4 bottom-[10px]">
        <div className="text-[11px] lg:text-[12px] lg:font-medium leading-4 text-white">{t("topics.count", { count: topic.count })}</div>
        <h3 className="mt-1 lg:mt-[5px] text-[14px] lg:text-[1.125rem] font-semibold leading-5 lg:leading-[22px] text-white truncate">{topic.title}</h3>
        {topic.description && <p className="mt-1 lg:mt-[3px] text-[11px] lg:text-[0.875rem] leading-[17px] lg:leading-[18px] text-white/60 clamp-2 lg:clamp-1">{topic.description}</p>}
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
      <h2 className="text-[16px] lg:text-[1.25rem] font-semibold leading-[25px] text-[#e6e6e6]">{t("way.entryTitle", { brand: BRAND })}</h2>
      <div className="mt-[11px] space-y-[10px] text-[13px] lg:text-[14px] font-medium leading-[18px] text-[#999]">
        <p><Trans i18nKey="home.entryText" values={{ brand: BRAND, host }} components={{ host: <strong className="font-semibold text-[#ffd321]" /> }} /></p>
        {(s?.telegram || s?.email) && (
          <p>
            {t("home.channels")}
            {s.telegram && <a href={s.telegram} target="_blank" rel="noreferrer" className="text-[#ffd321]">{t("home.telegram")}</a>}
            {s.telegram && s.email && " · "}
            {s.email && <>{t("home.supportEmail")} <span className="text-[#ffd321]">{s.email}</span></>}
          </p>
        )}
        <p>
          <Link to={href.wayHome()} className="text-[#ffd321]">{t("nav.wayHome")}</Link>{" · "}
          <Link to={href.download()} className="text-[#ffd321]">{t("way.appTitle")}</Link>
        </p>
      </div>
    </section>
  );
}

/* ---------------------------------------------------------------------------------------------
 * The page.
 * ------------------------------------------------------------------------------------------- */

const SECTION_GAP = "pt-[23.5px] lg:pt-[1.979vw]";

function CardSection({ title, more, items, pending, count }: { title: string; more: { label: string; to: string }; items: VideoCard[]; pending: boolean; count: number }) {
  if (!pending && items.length === 0) return null;
  return (
    <section className={SECTION_GAP}>
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
        <CardSection title={t("channel.sortPicks")} more={{ label: t("home.morePicks"), to: href.ranks("recommend") }}
          items={picks.data?.items ?? []} pending={picks.isPending} count={wide ? 7 : 6} />
        <CardSection title={t("footer.latest")} more={{ label: t("home.allLatest"), to: href.channel("all") }}
          items={updated.data?.items ?? []} pending={updated.isPending} count={14} />
        <CardSection title={t("home.hotDramas")} more={{ label: t("home.moreHot"), to: href.ranks("hot") }}
          items={hot.data?.items ?? []} pending={hot.isPending} count={14} />
        {(topics.data?.items.length ?? 0) > 0 && (
          <section className={SECTION_GAP}>
            <SectionTitle title={t("home.topicPicks")} more={{ label: t("home.viewAll"), to: href.topics() }} />
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
