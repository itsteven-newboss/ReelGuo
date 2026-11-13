/**
 * Playback quality for one play of one episode: how long until the first frame, how often and how long it stalled,
 * how much was watched, the best quality reached, and any fatal error. Sent with the anonymous play events when the
 * viewer leaves the episode; the admin 数据概览 page aggregates it.
 */
export type QoeSample = {
  series_id: number;
  ep: number;
  startup_ms: number | null;
  rebuffer_count: number;
  rebuffer_ms: number;
  watched_s: number;
  max_height: number | null;
  error: string | null;
  device: "mobile" | "desktop";
  browser: Browser;
};

export type Browser = "chrome" | "safari" | "firefox" | "edge" | "samsung" | "other";

/** Browser family from the user agent (order matters: Edge and Samsung also say "Chrome", Chrome says "Safari"). */
export function browserFamily(ua: string): Browser {
  if (/Edg(e|A|iOS)?\//.test(ua)) return "edge";
  if (/SamsungBrowser\//.test(ua)) return "samsung";
  if (/Firefox\/|FxiOS\//.test(ua)) return "firefox";
  if (/Chrome\/|CriOS\//.test(ua)) return "chrome";
  if (/Safari\//.test(ua)) return "safari";
  return "other";
}

/** Error codes the API accepts: hls.js `details` values already fit; anything else becomes a generic code. */
function errorCode(message: string): string {
  return /^[A-Za-z0-9_.:-]{1,64}$/.test(message) ? message : "mediaError";
}

export class PlaybackSession {
  private readonly startedAt: number;
  private firstFrameAt: number | null = null;
  private stallSince: number | null = null;
  private rebufferCount = 0;
  private rebufferMs = 0;
  private watched = 0;
  private lastTime: number | null = null;
  private maxHeight: number | null = null;
  private error: string | null = null;

  constructor(readonly seriesId: number, readonly ep: number, private readonly device: QoeSample["device"], private readonly browser: Browser, now: number) {
    this.startedAt = now;
  }

  playing(now: number) {
    if (this.firstFrameAt === null) this.firstFrameAt = now;
    if (this.stallSince !== null) {
      this.rebufferMs += now - this.stallSince;
      this.stallSince = null;
    }
  }

  /** Stalls before the first frame are part of the startup time, not rebuffering. */
  waiting(now: number) {
    if (this.firstFrameAt === null || this.stallSince !== null) return;
    this.stallSince = now;
    this.rebufferCount++;
  }

  /** Seeking also fires "waiting"; the stall it causes isn't the network's fault. */
  seeked() {
    if (this.stallSince !== null) {
      this.stallSince = null;
      this.rebufferCount = Math.max(0, this.rebufferCount - 1);
    }
    this.lastTime = null;
  }

  /** Watched time from the playhead: forward steps under 2 s count (bigger jumps are seeks). */
  time(t: number) {
    if (this.lastTime !== null) {
      const step = t - this.lastTime;
      if (step > 0 && step < 2) this.watched += step;
    }
    this.lastTime = t;
  }

  size(height: number) {
    if (height > 0 && (this.maxHeight === null || height > this.maxHeight)) this.maxHeight = height;
  }

  fail(message: string) {
    this.error ??= errorCode(message);
  }

  /** Null when nothing happened worth reporting (the episode was skipped before anything loaded). */
  finish(now: number): QoeSample | null {
    if (this.stallSince !== null) {
      this.rebufferMs += now - this.stallSince;
      this.stallSince = null;
    }
    if (this.firstFrameAt === null && this.error === null && now - this.startedAt < 3000) return null;
    return {
      series_id: this.seriesId,
      ep: this.ep,
      startup_ms: this.firstFrameAt === null ? null : Math.round(this.firstFrameAt - this.startedAt),
      rebuffer_count: this.rebufferCount,
      rebuffer_ms: Math.round(this.rebufferMs),
      watched_s: Math.round(this.watched * 10) / 10,
      max_height: this.maxHeight,
      error: this.error,
      device: this.device,
      browser: this.browser,
    };
  }
}
