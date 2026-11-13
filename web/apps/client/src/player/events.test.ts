import { describe, expect, it } from "vitest";
import { EventQueue, sessionId, type PlayEvent } from "./events";

describe("play events", () => {
  it("reports each start/complete once per session and batches by 50", async () => {
    const sent: PlayEvent[][] = [];
    const q = new EventQueue(({ events }) => { sent.push(events); });
    q.track({ type: "start", series_id: 1, ep: 1 });
    q.track({ type: "start", series_id: 1, ep: 1 });
    q.track({ type: "complete", series_id: 1, ep: 1 });
    expect(q.size).toBe(2);
    await q.flush(false);
    expect(sent).toEqual([[{ type: "start", series_id: 1, ep: 1 }, { type: "complete", series_id: 1, ep: 1 }]]);

    for (let i = 0; i < 120; i++) q.track({ type: "start", series_id: 100 + i, ep: 1 });
    await q.flush(false);
    expect(sent.slice(1).map((b) => b.length)).toEqual([50, 50, 20]);
  });

  it("sends playback samples with the events, 20 per request", async () => {
    const batches: { events: number; samples: number }[] = [];
    const q = new EventQueue(({ events, samples }) => { batches.push({ events: events.length, samples: samples.length }); });
    const sample = { series_id: 1, ep: 1, startup_ms: 500, rebuffer_count: 0, rebuffer_ms: 0, watched_s: 3, max_height: 720, error: null, device: "mobile" as const, browser: "chrome" as const };
    q.track({ type: "start", series_id: 1, ep: 1 });
    for (let i = 0; i < 19; i++) q.sample(sample);
    expect(q.size).toBe(20);
    q.sample(sample);   // the 20th sample triggers a send
    await q.flush(false);
    expect(batches).toEqual([{ events: 1, samples: 20 }]);
  });

  it("send failures never throw", async () => {
    const q = new EventQueue(() => Promise.reject(new Error("offline")));
    q.track({ type: "start", series_id: 1, ep: 1 });
    await expect(q.flush(true)).resolves.toBeUndefined();
  });

  it("keeps one random session id per tab", () => {
    const map = new Map<string, string>();
    const store = { getItem: (k: string) => map.get(k) ?? null, setItem: (k: string, v: string) => void map.set(k, v) };
    const a = sessionId(store);
    expect(a).toMatch(/^[a-f0-9]{32}$/);
    expect(sessionId(store)).toBe(a);
  });
});
