import { beforeEach, describe, expect, it } from "vitest";
import {
  choiceAt, fallbackOption, prefetchTargets, repairPlace, sceneName, scoresOf, seenScenes, truncateAt, useStoryLocal, visibleOptions,
  type Story, type StoryOption, type StoryScene,
} from "./story";

const opt = (id: number, label: string, target: number, is_default = false, extra: Partial<StoryOption> = {}): StoryOption => ({
  id, label, description: null, target, is_default, effects: [], conditions: [], ...extra,
});

const scene = (ep: number, extra: Partial<StoryScene> = {}): StoryScene => ({
  ep_num: ep, title: `S${ep} 标题`, duration: 90, is_free: true, price: 0, outcome: "ending", prompt: null, lead_seconds: 0,
  countdown: null, next: null, ending: null, ending_title: null, options: [], ...extra,
});

/** 《深夜来客》: S1 → S2 / S3; S2 → S4 / E5; S3 continues to S4; S4 → E6 / E7. */
const story: Story = {
  series_id: 9, start: 1, ending_count: 3, progress: null,
  variables: [{ key: "v1", name: "好感度", initial: 1 }],
  scenes: [
    scene(1, { outcome: "choice", options: [
      opt(11, "开门", 2, true, { effects: [{ variable: "v1", add: 2 }] }),
      opt(12, "隔门说话", 3, false, { effects: [{ variable: "v1", add: -1 }] }),
    ] }),
    scene(2, { outcome: "choice", lead_seconds: 5, options: [
      opt(21, "留下帮她", 4, false, { effects: [{ variable: "v1", add: 1 }, { variable: "gone", add: 5 }] }),
      opt(22, "送她离开", 5, true),
    ] }),
    scene(3, { outcome: "continue", next: 4 }),
    scene(4, { outcome: "choice", options: [
      opt(41, "说出实情", 6, true, { conditions: [{ variable: "v1", op: "gte", value: 3 }] }),
      opt(42, "替她隐瞒", 7, false),
    ] }),
    scene(5, { ending: 1, title: "", ending_title: null }),
    scene(6, { ending: 2, title: "", ending_title: null }),
    scene(7, { ending: 3, title: "结局·共犯", ending_title: "共犯" }),
  ],
};

describe("story helpers", () => {
  it("shows the options lead seconds before the end, never before the start", () => {
    expect(choiceAt(story.scenes[0]!, 90)).toBe(90);
    expect(choiceAt(story.scenes[1]!, 90)).toBe(85);
    expect(choiceAt(story.scenes[1]!, 3)).toBe(0);
  });

  it("going back to a choice drops it and everything after", () => {
    const path = [{ scene: 1, option: 11 }, { scene: 2, option: 21 }, { scene: 4, option: 42 }];
    expect(truncateAt(path, 2)).toEqual([{ scene: 1, option: 11 }]);
    expect(truncateAt(path, 1)).toEqual([]);
    expect(truncateAt(path, 3)).toEqual(path);
  });

  it("preloads the default option first, at most two, one on data saver", () => {
    expect(prefetchTargets(story.scenes[1]!, false)).toEqual([5, 4]);
    expect(prefetchTargets(story.scenes[1]!, true)).toEqual([5]);
    expect(prefetchTargets(story.scenes[2]!, false)).toEqual([4]);
    expect(prefetchTargets(story.scenes[4]!, false)).toEqual([]);
  });

  it("counts as seen the start, the path's scenes and targets, the current scene and the endings", () => {
    const seen = seenScenes(story, { scene: 4, path: [{ scene: 1, option: 12 }], endings: [5] }, [6]);
    expect([...seen].sort()).toEqual([1, 3, 4, 5, 6]);
  });

  it("names endings only once found", () => {
    expect(sceneName(story.scenes[4]!, {})).toBe("？？？");
    expect(sceneName(story.scenes[4]!, { 5: "各自安好" })).toBe("各自安好");
    expect(sceneName(story.scenes[6]!, {})).toBe("共犯");
    expect(sceneName(story.scenes[0]!, {})).toBe("S1 标题");
  });

  it("repairs a place the story no longer fits", () => {
    expect(repairPlace(story, { scene: 99, path: [{ scene: 1, option: 11 }, { scene: 2, option: 999 }], endings: [5, 3, 5] }))
      .toEqual({ scene: 1, path: [{ scene: 1, option: 11 }], endings: [5] });
    expect(repairPlace(story, { scene: 99, path: [], endings: [] }).scene).toBe(1);
  });
});

describe("hidden scores", () => {
  it("add up the effects of the options on the path, ignoring unknown scores", () => {
    expect(scoresOf(story, [])).toEqual({ v1: 1 });
    expect(scoresOf(story, [{ scene: 1, option: 11 }, { scene: 2, option: 21 }])).toEqual({ v1: 4 });
    expect(scoresOf(story, [{ scene: 1, option: 12 }])).toEqual({ v1: 0 });
  });

  it("show an option only when its conditions hold, and fall back to the first shown", () => {
    const s4 = story.scenes[3]!;
    expect(visibleOptions(s4, { v1: 4 }).map((o) => o.id)).toEqual([41, 42]);
    const low = visibleOptions(s4, { v1: 0 });
    expect(low.map((o) => o.id)).toEqual([42]);
    expect(fallbackOption(low)?.id).toBe(42);
    // Nothing qualifies: everything shows rather than nothing.
    const allConditional = { ...s4, options: s4.options.map((o) => ({ ...o, conditions: [{ variable: "v1", op: "eq", value: 99 }] })) };
    expect(visibleOptions(allConditional, { v1: 0 })).toHaveLength(2);
  });
});

describe("the device's copy", () => {
  beforeEach(() => useStoryLocal.setState({ stories: {} }));

  it("keeps endings found and scenes seen when the place changes", () => {
    const { setPlace, rememberTitle } = useStoryLocal.getState();
    // Reached a paid ending without playing it: the name is known, the ending isn't found.
    setPlace(9, { scene: 7, path: [{ scene: 1, option: 12 }, { scene: 4, option: 42 }], endings: [] });
    rememberTitle(9, 7, "共犯");
    expect(useStoryLocal.getState().get(9)!.endings).toEqual([]);
    setPlace(9, { scene: 5, path: [{ scene: 1, option: 11 }, { scene: 2, option: 22 }], endings: [5] });
    setPlace(9, { scene: 1, path: [], endings: [] });
    const mine = useStoryLocal.getState().get(9)!;
    expect(mine.scene).toBe(1);
    expect(mine.endings).toEqual([5]);
    expect(mine.titles).toEqual({ 7: "共犯" });
    expect(mine.seen.sort()).toEqual([1, 5, 7]);
  });
});
