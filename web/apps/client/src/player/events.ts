/**
 * Anonymous playback signals: start / complete for the rankings (热播 / 推荐 / 潜力), plus one playback-quality
 * sample per play (see qoe.ts). Keyed by a random id that lives only for this browser tab session: no account,
 * no device id. Batched and sent every 10 s, and flushed with keepalive when the page is hidden.
 */
import type { QoeSample } from "./qoe";

export type PlayEventType = "start" | "complete";
export type PlayEvent = { type: PlayEventType; series_id: number; ep: number };

const FLUSH_MS = 10_000;
const MAX_BATCH = 50;
const MAX_SAMPLES = 20;

export type EventBatch = { events: PlayEvent[]; samples: QoeSample[] };

function newSessionId(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

export function sessionId(store: Pick<Storage, "getItem" | "setItem"> | null = safeSessionStorage()): string {
  try {
    const existing = store?.getItem("rg.play_session");
    if (existing && /^[a-f0-9]{32}$/.test(existing)) return existing;
    const id = newSessionId();
    store?.setItem("rg.play_session", id);
    return id;
  } catch {
    return memoryId ??= newSessionId();
  }
}
let memoryId: string | undefined;

function safeSessionStorage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

/** Queue with de-duplication: each (type, series, episode) is reported once per session. */
export class EventQueue {
  private pending: PlayEvent[] = [];
  private samples: QoeSample[] = [];
  private seen = new Set<string>();

  constructor(private readonly send: (batch: EventBatch, keepalive: boolean) => Promise<void> | void) {}

  track(event: PlayEvent) {
    const key = `${event.type}:${event.series_id}:${event.ep}`;
    if (this.seen.has(key)) return;
    this.seen.add(key);
    this.pending.push(event);
    if (this.pending.length >= MAX_BATCH) void this.flush(false);
  }

  /** One playback-quality sample per play (not de-duplicated: replays are separate plays). */
  sample(sample: QoeSample) {
    this.samples.push(sample);
    if (this.samples.length >= MAX_SAMPLES) void this.flush(false);
  }

  get size() {
    return this.pending.length + this.samples.length;
  }

  async flush(keepalive: boolean) {
    while (this.pending.length || this.samples.length) {
      const batch = { events: this.pending.splice(0, MAX_BATCH), samples: this.samples.splice(0, MAX_SAMPLES) };
      try {
        await this.send(batch, keepalive);
      } catch {
        /* best effort; never disturb playback */
      }
    }
  }
}

let queue: EventQueue | null = null;

/** Starts the shared queue (once). */
export function playEvents(): EventQueue {
  if (queue) return queue;
  const sid = sessionId();
  queue = new EventQueue(({ events, samples }, keepalive) =>
    fetch("/api/v1/events", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ session_id: sid, events, samples }),
      keepalive,
    }).then(() => undefined));
  window.setInterval(() => void queue?.flush(false), FLUSH_MS);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") void queue?.flush(true);
  });
  return queue;
}
