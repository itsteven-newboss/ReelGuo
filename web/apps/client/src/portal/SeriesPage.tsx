import { ApiError, unwrap } from "@reelguo/api-client";
import { Img, Spinner } from "@reelguo/ui";
import { useQueryClient } from "@tanstack/react-query";
import { Heart, Lock, Star } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { api, useAppConfig, useChannelList, useMyRating, usePlay, useSeries, useSeriesAccess, useSite, type SeriesDetail, type VideoCard } from "../api";
import { isLocked } from "../billing/money";
import { Paywall } from "../billing/Paywall";
import { Feed } from "../feed/Feed";
import { DesktopControls, ErrorCard, PauseIndicator } from "../feed/overlays";
import { nativeApp } from "../native";
import { playEvents } from "../player/events";
import { fmtCount, fmtTime } from "../player/format";
import { browserFamily, PlaybackSession } from "../player/qoe";
import { VideoSurface, type VideoSurfaceHandle } from "../player/VideoSurface";
import { useWakeLock } from "../player/wakeLock";
import { goBack, href, Link, navigate, useLocation } from "../router";
import { CommentsPanel } from "../social/Comments";
import { useReport } from "../social/Report";
import { errorMessage, toggleFollow, toggleSeriesFav, toggleSeriesLike, useAuth, useCountBump } from "../state/auth";
import { useLibrary } from "../state/library";
import { useUi } from "../state/ui";
import { useMedia } from "./chrome";
import { Breadcrumb, Container, EmptyState, PosterGrid } from "./kit";

const toCard = (d: SeriesDetail): VideoCard => ({
  id: d.id, title: d.title, cover: d.cover, description: d.description, tags: d.tags, score: d.score,
  episode_count: d.episode_count, total_episodes: d.total_episodes, hot: d.counts.hot, like_count: d.counts.like,
  collect_count: d.counts.collect, comment_count: d.counts.comment, is_finished: d.is_finished, is_original: d.is_original,
  author: d.author, published_at: d.published_at, last_episode_at: d.last_episode_at ?? null,
  category_slug: d.category_slug, category_name: d.category_name,
});

export function SeriesPage({ id, ep: pathEp }: { id: number; ep?: number }) {
  const wide = useMedia("(min-width: 1024px)");
  const series = useSeries(id);
  const { query } = useLocation();
  const progress = useLibrary((s) => s.progressOf(id));
  const ep = pathEp || Number(query.get("ep")) || progress?.ep || series.data?.episodes[0]?.ep_num || 1;

  if (series.isError) {
    return <Container><Breadcrumb items={[{ label: "ReelGuo", to: "/" }, { label: "剧集" }]} /><EmptyState title="内容不存在或已下架" sub="可能已被下架，或在你所在的地区不可观看" /></Container>;
  }
  if (!series.data) return <div className="flex justify-center py-24"><Spinner /></div>;

  // Phones: the full-screen vertical player, as on the reference's mobile series page.
  if (!wide) return <MobileSeries series={series.data} ep={ep} startTime={progress?.ep === ep ? progress.t : undefined} />;
  return <DesktopSeries series={series.data} ep={ep} startTime={progress?.ep === ep ? progress.t : undefined} />;
}

function MobileSeries({ series, ep, startTime }: { series: SeriesDetail; ep: number; startTime?: number }) {
  useEffect(() => {
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => { document.body.style.overflow = prev; };
  }, []);
  const card = useMemo(() => toCard(series), [series]);
  return (
    <div className="fixed inset-0 z-50 bg-black" style={{ "--sat": "max(env(safe-area-inset-top), 12px)", "--sab": "env(safe-area-inset-bottom, 0px)" } as React.CSSProperties}>
      <Feed items={[card]} startEp={ep} startTime={startTime} title={series.title} mode="context" active wide={false} bottomInset={0} onBack={() => goBack()} />
    </div>
  );
}

/* ---------------------------------------------------------------------------------------------
 * Desktop: 16:9 player with the episode panel beside it, then details, ratings, recommendations, comments.
 * ------------------------------------------------------------------------------------------- */

function DesktopSeries({ series, ep, startTime }: { series: SeriesDetail; ep: number; startTime?: number }) {
  const monetization = useAppConfig().data?.monetization ?? false;
  const signedIn = useAuth((s) => s.status === "signed_in");
  const access = useSeriesAccess(series.id, monetization && signedIn);
  const [theater, setTheater] = useState(false);
  const [chosen, setChosen] = useState<string | null>(null);
  const episodes = series.episodes;
  const index = episodes.findIndex((e) => e.ep_num === ep);
  const prev = index > 0 ? episodes[index - 1]!.ep_num : undefined;
  const next = index >= 0 && index < episodes.length - 1 ? episodes[index + 1]!.ep_num : undefined;
  const current = episodes[index];
  const accessSet = signedIn && access.data ? { vip: access.data.vip_active, unlocked: new Set(access.data.unlocked_episodes) } : null;
  const lockedEp = (e: SeriesDetail["episodes"][number]) => monetization && isLocked(e, accessSet);

  const pick = useCallback((n: number) => {
    setChosen(`${series.id}:${n}`);
    navigate(href.video(series.id, n), { replace: true, keepScroll: true });
  }, [series.id]);

  return (
    <Container>
      <Breadcrumb items={[{ label: "首页", to: "/" }, { label: series.category_name, to: href.channel(series.category_slug) }, { label: series.title }]} />
      <div className={`mt-[13px] grid gap-[15px] ${theater ? "" : "grid-cols-[minmax(0,1fr)_327px]"}`}>
        <div className="min-w-0">
          <div className="h-10 px-[15px] rounded-[7.5px] bg-[#262423] border border-white/10 flex items-center gap-[5px] text-white truncate">
            <h1 className="text-[1.125rem] font-semibold leading-[22px] truncate">{series.title}</h1>
            <span className="text-[14px] shrink-0">第{ep}集</span>
            {current?.title && current.title !== `第${ep}集` && <span className="text-[13px] text-white/50 truncate">{current.title}</span>}
          </div>
          <SeriesPlayer series={series} ep={ep} startTime={startTime} intent={chosen === `${series.id}:${ep}`} next={next} onEp={pick}
            theater={theater} onTheater={() => setTheater((t) => !t)} />
          <ActionRow series={series} prev={prev} next={next} onEp={pick} />
        </div>
        <SidePanel series={series} ep={ep} onPick={pick} locked={lockedEp} fill={!theater} />
      </div>
      <InfoCard series={series} ep={ep} />
      <section className="mt-[12px] rounded-[9px] bg-[#161616] border border-white/10 px-6 py-[13px]">
        <h2 className="text-[1.125rem] font-semibold leading-[22px] text-white">剧情简介</h2>
        <p className="mt-[9px] text-[14px] leading-[21px] text-[#a29fa6] whitespace-pre-wrap">{series.description || "暂无简介"}</p>
      </section>
      <SeriesTabs series={series} />
    </Container>
  );
}

function SeriesPlayer({ series, ep, startTime, intent, next, onEp, theater, onTheater }: {
  series: SeriesDetail; ep: number; startTime?: number; intent: boolean; next?: number; onEp: (n: number) => void; theater: boolean; onTheater: () => void;
}) {
  const qc = useQueryClient();
  const play = usePlay(series.id, ep);
  const locked = play.error instanceof ApiError && play.error.code === "episode_locked";
  const video = useRef<VideoSurfaceHandle>(null);
  const root = useRef<HTMLDivElement>(null);
  const { muted, setMuted, volume, setVolume, toast } = useUi();
  const recordProgress = useLibrary((s) => s.recordProgress);
  const [time, setTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [buffered, setBuffered] = useState(0);
  const [paused, setPaused] = useState(false);
  const [waiting, setWaiting] = useState(true);
  const [error, setError] = useState("");
  const [rate, setRate] = useState(1);
  const [idle, setIdle] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [startAt, setStartAt] = useState<number>(startTime && startTime > 3 ? startTime : 0);
  const lastSave = useRef(0);
  const advancing = useRef(false);
  const info = play.data;

  useWakeLock(!!info && !paused && !waiting);

  // Reset per episode.
  useEffect(() => {
    setTime(0); setBuffered(0); setError(""); setWaiting(true); setPaused(false);
    lastSave.current = 0; advancing.current = false;
  }, [ep]);
  useEffect(() => { setDuration(info?.duration ?? 0); }, [info?.duration]);

  // Playback quality, one session per episode.
  const qoe = useRef<PlaybackSession | null>(null);
  useEffect(() => {
    const session = new PlaybackSession(series.id, ep, "desktop", browserFamily(navigator.userAgent), performance.now());
    qoe.current = session;
    const finish = () => {
      if (qoe.current !== session) return;
      qoe.current = null;
      const sample = session.finish(performance.now());
      if (sample) playEvents().sample(sample);
    };
    const onHide = () => { if (document.visibilityState === "hidden") { finish(); void playEvents().flush(true); } };
    document.addEventListener("visibilitychange", onHide);
    return () => { document.removeEventListener("visibilitychange", onHide); finish(); };
  }, [series.id, ep]);

  // Where the viewer was, when they leave the episode.
  const last = useRef<{ t: number; dur: number } | null>(null);
  useEffect(() => { if (time > 0) last.current = { t: time, dur: duration }; }, [time, duration]);
  useEffect(() => () => {
    if (last.current && last.current.t >= 1) recordProgress({ id: series.id, ep, t: last.current.t, dur: last.current.dur });
    last.current = null;
  }, [series.id, ep, recordProgress]);

  const onTime = (t: number) => {
    setTime(t);
    qoe.current?.time(t);
    if (duration > 0 && t >= duration * 0.9) playEvents().track({ type: "complete", series_id: series.id, ep });
    if (t - lastSave.current >= 5 || t < lastSave.current) {
      lastSave.current = t;
      recordProgress({ id: series.id, ep, t, dur: duration });
    }
    if (duration > 0 && t >= duration - 0.35 && next && !advancing.current) {
      advancing.current = true;
      setStartAt(0);
      onEp(next);
    }
  };
  const seekTo = (t: number) => { qoe.current?.seeked(); video.current?.seek(t); };

  // Idle hide of the controls, keyboard shortcuts.
  const idleTimer = useRef<number | undefined>(undefined);
  const wake = () => { setIdle(false); window.clearTimeout(idleTimer.current); idleTimer.current = window.setTimeout(() => setIdle(true), 2800); };
  useEffect(() => () => window.clearTimeout(idleTimer.current), []);
  useEffect(() => {
    const onChange = () => setFullscreen(document.fullscreenElement === root.current);
    document.addEventListener("fullscreenchange", onChange);
    return () => document.removeEventListener("fullscreenchange", onChange);
  }, []);
  const toggleFullscreen = () => {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
    else void root.current?.requestFullscreen().catch(() => toast("无法进入全屏"));
  };
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA")) return;
      const handled = (() => {
        switch (e.key) {
          case " ": video.current?.toggle(); return true;
          case "ArrowLeft": seekTo(Math.max(0, time - 5)); return true;
          case "ArrowRight": seekTo(time + 5); return true;
          case "f": case "F": toggleFullscreen(); return true;
          case "m": case "M": setMuted(!muted); return true;
          case "t": case "T": onTheater(); return true;
          default: return false;
        }
      })();
      if (handled) { e.preventDefault(); wake(); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  return (
    <div ref={root} onMouseMove={wake}
      className={`player-desktop relative mt-[9px] w-full bg-black rounded-[9px] overflow-hidden ${idle ? "is-idle" : ""}`} style={{ aspectRatio: fullscreen ? undefined : "16 / 9" }}>
      {info ? (
        <>
          <div className="absolute inset-0" onClick={() => video.current?.toggle()}>
            <VideoSurface ref={video} src={info.video_url} poster={info.poster ?? series.cover} active muted={muted} volume={volume} rate={rate}
              fit="contain" startAt={startAt}
              onTime={onTime} onDuration={(d) => Number.isFinite(d) && setDuration(d)} onBuffered={setBuffered}
              onPlaying={() => { qoe.current?.playing(performance.now()); setPaused(false); setWaiting(false); setError(""); playEvents().track({ type: "start", series_id: series.id, ep }); }}
              onPause={() => setPaused(true)}
              onWaiting={() => { qoe.current?.waiting(performance.now()); setWaiting(true); }}
              onSize={(_, h) => qoe.current?.size(h)}
              onEnded={() => { playEvents().track({ type: "complete", series_id: series.id, ep }); if (next) onEp(next); }}
              onError={(m) => { qoe.current?.fail(m); setError(m); }}
              onExpired={(at) => { setStartAt(at); void qc.invalidateQueries({ queryKey: ["play", series.id, ep] }); }}
              onAutoplayMuted={() => setMuted(true)} />
          </div>
          {paused && !error && <PauseIndicator />}
          {waiting && !paused && !error && <div className="absolute inset-0 z-10 flex items-center justify-center pointer-events-none"><Spinner size={36} /></div>}
          {error && <ErrorCard detail={error} onReload={() => { setError(""); setWaiting(true); video.current?.reload(); }} />}
          <DesktopControls paused={paused} time={time} duration={duration} buffered={buffered} rate={rate} muted={muted} volume={volume}
            theater={theater} fullscreen={fullscreen} nextTitle={next ? "下一集" : "已是最后一集"} nextDisabled={!next}
            onToggle={() => video.current?.toggle()} onSeek={(t) => { setTime(t); seekTo(t); }} onNext={() => next && onEp(next)}
            onRate={setRate} onMute={() => setMuted(!muted)} onVolume={setVolume}
            onPip={() => { const v = video.current?.el(); if (v && document.pictureInPictureEnabled) void v.requestPictureInPicture().catch(() => toast("画中画开启失败")); }}
            onTheater={onTheater} onFullscreen={toggleFullscreen} />
        </>
      ) : (
        <div className="absolute inset-0">
          <Img src={series.cover} eager />
          <div className="absolute inset-0 bg-black/60 backdrop-blur-md" />
          {locked ? <Paywall series={series} ep={ep} intent={intent} wide /> : <div className="absolute inset-0 flex items-center justify-center"><Spinner /></div>}
        </div>
      )}
    </div>
  );
}

/** One joined bar of seven cells: ♥ 3.5k · 收藏 · 分享 · 下载APP · 上一集 · 下一集 · 反馈. */
function ActionRow({ series, prev, next, onEp }: { series: SeriesDetail; prev?: number; next?: number; onEp: (n: number) => void }) {
  const { isFav, isLiked } = useLibrary();
  const bump = useCountBump();
  const toast = useUi((s) => s.toast);
  const openReport = useReport((s) => s.open);
  const liked = isLiked(series.id);
  const faved = isFav(series.id);
  const btn = "h-11 bg-[#262423] hover:bg-[#302d2b] text-[14px] font-medium leading-[18px] text-white flex items-center justify-center gap-[6px] disabled:text-white/40 disabled:hover:bg-[#262423]";
  return (
    <div className="mt-[9px] grid grid-cols-7 gap-px rounded-[9px] bg-[#1d1c1b] border border-white/10 overflow-hidden">
      <button className={btn} aria-pressed={liked} onClick={() => void toggleSeriesLike(series.id).then((on) => on !== null && toast(on ? "已加入喜欢" : "已取消喜欢", on ? "heart" : "info"))}>
        <Heart size={16} className={liked ? "text-like fill-current" : ""} />{fmtCount(series.counts.like + bump(liked))}
      </button>
      <button className={btn} aria-pressed={faved} onClick={() => void toggleSeriesFav(series.id).then((on) => on !== null && toast(on ? "已收藏" : "已取消收藏", on ? "star" : "info"))}>
        <Star size={16} className={faved ? "text-[#ffd21c] fill-current" : ""} />{faved ? "已收藏" : "收藏"}
      </button>
      <button className={btn} onClick={() => {
        const url = `${window.location.origin}${href.video(series.id)}`;
        navigator.clipboard?.writeText(url).then(() => toast("链接已复制", "ok")).catch(() => toast(url));
      }}>分享</button>
      {nativeApp ? <span className={btn} /> : <Link to={href.download()} className={btn}>下载APP</Link>}
      <button className={btn} disabled={!prev} onClick={() => prev && onEp(prev)}>上一集</button>
      <button className={btn} disabled={!next} onClick={() => next && onEp(next)}>下一集</button>
      <button className={btn} onClick={() => openReport({ type: "series", id: series.id, label: `剧集：${series.title}` })}>反馈</button>
    </div>
  );
}

/** Beside the player: 选集 / 评论 tabs; 52 × 36 episode buttons, five per row; comments scroll inside. */
function SidePanel({ series, ep, onPick, locked, fill }: {
  series: SeriesDetail; ep: number; onPick: (n: number) => void; locked: (e: SeriesDetail["episodes"][number]) => boolean;
  /** Take exactly the player column's height. */
  fill: boolean;
}) {
  const [tab, setTab] = useState<"eps" | "comments">("eps");
  const tabBtn = (k: "eps" | "comments", label: ReactNode) => (
    <button onClick={() => setTab(k)} className={`h-9 -mb-px border-b-2 text-[14px] leading-[19px] ${tab === k ? "border-[#ffd900] text-[#ffd900] font-semibold" : "border-transparent text-white/60 font-medium hover:text-white"}`}>{label}</button>
  );
  const panel = (
    <aside className={`rounded-[9px] bg-[#161616] border border-white/10 px-[18px] pt-[18px] pb-3 flex flex-col ${fill ? "absolute inset-0" : ""}`}>
      <div className="flex gap-[18px] border-b border-white/10 shrink-0">
        {tabBtn("eps", "选集")}
        {tabBtn("comments", <>评论（{fmtCount(series.counts.comment)}）</>)}
      </div>
      {tab === "eps" ? (
        <div className={`mt-[9px] grid grid-cols-5 gap-[7px] content-start overflow-y-auto no-bar ${fill ? "flex-1 min-h-0" : "max-h-[420px]"}`}>
          {series.episodes.map((e) => {
            const on = e.ep_num === ep;
            const lock = locked(e);
            return (
              <button key={e.ep_num} onClick={() => onPick(e.ep_num)} aria-current={on ? "true" : undefined} aria-label={`第 ${e.ep_num} 集${lock ? "（需解锁）" : ""}`}
                className={`relative h-9 rounded-[6px] border text-[14px] leading-5 tabular-nums ${on ? "bg-[#ffd21c] border-[#ffd21c] text-black font-bold" : "bg-[#1d1d1e] border-[#22222a] text-[#8a8a93] font-medium hover:text-white"}`}>
                {String(e.ep_num).padStart(2, "0")}
                {lock && <Lock size={9} className={`absolute top-1 right-1 ${on ? "text-black/70" : "text-white/50"}`} />}
              </button>
            );
          })}
          {series.episodes.length === 0 && <p className="col-span-5 py-8 text-center text-[13px] text-white/40">暂无剧集</p>}
        </div>
      ) : (
        <div className={`-mx-[18px] flex flex-col ${fill ? "flex-1 min-h-0" : "h-[420px]"}`}>
          <CommentsPanel target={{ kind: "series", id: series.id, ep, title: series.title }} total={series.counts.comment} />
        </div>
      )}
    </aside>
  );
  return fill ? <div className="relative min-h-[240px]">{panel}</div> : panel;
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="text-[14px] leading-5 text-[#737373]">{label}</div>
      <div className="mt-[5px] text-[14px] leading-5 text-white truncate">{children}</div>
    </div>
  );
}

/** Cover, facts in three columns, tag chips, and the rating box on the right. */
function InfoCard({ series, ep }: { series: SeriesDetail; ep: number }) {
  const following = useAuth((s) => !!series.author && s.follows.includes(series.author.id));
  const current = series.episodes.find((e) => e.ep_num === ep);
  return (
    <section className="mt-[12px] rounded-[12px] bg-[#161616] border border-white/10 p-[17px] flex gap-5">
      <div className="relative w-[160px] h-[213px] rounded-[12px] overflow-hidden bg-[#111] shrink-0"><Img src={series.cover} /></div>
      <div className="flex-1 min-w-0 pt-[20px] flex flex-col">
        <div className="grid grid-cols-3 gap-x-10 gap-y-[22px]">
          <Fact label="上线日期">{series.published_at ? new Date(series.published_at).toLocaleDateString("sv-SE") : "—"}</Fact>
          <Fact label="更新状态">{series.is_finished ? `全${series.episodes.length}集` : `更新至${series.episodes.length}集`}</Fact>
          <Fact label="单集片长">{current ? fmtTime(current.duration) : "—"}</Fact>
          <Fact label="出品">
            <span className="flex items-center gap-[10px]">
              <span className="truncate font-semibold">{series.author?.name ?? "ReelGuo"}</span>
              {series.author && (
                <button onClick={() => void toggleFollow(series.author!.id, series.author!.name)} aria-pressed={following}
                  className={`h-[26px] px-3 rounded-full text-[12px] font-semibold shrink-0 ${following ? "bg-white/10 text-white/60" : "bg-[#ffd21c] text-black"}`}>
                  {following ? "已关注" : "+关注"}
                </button>
              )}
            </span>
          </Fact>
          <Fact label="播放次数">{fmtCount(series.counts.hot)}</Fact>
          <Fact label="题材类型">{[series.category_name, series.tags[0]].filter(Boolean).join("·")}</Fact>
        </div>
        {series.tags.length > 0 && (
          <div className="mt-auto pb-[17px] flex flex-wrap gap-[6px]">
            {series.tags.map((t) => <Link key={t} to={href.search(t)} className="h-[26px] px-[9px] rounded-[6px] bg-[#1c191e] border border-[#2d2831] text-[12px] font-medium text-[#a29fa6] hover:text-white flex items-center">{t}</Link>)}
          </div>
        )}
      </div>
      <RatingBox series={series} />
    </section>
  );
}

function RatingBox({ series }: { series: SeriesDetail }) {
  const qc = useQueryClient();
  const signedIn = useAuth((s) => s.status === "signed_in");
  const openLogin = useAuth((s) => s.openLogin);
  const toast = useUi((s) => s.toast);
  const mine = useMyRating(series.id, signedIn);
  const [pick, setPick] = useState<number | null>(null);
  const [hover, setHover] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const rating = mine.data?.rating ?? series.rating;
  // Viewers' average once enough people have rated; the editors' score before that.
  const shown = rating.average !== null && rating.average !== undefined && rating.count >= 5 ? rating.average : series.score;
  const value = hover ?? pick ?? mine.data?.mine ?? 0;

  const submit = async () => {
    if (!signedIn) return openLogin();
    if (!pick) return toast("请先点星星选择分数");
    setBusy(true);
    try {
      unwrap(await api.PUT("/api/v1/series/{id}/rating", { params: { path: { id: series.id } }, body: { score: pick } }));
      void qc.invalidateQueries({ queryKey: ["rating", series.id] });
      void qc.invalidateQueries({ queryKey: ["series", series.id] });
      toast(`已评分 ${pick} 分`, "ok");
      setPick(null);
    } catch (e) {
      toast(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="w-[263px] shrink-0 rounded-[15px] border border-white/[.08] px-[19px] pt-[13px] pb-[13px] flex flex-col"
      style={{ background: "linear-gradient(rgb(23,22,17) 0%, rgb(33,30,19) 100%)" }}>
      <div className="flex items-center justify-between">
        <b className="text-[1.25rem] font-normal leading-[19px] text-[#737373]">综合评分</b>
        <span className="text-[#ffd21c] tabular-nums flex items-baseline gap-[5px]">
          <span className="text-[3.75rem] font-semibold leading-10">{shown > 0 ? shown.toFixed(1) : "—"}</span>
          <small className="text-[1.25rem] leading-[19px]">分</small>
        </span>
      </div>
      <div className="mt-[15px] flex items-center justify-between">
        <em className="not-italic text-[1.125rem] leading-9 text-white/40">点我评分</em>
        <span className="flex" onMouseLeave={() => setHover(null)}>
          {[1, 2, 3, 4, 5].map((i) => (
            <button key={i} aria-label={`${i * 2} 分`} onMouseEnter={() => setHover(i * 2)} onClick={() => setPick(i * 2)} className="p-0.5">
              <Star size={18} className={value >= i * 2 ? "text-[#ffd21c] fill-current" : "text-white/20"} />
            </button>
          ))}
        </span>
      </div>
      <div className="text-[11.5px] text-white/35 text-right">
        {mine.data?.mine ? `你的评分 ${mine.data.mine} 分 · ` : ""}{rating.count} 人评分
      </div>
      <button onClick={() => void submit()} disabled={busy}
        className={`mt-auto h-[38px] rounded-[7.5px] text-[1.125rem] font-semibold leading-[22px] ${pick ? "text-black" : "text-black/55"}`}
        style={{ background: pick ? "linear-gradient(90deg,#ffd21c,#f5a623)" : "linear-gradient(90deg,rgba(255,210,28,.4),rgba(245,166,35,.4))" }}>
        {busy ? "提交中…" : "提交评分"}
      </button>
    </div>
  );
}

/** 猜你喜欢 / 最新地址 tabs, the recommendation grid, then the address cards and lines. */
function SeriesTabs({ series }: { series: SeriesDetail }) {
  const [tab, setTab] = useState<"like" | "addresses">("like");
  const related = useChannelList(series.category_slug, "hot", 15);
  const site = useSite();
  const toast = useUi((s) => s.toast);
  const items = (related.data?.pages[0]?.items ?? []).filter((v) => v.id !== series.id).slice(0, 14);
  const go = (id: "like" | "addresses") => {
    setTab(id);
    document.getElementById(`series-${id}`)?.scrollIntoView({ behavior: "smooth", block: "start" });
  };
  const origin = window.location.origin;
  const mirrors = Array.from(new Set([origin, ...(site.data?.mirrors ?? [])]));
  const bare = (u: string) => u.replace(/^https?:\/\//, "").replace(/\/$/, "");
  const cards: { title: string; badge: string; value: string; hint: string }[] = [
    { title: "当前地址", badge: "可用", value: bare(origin), hint: "建议优先使用此地址访问" },
  ];
  if (site.data?.permanent_domain) cards.push({ title: "永久地址", badge: "长期保存", value: bare(site.data.permanent_domain), hint: "建议收藏，长期可用" });
  cards.push({ title: "地址发布页", badge: "备用访问", value: bare(`${origin}${href.wayHome()}`), hint: "当前无法访问时使用此地址" });
  const copy = (v: string) => navigator.clipboard?.writeText(v).then(() => toast("已复制", "ok")).catch(() => toast(v));
  return (
    <>
      <nav className="mt-[22px] flex gap-5 border-b border-white/[.08]">
        {([["like", "猜你喜欢"], ["addresses", "最新地址"]] as const).map(([k, label]) => (
          <button key={k} onClick={() => go(k)}
            className={`h-[37px] -mb-px border-b text-[14px] leading-[17px] ${tab === k ? "border-[#ffd21c] text-[#ffd21c] font-semibold" : "border-transparent text-[#7a7a7a] hover:text-white"}`}>{label}</button>
        ))}
      </nav>
      <section id="series-like" className="pt-[2px] scroll-mt-4">
        <h2 className="mt-[13px] mb-[15px] text-[1.625rem] font-semibold leading-[30px] text-white">猜你喜欢</h2>
        {items.length ? <PosterGrid items={items} /> : <EmptyState title="暂无推荐" />}
      </section>
      <section id="series-addresses" className="mt-[22px] scroll-mt-4">
        <h2 className="text-[1.625rem] font-semibold leading-[34px] text-white">最新地址</h2>
        <div className="mt-[15px] grid grid-cols-3 gap-[15px]">
          {cards.map((c) => (
            <div key={c.title} className="rounded-[9px] bg-[#161616] border border-white/10 px-[15px] py-[15px]">
              <div className="flex items-center gap-2">
                <span className={`text-[1.125rem] font-semibold ${c.title === "当前地址" ? "text-[#ffd21c]" : "text-white"}`}>{c.title}</span>
                <span className="h-5 px-2 rounded-full bg-[#ffd21c]/10 text-[#ffd21c] text-[11px] flex items-center">• {c.badge}</span>
              </div>
              <div className="mt-[10px] text-[14px] font-semibold text-white truncate">{c.value}</div>
              <div className="mt-[14px] flex items-center gap-2">
                <span className="flex-1 min-w-0 text-[13px] text-white/40 truncate">{c.hint}</span>
                <button onClick={() => copy(c.value)} className="h-8 px-5 rounded-full bg-white/10 hover:bg-white/15 text-[13px] text-white">复制</button>
              </div>
            </div>
          ))}
        </div>
        <ul className="mt-6 space-y-[7px] text-[14px] leading-5 text-white">
          {mirrors.map((a, i) => <li key={a}>{i === 0 ? "最新地址" : "备用地址"}：<a href={a} className="hover:text-[#ffd21c]">{a}</a></li>)}
          {site.data?.permanent_domain && <li>永久发布页：{site.data.permanent_domain}</li>}
          {site.data?.telegram && <li>官方交流群：<a href={site.data.telegram} target="_blank" rel="noreferrer" className="hover:text-[#ffd21c]">{site.data.telegram}</a></li>}
          {site.data?.email && <li>发送任意邮件到 {site.data.email} 获取最新地址</li>}
        </ul>
      </section>
    </>
  );
}
