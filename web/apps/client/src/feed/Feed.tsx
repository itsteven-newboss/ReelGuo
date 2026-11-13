import { ApiError } from "@reelguo/api-client";
import { Img, Spinner } from "@reelguo/ui";
import { useQueryClient } from "@tanstack/react-query";
import { Expand } from "lucide-react";
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { href } from "../router";
import { useReport } from "../social/Report";
import { playQuery, seriesQuery, useAppConfig, usePlay, useSeries, useSeriesAccess, type VideoCard } from "../api";
import { isLocked } from "../billing/money";
import { Paywall } from "../billing/Paywall";
import {
  LONG_PRESS_MS, SINGLE_TAP_DELAY_MS, initialGesture, longPressElapsed, pointerDown, pointerMove, pointerUp,
  type GestureEffect, type GestureState,
} from "../player/gestures";
import { playEvents } from "../player/events";
import { browserFamily, PlaybackSession } from "../player/qoe";
import { prefetchStart } from "../player/prefetch";
import { useWakeLock } from "../player/wakeLock";
import { VideoSurface, type VideoSurfaceHandle } from "../player/VideoSurface";
import { CommentsPanel } from "../social/Comments";
import { toggleSeriesFav, toggleSeriesLike, useAuth, useCountBump } from "../state/auth";
import { useLibrary } from "../state/library";
import { closeOverlay, openOverlay } from "../state/nav";
import { useUi } from "../state/ui";
import { Sheet } from "../ui/Overlays";
import { DetailPanel, EpisodeGrid, type LockedFn, type Panel } from "./DetailSheet";
import {
  ContextHeader, ControlsLock, DesktopContextHeader, DesktopControls, DesktopMeta, DesktopRail, ErrorCard, FullscreenControls,
  LockHint, MobileInfo, MobileRail, MobileTopChips, PauseIndicator, ScrubOverlay, SeekLine, SpeedChip,
} from "./overlays";

type Props = {
  items: VideoCard[];
  /** home = the Home tab; context = opened from a list, with a back header. */
  mode: "home" | "context";
  title?: string;
  startIndex?: number;
  startEp?: number;
  /** Resume position (seconds) for the start item's start episode, e.g. from watch history. */
  startTime?: number;
  /** Only the visible feed plays. */
  active: boolean;
  wide: boolean;
  /** Space kept free at the bottom on mobile (the tab bar on Home). */
  bottomInset: number;
  onBack?: () => void;
  /** Called when the viewer is 3 items from the end, to load more. */
  onNearEnd?: () => void;
};

const SAVE_EVERY_S = 5;
const NEAR_END_S = 0.35;
const IDLE_MS = 2800;
const RATES = [1, 1.25, 1.5, 2];

/**
 * Full-screen vertical feed (UI-REFERENCE §3-5). Native scroll-snap does the paging; only the
 * active item mounts a video, the others show their cover.
 */
export function Feed({ items, mode, title = "", startIndex = 0, startEp, startTime, active, wide, bottomInset, onBack, onNearEnd }: Props) {
  const qc = useQueryClient();
  const box = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLDivElement>(null);
  const video = useRef<VideoSurfaceHandle>(null);

  const [index, setIndex] = useState(Math.min(startIndex, Math.max(0, items.length - 1)));
  const cur = items[index];
  const curId = cur?.id;

  // Episode chosen per series in this feed (first ready episode by default).
  const [epMap, setEpMap] = useState<Record<number, number>>(() => (startEp && cur ? { [cur.id]: startEp } : {}));
  const series = useSeries(curId);
  const episodes = series.data?.episodes ?? [];
  const curEp = (curId && epMap[curId]) || episodes[0]?.ep_num || 1;
  const play = usePlay(curId, series.data ? curEp : undefined);

  // Paid episodes (Phase 5): the API answers 402 episode_locked until the viewer unlocks it or has VIP.
  const monetization = useAppConfig().data?.monetization ?? false;
  const signedIn = useAuth((s) => s.status === "signed_in");
  const access = useSeriesAccess(curId, monetization && signedIn);
  const playLocked = play.error instanceof ApiError && play.error.code === "episode_locked";
  const lockedFn = useMemo<LockedFn | undefined>(() => {
    if (!monetization) return undefined;
    const a = signedIn && access.data ? { vip: access.data.vip_active, unlocked: new Set(access.data.unlocked_episodes) } : null;
    return (e) => isLocked(e, a);
  }, [monetization, signedIn, access.data]);
  // The episode the viewer chose (picked, or played on into); auto-unlock may spend coins only for that one.
  const [chosen, setChosen] = useState<string | null>(null);

  const { clean, setClean, fullscreen, setFullscreen, muted, setMuted, volume, setVolume, theater, setTheater, toast } = useUi();
  const { isFav, isLiked, recordProgress } = useLibrary();
  const bump = useCountBump();

  // Playback state for the active item.
  const [time, setTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [buffered, setBuffered] = useState(0);
  const [paused, setPaused] = useState(false);
  const [waiting, setWaiting] = useState(true);
  const [error, setError] = useState("");
  const [rate, setRate] = useState(1);
  const [holding, setHolding] = useState(false);
  const [speedLock, setSpeedLock] = useState(false);
  const [lockArmed, setLockArmed] = useState(false);
  const [scrub, setScrub] = useState<{ target: number; fraction: number } | null>(null);
  const [locked, setLocked] = useState(false);
  const [panel, setPanel] = useState<Panel | null>(null);
  const [rotated, setRotated] = useState(false);
  const [landscapeFit, setLandscapeFit] = useState<"contain" | "cover">("contain");
  const [idle, setIdle] = useState(false);
  const [docFullscreen, setDocFullscreen] = useState(false);

  const effectiveRate = holding || speedLock ? 2 : rate;
  // The screen stays on while a video plays (phones would otherwise dim mid-episode).
  useWakeLock(active && !!play.data && !paused && !waiting);
  // Where to start a given series/episode (history resume, or after a token refresh).
  const [resume, setResume] = useState<{ id: number; ep: number; t: number } | null>(() =>
    cur && startEp && startTime ? { id: cur.id, ep: startEp, t: startTime } : null);
  const startAt = resume && resume.id === curId && resume.ep === curEp ? resume.t : 0;
  const lastSave = useRef(0);
  const advancing = useRef(false);
  const holdingRef = useRef(false);

  // ---------- paging ----------

  useLayoutEffect(() => {
    const el = box.current;
    if (el) el.scrollTop = index * el.clientHeight;
    // Only on mount: afterwards the viewer scrolls.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const indexRef = useRef(index);
  indexRef.current = index;
  useEffect(() => {
    const el = box.current;
    if (!el) return undefined;
    // Keep the active item aligned when the page height changes (rotation, window resize). Width-only
    // changes (the desktop dock opening/closing) must not touch scrollTop, or they fight an ongoing scroll.
    let lastHeight = el.clientHeight;
    const ro = new ResizeObserver(() => {
      if (el.clientHeight === lastHeight) return;
      lastHeight = el.clientHeight;
      el.scrollTop = indexRef.current * el.clientHeight;
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const onScroll = () => {
    const el = box.current;
    if (!el || !el.clientHeight) return;
    const next = Math.round(el.scrollTop / el.clientHeight);
    if (next !== index && next >= 0 && next < items.length) setIndex(next);
  };

  const go = useCallback((delta: number) => {
    const el = box.current;
    if (!el) return;
    const target = Math.max(0, Math.min(items.length - 1, index + delta));
    el.scrollTo({ top: target * el.clientHeight, behavior: "smooth" });
  }, [index, items.length]);

  useEffect(() => {
    if (index >= items.length - 3) onNearEnd?.();
  }, [index, items.length, onNearEnd]);

  // Neighbours' details are fetched ahead (UI-REFERENCE §3).
  useEffect(() => {
    for (const i of [index - 1, index + 1]) {
      const item = items[i];
      if (item) void qc.prefetchQuery(seriesQuery(item.id));
    }
  }, [index, items, qc]);

  // Save where the viewer was when they leave an item or episode (not only every 5 s),
  // so short views still appear in watch history.
  const lastPosition = useRef<{ id: number; ep: number; t: number; dur: number } | null>(null);
  useEffect(() => {
    if (curId && time > 0) lastPosition.current = { id: curId, ep: curEp, t: time, dur: duration };
  }, [curId, curEp, time, duration]);
  useEffect(() => () => {
    const last = lastPosition.current;
    if (last && last.t >= 1) recordProgress(last);
    lastPosition.current = null;
  }, [curId, curEp, recordProgress]);

  // Playback quality: one session per item / episode while it is on screen, reported when the viewer leaves it.
  const qoe = useRef<PlaybackSession | null>(null);
  useEffect(() => {
    if (!curId || !active) return undefined;
    const session = new PlaybackSession(curId, curEp, wide ? "desktop" : "mobile", browserFamily(navigator.userAgent), performance.now());
    qoe.current = session;
    const finish = () => {
      if (qoe.current !== session) return;
      qoe.current = null;
      const sample = session.finish(performance.now());
      if (sample) playEvents().sample(sample);
    };
    const onHide = () => {
      if (document.visibilityState !== "hidden") return;
      finish();
      void playEvents().flush(true);
    };
    document.addEventListener("visibilitychange", onHide);
    return () => {
      document.removeEventListener("visibilitychange", onHide);
      finish();
    };
  }, [curId, curEp, active, wide]);
  const seekTo = (t: number) => {
    qoe.current?.seeked();
    video.current?.seek(t);
  };

  // Reset per item / episode.
  useEffect(() => {
    setTime(0);
    setDuration(play.data?.duration ?? 0);
    setBuffered(0);
    setError("");
    setWaiting(true);
    setPaused(false);
    setScrub(null);
    advancing.current = false;
    lastSave.current = 0;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [curId, curEp]);

  // Once the current item plays, warm up the next one's first segment.
  useEffect(() => {
    if (paused || waiting || !active) return undefined;
    const next = items[index + 1];
    if (!next) return undefined;
    const ctrl = new AbortController();
    void (async () => {
      try {
        const detail = await qc.fetchQuery(seriesQuery(next.id));
        const ep = detail.episodes[0]?.ep_num;
        if (!ep) return;
        const info = await qc.fetchQuery(playQuery(next.id, ep));
        await prefetchStart(info.video_url, ctrl.signal);
      } catch {
        /* best effort */
      }
    })();
    return () => ctrl.abort();
  }, [paused, waiting, active, index, items, qc]);

  // ---------- playback events ----------

  const pickEp = useCallback((ep: number) => {
    if (!curId) return;
    setChosen(`${curId}:${ep}`);
    setResume(null);
    setEpMap((m) => ({ ...m, [curId]: ep }));
    if (panel) closeOverlay();
  }, [curId, panel]);

  const nextEp = episodes.find((e) => e.ep_num > curEp)?.ep_num;

  const onTime = (t: number) => {
    setTime(t);
    qoe.current?.time(t);
    if (!curId) return;
    // Rankings: watched to 90% counts as a completion (reported once per session).
    if (duration > 0 && t >= duration * 0.9) playEvents().track({ type: "complete", series_id: curId, ep: curEp });
    if (t - lastSave.current >= SAVE_EVERY_S || t < lastSave.current) {
      lastSave.current = t;
      recordProgress({ id: curId, ep: curEp, t, dur: duration });
    }
    // Auto-advance to the next episode just before the end.
    if (duration > 0 && t >= duration - NEAR_END_S && nextEp && !advancing.current) {
      advancing.current = true;
      pickEp(nextEp);
    }
  };

  const onExpired = (at: number) => {
    // The signed URL ran out mid-episode: get a fresh one and resume where we were.
    if (curId) setResume({ id: curId, ep: curEp, t: at });
    void qc.invalidateQueries({ queryKey: ["play", curId, curEp] });
  };

  // ---------- gestures (mobile) ----------

  const gesture = useRef<GestureState>(initialGesture);
  const longTimer = useRef<number | undefined>(undefined);
  const tapTimer = useRef<number | undefined>(undefined);
  const wasPlaying = useRef(false);

  const apply = (effects: GestureEffect[]) => {
    for (const e of effects) {
      switch (e.type) {
        case "start-long-press-timer":
          window.clearTimeout(longTimer.current);
          longTimer.current = window.setTimeout(() => run(longPressElapsed(gesture.current)), LONG_PRESS_MS);
          break;
        case "cancel-long-press-timer":
          window.clearTimeout(longTimer.current);
          break;
        case "hold-start":
          window.clearTimeout(tapTimer.current);
          holdingRef.current = true;
          setHolding(true);
          setLockArmed(false);
          try { navigator.vibrate?.(25); } catch { /* unsupported */ }
          break;
        case "hold-armed":
          setLockArmed(e.armed);
          break;
        case "hold-end":
          holdingRef.current = false;
          setHolding(false);
          setLockArmed(false);
          if (e.lock) {
            setSpeedLock(true);
            toast("已锁定 2.0x 倍速 · 点顶部标签解除");
          }
          break;
        case "scrub":
          setScrub({ target: e.target, fraction: e.fraction });
          break;
        case "scrub-end":
          setScrub(null);
          if (e.target !== null) {
            setError("");
            setTime(e.target);
            seekTo(e.target);
          }
          if (wasPlaying.current) video.current?.play();
          break;
        case "single-tap-pending":
          window.clearTimeout(tapTimer.current);
          tapTimer.current = window.setTimeout(() => setClean(!useUi.getState().clean), SINGLE_TAP_DELAY_MS);
          break;
        case "double-tap":
          window.clearTimeout(tapTimer.current);
          video.current?.toggle();
          break;
      }
    }
  };

  const run = (r: { state: GestureState; effects: GestureEffect[] }) => {
    gesture.current = r.state;
    apply(r.effects);
  };

  const onDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (wide || locked || !active || !e.isPrimary || e.button !== 0) return;
    try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    wasPlaying.current = video.current?.el()?.paused === false;
    const rect = e.currentTarget.getBoundingClientRect();
    run(pointerDown(gesture.current, {
      x: e.clientX, y: e.clientY, t: e.timeStamp, base: time, duration, width: rect.width, viewportWidth: window.innerWidth,
    }));
  };
  const onMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (e.isPrimary) run(pointerMove(gesture.current, { x: e.clientX, y: e.clientY }));
  };
  const onUp = (e: ReactPointerEvent<HTMLDivElement>, cancelled: boolean) => {
    if (e.isPrimary) run(pointerUp(gesture.current, { x: e.clientX, y: e.clientY, t: e.timeStamp, cancelled }));
  };

  // While holding for 2x or in fullscreen, the feed must not scroll.
  useEffect(() => {
    const el = box.current;
    if (!el || wide) return undefined;
    const block = (ev: TouchEvent) => {
      if (holdingRef.current || useUi.getState().fullscreen || ev.touches.length >= 2) ev.preventDefault();
    };
    el.addEventListener("touchmove", block, { passive: false });
    return () => el.removeEventListener("touchmove", block);
  }, [wide]);

  // Leaving the feed cancels gestures and timers.
  useEffect(() => {
    if (!active) {
      window.clearTimeout(longTimer.current);
      window.clearTimeout(tapTimer.current);
      gesture.current = initialGesture;
      holdingRef.current = false;
      setHolding(false);
    }
  }, [active]);

  // ---------- actions ----------

  // Signed in, these are saved to the account (optimistic; undone with a toast if the server refuses).
  const onFav = () => {
    if (!curId) return;
    void toggleSeriesFav(curId).then((on) => { if (on !== null) toast(on ? "已收藏" : "已取消收藏", on ? "star" : "info"); });
  };
  const onLike = () => {
    if (!curId) return;
    void toggleSeriesLike(curId).then((on) => { if (on !== null) toast(on ? "已加入喜欢" : "已取消喜欢", on ? "heart" : "info"); });
  };
  const onShare = () => {
    if (!curId) return;
    const url = `${window.location.origin}${href.video(curId, curEp)}`;
    navigator.clipboard?.writeText(url).then(() => toast("链接已复制", "ok")).catch(() => toast(url));
  };
  const onComments = () => openPanel("comments");

  const openPanel = (p: Panel) => {
    if (panel) {
      setPanel(p);
      return;
    }
    setPanel(p);
    openOverlay(() => setPanel(null));
  };

  const exitFullscreenNow = useCallback(() => {
    setFullscreen(false);
    setRotated(false);
    try { (screen.orientation as ScreenOrientation & { unlock?: () => void }).unlock?.(); } catch { /* unsupported */ }
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
  }, [setFullscreen]);

  const enterFullscreen = async () => {
    setFullscreen(true);
    openOverlay(exitFullscreenNow);
    try { await document.documentElement.requestFullscreen?.({ navigationUI: "hide" }); } catch { /* iPhone: no element fullscreen */ }
    const info = play.data;
    if (info && info.width > info.height) {
      try {
        await (screen.orientation as ScreenOrientation & { lock?: (o: string) => Promise<void> }).lock?.("landscape");
      } catch {
        // Cannot rotate the device: rotate the stage with CSS instead.
        setRotated(window.innerHeight > window.innerWidth);
      }
    }
  };

  useEffect(() => {
    const onChange = () => {
      setDocFullscreen(!!document.fullscreenElement);
      if (!document.fullscreenElement && useUi.getState().fullscreen && !wide) closeOverlay();
    };
    document.addEventListener("fullscreenchange", onChange);
    return () => document.removeEventListener("fullscreenchange", onChange);
  }, [wide]);

  const toggleDesktopFullscreen = () => {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
    else void root.current?.requestFullscreen().catch(() => toast("无法进入全屏"));
  };

  const togglePip = async () => {
    try {
      const v = video.current?.el();
      if (document.pictureInPictureElement) await document.exitPictureInPicture();
      else if (v && document.pictureInPictureEnabled) await v.requestPictureInPicture();
      else toast("当前环境不支持画中画");
    } catch {
      toast("画中画开启失败");
    }
  };

  const cycleRate = () => {
    if (speedLock) {
      setSpeedLock(false);
      return;
    }
    setRate(RATES[(RATES.indexOf(rate) + 1) % RATES.length]!);
  };

  // ---------- desktop: idle hide + keyboard ----------

  const idleTimer = useRef<number | undefined>(undefined);
  const wake = useCallback(() => {
    setIdle(false);
    window.clearTimeout(idleTimer.current);
    idleTimer.current = window.setTimeout(() => setIdle(true), IDLE_MS);
  }, []);
  useEffect(() => () => window.clearTimeout(idleTimer.current), []);

  useEffect(() => {
    if (!active || !wide) return undefined;
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA") && (target as HTMLInputElement).type !== "range") return;
      if (panel && e.key === "Escape") { closeOverlay(); e.stopPropagation(); return; }
      const handled = (() => {
        switch (e.key) {
          case "ArrowDown": go(1); return true;
          case "ArrowUp": go(-1); return true;
          case " ": video.current?.toggle(); return true;
          case "ArrowLeft": seekTo(Math.max(0, time - 5)); return true;
          case "ArrowRight": seekTo(time + 5); return true;
          case "f": case "F": toggleDesktopFullscreen(); return true;
          case "t": case "T": setTheater(!theater); return true;
          case "m": case "M": setMuted(!muted); return true;
          case "l": case "L": onLike(); return true;
          case "s": case "S": onFav(); return true;
          case "c": case "C": onComments(); return true;
          case "e": case "E": openPanel("episodes"); return true;
          default: return false;
        }
      })();
      if (handled) {
        e.preventDefault();
        e.stopPropagation();
        wake();
      }
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  });

  // ---------- render ----------

  if (!items.length) {
    return <div className="absolute inset-0 bg-black flex items-center justify-center"><Spinner /></div>;
  }

  const info = play.data;
  const isLandscape = !!info && info.width > info.height;
  const viewportLandscape = typeof window !== "undefined" && window.innerWidth > window.innerHeight;
  const fit: "contain" | "cover" = wide ? "contain" : isLandscape ? (fullscreen || viewportLandscape ? "contain" : landscapeFit) : viewportLandscape ? "contain" : "cover";
  const showUi = !fullscreen && !clean;
  const mobileBottom = `calc(var(--sab) + ${bottomInset}px)`;

  return (
    <div ref={root} className={`absolute inset-0 overflow-hidden bg-black ${wide ? "player-desktop" : ""} ${wide && idle ? "is-idle" : ""}`}
      onPointerMove={() => { if (wide) wake(); }}>
      <div className="absolute inset-0" inert={locked || undefined}>
        {/* Ambient background: the cover, blurred (hidden in fullscreen). */}
        {!fullscreen && cur?.cover && (
          <div className="absolute inset-0 scale-110 blur-3xl opacity-25 pointer-events-none">
            <img src={cur.cover} alt="" className="w-full h-full object-cover" />
          </div>
        )}

        <div ref={box} onScroll={onScroll}
          className={`absolute inset-y-0 left-0 no-bar ${fullscreen ? "overflow-hidden" : "overflow-y-scroll snap-y snap-mandatory overscroll-y-contain"}`}
          style={{ right: wide && panel ? 372 : 0, transition: "right .3s cubic-bezier(.2,0,0,1)" }}>
          {items.map((v, i) => {
            const isActive = i === index;
            const stage = rotated ? "fs-landscape" : !wide && fullscreen ? "fs-immersive" : "absolute inset-0";
            return (
              <section key={v.id} style={!isActive && fullscreen ? { display: "none" } : undefined}
                className={`relative w-full h-full snap-start snap-always ${fullscreen && isActive ? "" : "overflow-hidden"}`}>
                {isActive && info ? (
                  <div className={stage}>
                    <div className="absolute inset-0 overflow-hidden" style={{ touchAction: fullscreen ? "none" : "pan-y" }}
                      onPointerDown={onDown} onPointerMove={onMove}
                      onPointerUp={(e) => onUp(e, false)} onPointerCancel={(e) => onUp(e, true)}
                      onClick={() => { if (wide) video.current?.toggle(); }}>
                      <VideoSurface
                        ref={video}
                        src={info.video_url}
                        poster={fullscreen ? null : info.poster ?? v.cover}
                        active={active}
                        muted={muted}
                        volume={volume}
                        rate={effectiveRate}
                        fit={fit}
                        startAt={startAt}
                        onTime={onTime}
                        onDuration={(d) => Number.isFinite(d) && setDuration(d)}
                        onBuffered={setBuffered}
                        onPlaying={() => {
                          qoe.current?.playing(performance.now());
                          setPaused(false);
                          setWaiting(false);
                          setError("");
                          if (curId) playEvents().track({ type: "start", series_id: curId, ep: curEp });
                        }}
                        onPause={() => setPaused(true)}
                        onWaiting={() => {
                          qoe.current?.waiting(performance.now());
                          setWaiting(true);
                        }}
                        onSize={(_, h) => qoe.current?.size(h)}
                        onEnded={() => {
                          if (curId) playEvents().track({ type: "complete", series_id: curId, ep: curEp });
                          if (nextEp) pickEp(nextEp);
                        }}
                        onError={(message) => {
                          qoe.current?.fail(message);
                          setError(message);
                        }}
                        onExpired={onExpired}
                        onAutoplayMuted={() => setMuted(true)}
                      />
                    </div>

                    {!wide && scrub && <ScrubOverlay target={scrub.target} fraction={scrub.fraction} duration={duration} />}
                    {paused && !error && !scrub && <PauseIndicator />}
                    {waiting && !paused && !error && (
                      <div className="absolute inset-0 z-10 flex items-center justify-center pointer-events-none"><Spinner size={36} /></div>
                    )}
                    {error && <ErrorCard detail={error} onReload={() => { setError(""); setWaiting(true); video.current?.reload(); }} />}

                    {!wide && (!clean || holding) && (holding || speedLock || rate !== 1) && (
                      <SpeedChip rate={effectiveRate} locked={speedLock} holding={holding}
                        top={rotated || viewportLandscape ? "14px" : "calc(var(--sat) + 54px)"}
                        onClick={() => {
                          if (speedLock) { setSpeedLock(false); toast("已解除倍速锁定"); }
                          else if (rate !== 1 && !holding) setRate(1);
                        }} />
                    )}
                    {!wide && holding && <LockHint armed={lockArmed} />}

                    {!wide && fullscreen && !clean && !locked && (
                      <FullscreenControls paused={paused} time={time} duration={duration} rate={effectiveRate} rotated={rotated || viewportLandscape}
                        onToggle={() => video.current?.toggle()} onSeek={(t) => { setTime(t); seekTo(t); }}
                        onRate={cycleRate} onLock={() => setLocked(true)} onExit={closeOverlay} />
                    )}
                  </div>
                ) : (
                  <div className="absolute inset-0">
                    <Img src={v.cover} eager={Math.abs(i - index) <= 1} />
                    <div className="absolute inset-0" style={{ background: isActive && playLocked ? "rgba(0,0,0,.55)" : "rgba(0,0,0,.25)" }} />
                    {isActive && playLocked && series.data ? (
                      <Paywall series={series.data} ep={curEp} intent={chosen === `${v.id}:${curEp}`} wide={wide} />
                    ) : isActive && <div className="absolute inset-0 flex items-center justify-center"><Spinner /></div>}
                  </div>
                )}

                {/* Readability gradient (none in clean mode: video only). */}
                {showUi && (
                  <div className="absolute inset-0 pointer-events-none"
                    style={{ background: "linear-gradient(0deg, rgba(8,11,15,.86) 0, rgba(11,14,20,.18) 26%, transparent 48%)" }} />
                )}

                {/* Landscape video on a portrait phone: fill/fit toggle and landscape fullscreen. */}
                {isActive && showUi && !wide && isLandscape && !viewportLandscape && (
                  <div className="absolute left-1/2 -translate-x-1/2 z-20 flex items-center gap-2" style={{ top: "64%" }}>
                    <button onClick={() => setLandscapeFit((f) => (f === "contain" ? "cover" : "contain"))}
                      className="ov-chip-strong backdrop-blur-md px-3 h-7 rounded-full text-[11.5px] font-semibold pressable elev-1">
                      {landscapeFit === "contain" ? "放大铺满" : "完整适应"}
                    </button>
                    <button onClick={() => void enterFullscreen()}
                      className="ov-chip-strong backdrop-blur-md px-3 h-7 rounded-full text-[11.5px] font-semibold flex items-center gap-1 pressable elev-1">
                      <Expand size={12} />横屏全屏
                    </button>
                  </div>
                )}

                {showUi && mode === "context" && onBack && (wide
                  ? <DesktopContextHeader title={title} index={index} total={items.length} onBack={onBack} />
                  : <ContextHeader title={title} index={index} total={items.length} onBack={onBack} />)}

                {showUi && !wide && (
                  <>
                    <MobileRail bottom={`calc(var(--sab) + ${bottomInset + 92}px)`} author={v.author?.name ?? "ReelGuo"}
                      liked={isLiked(v.id)} likeCount={((isActive ? series.data?.counts.like : undefined) ?? v.like_count) + bump(isLiked(v.id))} onLike={onLike}
                      fav={isFav(v.id)} favCount={((isActive ? series.data?.counts.collect : undefined) ?? v.collect_count) + bump(isFav(v.id))} onFav={onFav}
                      comments={(isActive ? series.data?.counts.comment : undefined) ?? v.comment_count} onComments={onComments}
                      onShare={onShare} rate={effectiveRate} onRate={cycleRate} />
                    <MobileInfo bottom={mobileBottom} title={v.title} description={v.description} total={isActive ? episodes.length : v.episode_count}
                      episode={isActive ? curEp : 1} onDetail={() => openPanel("episodes")} onFullscreen={() => void enterFullscreen()}
                      seek={isActive ? (
                        <SeekLine progress={duration ? time / duration : 0} duration={duration} height={22}
                          onSeek={(t) => { setError(""); setTime(t); seekTo(t); }} />
                      ) : undefined} />
                  </>
                )}

                {!fullscreen && wide && isActive && (
                  <>
                    <DesktopRail index={index} total={items.length} onPrev={() => go(-1)} onNext={() => go(1)}
                      liked={isLiked(v.id)} likeCount={(series.data?.counts.like ?? v.like_count) + bump(isLiked(v.id))} onLike={onLike}
                      comments={series.data?.counts.comment ?? v.comment_count} onComments={onComments}
                      fav={isFav(v.id)} favCount={(series.data?.counts.collect ?? v.collect_count) + bump(isFav(v.id))} onFav={onFav}
                      onEpisodes={() => openPanel("episodes")} onShare={onShare} />
                    <DesktopMeta author={v.author?.name ?? "ReelGuo"} episode={curEp} total={episodes.length} score={v.score}
                      title={info?.title ? `${v.title} · ${info.title}` : v.title} description={v.description} />
                  </>
                )}
              </section>
            );
          })}
        </div>

        {wide && (
          <DesktopControls paused={paused} time={time} duration={duration} buffered={buffered} rate={rate} muted={muted} volume={volume}
            theater={theater} fullscreen={docFullscreen} nextTitle={nextEp ? "下一集" : "下一部 ↓"} nextDisabled={!nextEp && index >= items.length - 1}
            onToggle={() => video.current?.toggle()} onSeek={(t) => { setTime(t); seekTo(t); }}
            onNext={() => (nextEp ? pickEp(nextEp) : go(1))} onRate={setRate} onMute={() => setMuted(!muted)} onVolume={setVolume}
            onPip={() => void togglePip()} onLock={() => setLocked(true)} onTheater={() => setTheater(!theater)} onFullscreen={toggleDesktopFullscreen} />
        )}

        {/* Episodes and comments: docked on the right on desktop, a bottom sheet on mobile. */}
        {wide ? (
          <div className="absolute top-0 right-0 bottom-0 z-40 bg-s1 overflow-hidden transition-[width] duration-300 border-l border-white/5"
            style={{ width: panel ? 372 : 0, boxShadow: panel ? "-14px 0 40px rgba(0,0,0,.35)" : "none" }}>
            {panel === "comments" && curId && series.data ? (
              <div className="w-[372px] h-full flex flex-col pt-2">
                <CommentsPanel target={{ kind: "series", id: curId, ep: curEp, title: series.data.title }} total={series.data.counts.comment} onClose={closeOverlay} />
              </div>
            ) : series.data && (
              <div className="w-[372px] h-full flex flex-col pt-3">
                <div className="flex items-center px-4 h-10 shrink-0">
                  <span className="text-[13.5px] font-bold text-on">选集</span>
                  <button onClick={closeOverlay} className="ml-auto text-[12px] text-mut hover:text-on">关闭</button>
                </div>
                <EpisodeGrid series={series.data} current={curEp} onPick={pickEp} locked={lockedFn} />
              </div>
            )}
          </div>
        ) : (
          <Sheet open={!!panel} onClose={closeOverlay}>
            {series.data && panel === "comments" && curId ? (
              <CommentsPanel target={{ kind: "series", id: curId, ep: curEp, title: series.data.title }} total={series.data.counts.comment} onClose={closeOverlay} />
            ) : series.data && panel && (
              <DetailPanel series={series.data} panel={panel} current={curEp} onPanel={setPanel} onPick={pickEp} onClose={closeOverlay} locked={lockedFn} />
            )}
          </Sheet>
        )}
      </div>

      {!wide && showUi && !locked && active && (
        <MobileTopChips muted={muted} onMute={() => setMuted(!muted)}
          onReport={() => cur && useReport.getState().open({ type: "series", id: cur.id, label: `剧集：${cur.title}` })} />
      )}
      {locked && <ControlsLock onUnlock={() => { setLocked(false); wake(); }} />}
    </div>
  );
}
