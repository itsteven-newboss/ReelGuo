import { describe, expect, it } from "vitest";
import { browserFamily, PlaybackSession } from "./qoe";

describe("playback quality", () => {
  it("measures startup, stalls, watched time and the best quality", () => {
    const s = new PlaybackSession(7, 2, "mobile", "safari", 1000);
    s.waiting(1100);            // before the first frame: part of startup
    s.playing(1800);            // first frame after 800 ms
    s.size(480);
    for (let t = 0; t <= 10; t += 0.25) s.time(t);
    s.waiting(5000);
    s.size(720);
    s.playing(6200);            // 1.2 s stall
    s.time(40);                 // a seek forward: not watched
    s.time(40.5);
    const sample = s.finish(9000)!;
    expect(sample.startup_ms).toBe(800);
    expect(sample.rebuffer_count).toBe(1);
    expect(sample.rebuffer_ms).toBe(1200);
    expect(sample.watched_s).toBe(10.5);
    expect(sample.max_height).toBe(720);
    expect(sample.error).toBeNull();
    expect(sample).toMatchObject({ series_id: 7, ep: 2, device: "mobile", browser: "safari" });
  });

  it("does not count the stall a seek causes", () => {
    const s = new PlaybackSession(1, 1, "desktop", "chrome", 0);
    s.playing(500);
    s.waiting(1000);
    s.seeked();
    s.playing(1400);
    expect(s.finish(2000)).toMatchObject({ rebuffer_count: 0, rebuffer_ms: 0 });
  });

  it("reports failures, closes an open stall, and skips episodes swiped past at once", () => {
    const failed = new PlaybackSession(1, 1, "desktop", "chrome", 0);
    failed.fail("manifestLoadError");
    failed.fail("later error");
    expect(failed.finish(200)).toMatchObject({ error: "manifestLoadError", startup_ms: null });

    const odd = new PlaybackSession(1, 1, "desktop", "chrome", 0);
    odd.fail("MEDIA_ERR_DECODE: PipelineStatus::DECODE_ERROR");
    expect(odd.finish(100)?.error).toBe("mediaError");

    const stalled = new PlaybackSession(1, 1, "mobile", "chrome", 0);
    stalled.playing(300);
    stalled.waiting(1000);
    expect(stalled.finish(4000)).toMatchObject({ rebuffer_count: 1, rebuffer_ms: 3000 });

    expect(new PlaybackSession(1, 1, "mobile", "chrome", 0).finish(800)).toBeNull();
    expect(new PlaybackSession(1, 1, "mobile", "chrome", 0).finish(5000)).toMatchObject({ startup_ms: null, error: null });
  });

  it.each([
    ["Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "safari"],
    ["Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36", "chrome"],
    ["Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0", "edge"],
    ["Mozilla/5.0 (Linux; Android 14; SM-S921B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/26.0 Chrome/122.0 Mobile Safari/537.36", "samsung"],
    ["Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0", "firefox"],
    ["curl/8.0", "other"],
  ])("browser family %#", (ua, family) => {
    expect(browserFamily(ua)).toBe(family);
  });
});
