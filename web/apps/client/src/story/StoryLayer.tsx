import { useQueryClient } from "@tanstack/react-query";
import { ListTree, Lock, RotateCcw, Share2, Undo2 } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { playQuery } from "../api";
import { BRAND } from "../brand";
import { t } from "../i18n";
import { prefetchStart } from "../player/prefetch";
import { href } from "../router";
import { useUi } from "../state/ui";
import { shareEnding } from "./shareCard";
import {
  choiceAt, fallbackOption, prefetchTargets, sceneName, sceneOf, scoresOf, seenScenes, truncateAt, useStoryLocal, visibleOptions,
  type Story, type StoryOption, type StoryPlace, type StoryScene,
} from "./story";
import { savePlace, sendNext, useStoryPlace } from "./useStory";

/** Seconds before the options appear when the next scenes' first seconds start loading. */
const PRELOAD_LEAD_S = 8;

type Phase = { ep: number; kind: "choice" | "ending" } | null;

export type StoryPlayer = {
  /** Call from the video's time updates. */
  onTime: (t: number, duration: number) => void;
  /** Call when the video ends. */
  onEnded: () => void;
  /** The options or the ending card, over the video. */
  overlay: ReactNode;
  /** The options or the ending card are showing (the player hides its own buttons meanwhile). */
  open: boolean;
  /** The story map (剧情图), for the episodes panel. */
  map: ReactNode;
  /** Back to the choice that led to the current scene (or the start). */
  rewind: () => void;
  restart: () => void;
  /** The current scene's name. */
  sceneTitle: string;
  endings: { found: number; total: number };
};

/**
 * Plays an interactive series inside a player: shows the options when a scene ends (or `lead_seconds` earlier), runs
 * the countdown, preloads the next scenes, moves to the chosen scene, shows the ending card, and keeps the viewer's place.
 * The player decides how to play a scene through `onGo(ep, startAt)`.
 */
export function useStoryPlayer({ seriesId, series, story, ep, wide, locked, onGo, onMap }: {
  seriesId: number;
  /** For the 「我解锁了结局」 share card. */
  series?: { title: string; cover?: string | null };
  story: Story | undefined;
  ep: number;
  wide: boolean;
  /** Whether a scene still needs unlocking (its price then shows on the option). */
  locked?: (ep: number) => boolean;
  /** Play another scene (or the same one again) from `startAt` seconds. */
  onGo: (ep: number, startAt: number) => void;
  /** Open the story map. */
  onMap?: () => void;
}): StoryPlayer | null {
  const qc = useQueryClient();
  const toast = useUi((s) => s.toast);
  const info = useStoryPlace(seriesId, story);
  const setPlace = useStoryLocal((s) => s.setPlace);
  const rememberTitle = useStoryLocal((s) => s.rememberTitle);
  const [phase, setPhase] = useState<Phase>(null);
  const pendingChoice = useRef<number | null>(null);
  const preloaded = useRef<number | null>(null);
  const counted = useRef<number | null>(null);
  const scene = story ? sceneOf(story, ep) : undefined;
  const place = info?.place;

  // A new scene: its options show at once when the viewer came back to choose again.
  useEffect(() => {
    if (pendingChoice.current === ep) {
      pendingChoice.current = null;
      setPhase({ ep, kind: "choice" });
    } else {
      setPhase(null);
    }
    preloaded.current = null;
  }, [ep]);

  // The device remembers the scene the viewer is on (also when they arrive from history or a shared link).
  const placeScene = place?.scene;
  useEffect(() => {
    if (!place || !scene || placeScene === ep) return;
    setPlace(seriesId, { ...place, scene: ep });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [seriesId, ep, placeScene, scene]);

  const preload = useCallback((s: StoryScene) => {
    const saveData = !!(navigator as Navigator & { connection?: { saveData?: boolean } }).connection?.saveData;
    for (const target of prefetchTargets(s, saveData)) {
      if (locked?.(target)) continue;
      void qc.fetchQuery(playQuery(seriesId, target)).then((p) => prefetchStart(p.video_url)).catch(() => { /* best effort */ });
    }
  }, [qc, seriesId, locked]);

  const advance = (target: number, option: StoryOption | null, timedOut: boolean) => {
    if (!story || !place) return;
    const from = ep;
    // An ending counts as found once it plays (a paid one may stop at the unlock card).
    const next: StoryPlace = {
      scene: target,
      path: option ? [...place.path, { scene: from, option: option.id }].slice(-200) : place.path,
      endings: place.endings,
    };
    setPlace(seriesId, next);
    setPhase(null);
    onGo(target, 0);
    if (timedOut) toast(t("story.timedOut"));
    void sendNext(seriesId, from, option?.id ?? null, timedOut).then((r) => {
      if (!r) return;
      if (r.ending_title) rememberTitle(seriesId, r.scene, r.ending_title);
      if (!timedOut && r.share !== null && r.share !== undefined) toast(t("story.share", { percent: r.share }));
    });
  };

  const restart = () => {
    if (!story || !place) return;
    const next: StoryPlace = { scene: story.start, path: [], endings: place.endings };
    setPlace(seriesId, next);
    void savePlace(seriesId, next);
    setPhase(null);
    onGo(story.start, 0);
  };

  /** Back to a choice the viewer reached, to choose again: the scene's last moments play under its options. */
  const jumpToChoice = (target: number) => {
    if (!story || !place) return;
    const s = sceneOf(story, target);
    if (!s || s.outcome !== "choice") return;
    const next: StoryPlace = { scene: target, path: truncateAt(place.path, target), endings: place.endings };
    setPlace(seriesId, next);
    void savePlace(seriesId, next);
    if (target === ep) {
      setPhase({ ep, kind: "choice" });
      return;
    }
    pendingChoice.current = target;
    onGo(target, Math.max(0, s.duration - 1.5));
  };

  const rewind = () => {
    const last = place?.path.at(-1);
    if (last) jumpToChoice(last.scene);
    else restart();
  };

  /** The ending is playing: it counts as found (saved to the account too, which the API only does for endings it can see were played). */
  const found = () => {
    if (!place || place.endings.includes(ep) || counted.current === ep) return;
    counted.current = ep;
    const next: StoryPlace = { ...place, scene: ep, endings: [...place.endings, ep] };
    setPlace(seriesId, next);
    void savePlace(seriesId, next);
  };

  if (!story || !place || !info) return null;

  const onTime = (t: number, duration: number) => {
    if (scene?.outcome === "ending" && t >= 0.5) found();
    if (!scene || duration <= 0 || phase) return;
    const at = scene.outcome === "choice" ? choiceAt(scene, duration) : duration;
    if (preloaded.current !== ep && t >= at - PRELOAD_LEAD_S) {
      preloaded.current = ep;
      preload(scene);
    }
    if (scene.outcome === "choice" && t >= Math.min(at, duration - 0.25)) setPhase({ ep, kind: "choice" });
  };

  const onEnded = () => {
    if (!scene) return;
    if (scene.outcome === "choice") setPhase({ ep, kind: "choice" });
    else if (scene.outcome === "continue" && scene.next) advance(scene.next, null, false);
    else {
      if (scene.outcome === "ending") found();
      setPhase({ ep, kind: "ending" });
    }
  };

  // The hidden scores decide which options show.
  const options = scene ? visibleOptions(scene, scoresOf(story, place.path)) : [];
  const share = series && scene?.outcome === "ending" && place.endings.includes(ep)
    ? async () => {
      try {
        const done = await shareEnding({
          series: series.title, cover: series.cover, ending: sceneName(scene, info.titles), number: scene.ending ?? 0,
          total: story.ending_count, url: `${window.location.origin}${href.video(seriesId)}`, brand: BRAND,
        });
        if (done === "saved") toast(t("story.shareSaved"), "ok");
      } catch {
        toast(t("story.shareFailed"));
      }
    }
    : undefined;
  const overlay = !scene || !phase || phase.ep !== ep ? null : phase.kind === "choice" ? (
    <ChoiceOverlay key={ep} scene={scene} options={options} wide={wide}
      priceOf={(t) => (locked?.(t) ? sceneOf(story, t)?.price ?? 0 : 0)}
      onPick={(o, timedOut) => advance(o.target, o, timedOut)} />
  ) : (
    <EndingCard story={story} scene={scene} place={place} titles={info.titles} onRewind={rewind} onRestart={restart} onMap={onMap} onShare={share} />
  );

  return {
    onTime,
    onEnded,
    overlay,
    open: overlay !== null,
    map: (
      <StoryMap story={story} place={place} seen={seenScenes(story, place, info.seen)} titles={info.titles} current={ep}
        onJump={jumpToChoice} onRestart={restart} />
    ),
    rewind,
    restart,
    // An ending is named once it played (not while its unlock card shows).
    sceneTitle: !scene ? "" : scene.outcome === "ending" && !place.endings.includes(ep) ? t("story.ending") : sceneName(scene, info.titles),
    endings: { found: place.endings.length, total: story.ending_count },
  };
}

/** The question and 2-4 options over the last frame, with the countdown when the admin set one. Keys 1-4 choose on desktop. */
function ChoiceOverlay({ scene, options, wide, priceOf, onPick }: {
  scene: StoryScene;
  /** The options this viewer sees (their scores decide). */
  options: StoryOption[];
  wide: boolean; priceOf: (ep: number) => number; onPick: (option: StoryOption, timedOut: boolean) => void;
}) {
  const fallback = scene.countdown ? fallbackOption(options) : undefined;
  const total = (scene.countdown ?? 0) * 1000;
  const [left, setLeft] = useState(total);
  const fired = useRef(false);
  const pick = useCallback((o: StoryOption, timedOut: boolean) => {
    if (fired.current) return;
    fired.current = true;
    onPick(o, timedOut);
  }, [onPick]);

  useEffect(() => {
    if (!fallback || !total) return undefined;
    const started = Date.now();
    const id = window.setInterval(() => {
      const remaining = Math.max(0, total - (Date.now() - started));
      setLeft(remaining);
      if (remaining <= 0) {
        window.clearInterval(id);
        pick(fallback, true);
      }
    }, 100);
    return () => window.clearInterval(id);
  }, [fallback, total, pick]);

  useEffect(() => {
    if (!wide) return undefined;
    const onKey = (e: KeyboardEvent) => {
      const option = options[Number(e.key) - 1];
      if (!option) return;
      e.preventDefault();
      e.stopPropagation();
      pick(option, false);
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, [wide, options, pick]);

  return (
    <div className="absolute inset-0 z-[35] flex flex-col justify-end pointer-events-none a-fade"
      style={{ background: "linear-gradient(180deg, rgba(0,0,0,0) 20%, rgba(0,0,0,.78) 52%, rgba(0,0,0,.94) 72%)" }}>
      <div role="dialog" aria-label={t("story.choose")} className="pointer-events-auto w-full max-w-[520px] mx-auto px-4 flex flex-col gap-2.5 a-rise"
        style={{ paddingBottom: wide ? 72 : "calc(var(--sab, 0px) + 28px)" }}>
        {scene.prompt && <p className="text-[17px] font-bold leading-snug ov-ink ov-shadow">{scene.prompt}</p>}
        {fallback && (
          <div>
            <div className="h-[3px] rounded-full overflow-hidden bg-white/20">
              <div className="h-full bg-primary origin-left" style={{ transform: `scaleX(${total ? left / total : 0})` }} />
            </div>
            <p className="mt-1.5 text-[11.5px] ov-ink-4 tabular-nums">{t("story.countdown", { count: Math.ceil(left / 1000), label: fallback.label })}</p>
          </div>
        )}
        <div className="flex flex-col gap-2">
          {options.map((o, i) => {
            const price = priceOf(o.target);
            const isDefault = !!fallback && fallback.id === o.id;
            return (
              <button key={o.id} type="button" onClick={() => pick(o, false)}
                className={`relative text-left rounded-xl px-3.5 py-2.5 border pressable bg-[#26262c] hover:bg-[#303037] ${isDefault ? "border-primary" : "border-white/15"}`}>
                <span className="block pr-20 text-[15px] font-semibold ov-ink">
                  {wide && <span className="mr-1.5 opacity-45 tabular-nums">{i + 1}</span>}{o.label}
                </span>
                {o.description && <span className="block mt-0.5 text-[12px] ov-ink-4">{o.description}</span>}
                {price > 0 ? (
                  <span className="absolute right-3 top-2.5 h-5 px-2 rounded-full flex items-center gap-1 text-[10.5px] font-semibold bg-primary/20 text-primary">
                    <Lock size={10} />{t("billing.coinsN", { n: price })}
                  </span>
                ) : isDefault ? (
                  <span className="absolute right-3 top-2.5 h-5 px-2 rounded-full flex items-center text-[10.5px] font-bold bg-primary text-on-primary">{t("story.default")}</span>
                ) : null}
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}

/** 结局 2 / 3, its name, the endings found so far, and the ways on. */
function EndingCard({ story, scene, place, titles, onRewind, onRestart, onMap, onShare }: {
  story: Story; scene: StoryScene; place: StoryPlace; titles: Record<number, string>;
  onRewind: () => void; onRestart: () => void; onMap?: () => void;
  /** 分享结局: the 「我解锁了结局」 card. */
  onShare?: () => Promise<void>;
}) {
  const [sharing, setSharing] = useState(false);
  const endings = useMemo(() => story.scenes.filter((s) => s.outcome === "ending").sort((a, b) => (a.ending ?? 0) - (b.ending ?? 0)), [story]);
  const isEnding = scene.outcome === "ending";
  const secondary = "h-11 rounded-full border border-white/20 ov-ink text-[14px] font-semibold flex items-center justify-center gap-1.5 pressable";
  return (
    <div className="absolute inset-0 z-[35] flex items-center justify-center px-6 a-fade" style={{ background: "rgba(5,6,9,.84)" }}>
      <div role="dialog" aria-label={t("story.ending")} className="w-full max-w-[320px] text-center flex flex-col gap-2.5 a-scale-in">
        <p className="text-[12px] tracking-[.14em] font-semibold text-primary">{isEnding ? t("story.endingOf", { n: scene.ending, total: story.ending_count }) : t("story.toBeContinued")}</p>
        {isEnding && <p className="text-[24px] font-bold ov-ink">「{sceneName(scene, titles)}」</p>}
        <div className="my-1 flex flex-wrap items-center justify-center gap-1.5 text-[11px]">
          <span className="ov-ink-4">{t("story.endingsFound", { count: place.endings.length })}</span>
          {endings.map((e) => {
            const found = place.endings.includes(e.ep_num);
            return (
              <span key={e.ep_num} className={`h-5 px-2 rounded-full border flex items-center ${found ? "border-primary text-primary" : "border-white/20 ov-ink-4"}`}>
                {found ? sceneName(e, titles) : "？？？"}
              </span>
            );
          })}
        </div>
        {place.path.length > 0 && (
          <button type="button" onClick={onRewind} className="h-11 rounded-full bg-primary text-on-primary text-[14px] font-bold flex items-center justify-center gap-1.5 pressable">
            <Undo2 size={16} />{t("story.backToChoice")}
          </button>
        )}
        <button type="button" onClick={onRestart} className={secondary}><RotateCcw size={15} />{t("story.restart")}</button>
        {onMap && <button type="button" onClick={onMap} className={secondary}><ListTree size={15} />{t("story.mapTab")}</button>}
        {onShare && (
          <button type="button" disabled={sharing} className={`${secondary} disabled:opacity-60`}
            onClick={() => { setSharing(true); void onShare().finally(() => setSharing(false)); }}>
            <Share2 size={15} />{sharing ? t("story.sharing") : t("story.shareEnding")}
          </button>
        )}
      </div>
    </div>
  );
}

/** 剧情图: every scene in order; the ones reached show their names, choices reached can be chosen again. */
export function StoryMap({ story, place, seen, titles, current, onJump, onRestart }: {
  story: Story; place: StoryPlace; seen: Set<number>; titles: Record<number, string>; current: number;
  onJump: (ep: number) => void; onRestart: () => void;
}) {
  const scenes = useMemo(() => [...story.scenes].sort((a, b) =>
    Number(a.outcome === "ending") - Number(b.outcome === "ending") || (a.ending ?? 0) - (b.ending ?? 0) || a.ep_num - b.ep_num), [story]);
  return (
    <div className="flex flex-col h-full min-h-0">
      <div className="shrink-0 flex items-center gap-2 px-4 pb-2">
        <span className="text-[11px] text-mut">{t("story.mapSummary", { found: place.endings.length, total: story.ending_count })}</span>
        <button type="button" onClick={onRestart} className="ml-auto text-[11.5px] text-primary flex items-center gap-1 pressable"><RotateCcw size={12} />{t("story.restart")}</button>
      </div>
      <div className="flex-1 scroll-y px-4 pb-4">
        <ol className="flex flex-col gap-1.5">
          {scenes.map((s) => {
            // Endings count once they played (a paid one may have stopped at the unlock card).
            const isSeen = s.outcome === "ending" ? place.endings.includes(s.ep_num) : seen.has(s.ep_num);
            const name = isSeen ? sceneName(s, titles) : s.outcome === "ending" ? "？？？" : t("story.unseen");
            return (
              <li key={s.ep_num} aria-current={s.ep_num === current ? "true" : undefined}
                className={`flex items-center gap-2.5 rounded-(--r-md) px-3 py-2.5 text-[12.5px] ${isSeen ? "bg-s3 text-on" : "bg-s2 text-dim"} ${s.ep_num === current ? "ring-1 ring-primary" : ""}`}>
                <b className="min-w-[40px] text-[11px] font-semibold text-primary tabular-nums">{s.outcome === "ending" ? t("story.endingN", { n: s.ending }) : `S${s.ep_num}`}</b>
                <span className="flex-1 min-w-0 truncate">{name}</span>
                {isSeen && s.outcome === "choice" && (
                  <button type="button" onClick={() => onJump(s.ep_num)}
                    className="shrink-0 h-6 px-2.5 rounded-full border border-primary/50 text-primary text-[11px] pressable">{t("story.chooseAgain")}</button>
                )}
              </li>
            );
          })}
        </ol>
      </div>
    </div>
  );
}
