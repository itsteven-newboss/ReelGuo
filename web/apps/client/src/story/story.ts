import type { Schemas } from "@reelguo/api-client";
import { create } from "zustand";
import { storage } from "../storage";

/** Interactive series (互动剧): pure helpers and the viewer's place, kept on the device (unit-tested). */

export type Story = Schemas["StoryDto"];
export type StoryScene = Schemas["StorySceneDto"];
export type StoryOption = Schemas["StoryOptionDto"];
export type StoryStep = { scene: number; option: number };

/** Where a viewer is in one story: the scene, the choices that led there, the endings found (episode numbers). */
export type StoryPlace = { scene: number; path: StoryStep[]; endings: number[] };

/** The device's copy: a guest's only record, and for everyone the scenes seen and the names of endings found. */
export type LocalStory = StoryPlace & { seen: number[]; titles: Record<number, string>; at: number };

export const sceneOf = (story: Story, ep: number): StoryScene | undefined => story.scenes.find((s) => s.ep_num === ep);

/** The viewer's hidden scores (好感度 …): each starts at its initial value, and every option on the path adds its effects. */
export function scoresOf(story: Story, path: StoryStep[]): Record<string, number> {
  const scores: Record<string, number> = Object.fromEntries(story.variables.map((v) => [v.key, v.initial]));
  for (const step of path) {
    const option = sceneOf(story, step.scene)?.options.find((o) => o.id === step.option);
    for (const e of option?.effects ?? []) if (e.variable in scores) scores[e.variable] = scores[e.variable]! + e.add;
  }
  return scores;
}

const holds = (op: string, score: number, value: number) => (op === "gte" ? score >= value : op === "lte" ? score <= value : op === "eq" ? score === value : true);

/** The options this viewer sees: those whose conditions all hold. If none do, all of them (the admin is warned about that case). */
export function visibleOptions(scene: StoryScene, scores: Record<string, number>): StoryOption[] {
  const shown = scene.options.filter((o) => o.conditions.every((c) => !(c.variable in scores) || holds(c.op, scores[c.variable]!, c.value)));
  return shown.length ? shown : scene.options;
}

/** What the countdown plays: the default option when it shows, else the first one shown. */
export const fallbackOption = (options: StoryOption[]): StoryOption | undefined => options.find((o) => o.is_default) ?? options[0];

/** Seconds into the scene when its options appear: `lead_seconds` before the end (never before the start). */
export function choiceAt(scene: StoryScene, duration: number): number {
  return Math.max(0, duration - Math.max(0, scene.lead_seconds));
}

/** The path up to (not including) the first choice made at `scene`: going back there to choose again. */
export function truncateAt(path: StoryStep[], scene: number): StoryStep[] {
  const i = path.findIndex((p) => p.scene === scene);
  return i >= 0 ? path.slice(0, i) : path;
}

/** Scenes whose options to preload while a scene plays: the default first, at most two (one on data saver). */
export function prefetchTargets(scene: StoryScene, saveData: boolean): number[] {
  if (scene.outcome === "continue") return scene.next ? [scene.next] : [];
  if (scene.outcome !== "choice") return [];
  const ordered = [...scene.options].sort((a, b) => Number(b.is_default) - Number(a.is_default)).map((o) => o.target);
  return [...new Set(ordered)].slice(0, saveData ? 1 : 2);
}

/** Scenes the viewer has reached: the device's record, the path's scenes and targets, the current scene and the endings. */
export function seenScenes(story: Story, place: StoryPlace, extra: number[] = []): Set<number> {
  const seen = new Set<number>([story.start, place.scene, ...place.endings, ...extra]);
  for (const step of place.path) {
    seen.add(step.scene);
    const option = sceneOf(story, step.scene)?.options.find((o) => o.id === step.option);
    if (option) seen.add(option.target);
  }
  return seen;
}

/** The scene's name for the viewer: endings show their name once found, otherwise 「？？？」. */
export function sceneName(scene: StoryScene, found: Record<number, string>): string {
  if (scene.outcome === "ending") return scene.ending_title ?? found[scene.ep_num] ?? "？？？";
  return scene.title || `S${scene.ep_num}`;
}

/** A place that fits the story: unknown scenes fall back to the last valid choice or the start, unknown steps are dropped. */
export function repairPlace(story: Story, place: StoryPlace): StoryPlace {
  const path = place.path.filter((p) => sceneOf(story, p.scene)?.options.some((o) => o.id === p.option));
  const scene = sceneOf(story, place.scene) ? place.scene : (path.at(-1)?.scene ?? story.start);
  const endings = [...new Set(place.endings.filter((e) => sceneOf(story, e)?.outcome === "ending"))].sort((a, b) => a - b);
  return { scene, path, endings };
}

const KEY = "rg.story.v1";
const MAX_STORIES = 50;

function load(): Record<number, LocalStory> {
  try {
    const raw = storage.get(KEY);
    const parsed = raw ? (JSON.parse(raw) as Record<string, LocalStory>) : {};
    const out: Record<number, LocalStory> = {};
    for (const [id, v] of Object.entries(parsed)) {
      if (v && typeof v.scene === "number" && Array.isArray(v.path)) {
        out[Number(id)] = { scene: v.scene, path: v.path, endings: v.endings ?? [], seen: v.seen ?? [], titles: v.titles ?? {}, at: v.at ?? 0 };
      }
    }
    return out;
  } catch {
    return {};
  }
}

type StoryState = {
  stories: Record<number, LocalStory>;
  get: (id: number) => LocalStory | undefined;
  /** Sets the place (and remembers the scene as seen). */
  setPlace: (id: number, place: StoryPlace) => void;
  /** Remembers an ending's name (guests get it from the API only when they reach it). It counts as found once it plays. */
  rememberTitle: (id: number, ep: number, title: string | null | undefined) => void;
};

export const useStoryLocal = create<StoryState>((set, get) => {
  const persist = () => {
    const entries = Object.entries(get().stories).sort((a, b) => b[1].at - a[1].at).slice(0, MAX_STORIES);
    storage.set(KEY, JSON.stringify(Object.fromEntries(entries)));
  };
  return {
    stories: load(),
    get: (id) => get().stories[id],
    setPlace: (id, place) => {
      const prev = get().stories[id];
      const seen = [...new Set([...(prev?.seen ?? []), place.scene])];
      const endings = [...new Set([...(prev?.endings ?? []), ...place.endings])].sort((a, b) => a - b);
      set({ stories: { ...get().stories, [id]: { ...place, endings, seen, titles: prev?.titles ?? {}, at: Date.now() } } });
      persist();
    },
    rememberTitle: (id, ep, title) => {
      const prev = get().stories[id];
      if (!prev || !title || prev.titles[ep] === title) return;
      set({ stories: { ...get().stories, [id]: { ...prev, titles: { ...prev.titles, [ep]: title }, at: Date.now() } } });
      persist();
    },
  };
});
