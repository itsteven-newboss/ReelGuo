import Hls, { ErrorTypes, Events, type ErrorData } from "hls.js";
import { forwardRef, useEffect, useImperativeHandle, useRef } from "react";

export type VideoSurfaceHandle = {
  el: () => HTMLVideoElement | null;
  toggle: () => void;
  play: () => void;
  pause: () => void;
  seek: (t: number) => void;
  /** Rebuilds the pipeline (after an error), resuming at the current position. */
  reload: () => void;
};

type Props = {
  src: string;
  poster?: string | null;
  /** Inactive surfaces pause and stop downloading (only the visible item plays). */
  active: boolean;
  muted: boolean;
  volume: number;
  rate: number;
  /** cover = fill the screen (portrait video on a portrait screen); contain = letterbox. */
  fit: "cover" | "contain";
  /** Native controls (inline players such as the post page); the feed draws its own. */
  controls?: boolean;
  startAt?: number;
  onTime?: (t: number) => void;
  onDuration?: (d: number) => void;
  onBuffered?: (end: number) => void;
  onPlaying?: () => void;
  onPause?: () => void;
  onWaiting?: () => void;
  onEnded?: () => void;
  onSize?: (w: number, h: number) => void;
  onError?: (message: string) => void;
  /** The signed URL expired (403 from the edge): fetch a fresh one. */
  onExpired?: (at: number) => void;
  /** The browser refused unmuted autoplay; the player falls back to muted. */
  onAutoplayMuted?: () => void;
};

const MAX_RECOVERIES = 4;

function safeFlag(key: string) {
  try {
    return window.localStorage.getItem(key) === "1";
  } catch {
    return false;
  }
}

/** Uses hls.js where MediaSource exists (incl. ManagedMediaSource on iOS 17.1+), else native HLS (older iOS Safari). */
function canUseHlsJs() {
  return Hls.isSupported();
}

export const VideoSurface = forwardRef<VideoSurfaceHandle, Props>(function VideoSurface(props, ref) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const hlsRef = useRef<Hls | null>(null);
  const recoveries = useRef(0);
  /** hls.js must not be told to startLoad() before the master playlist is parsed (it errors in 1.7). */
  const manifestParsed = useRef(false);
  /** The surface was hidden while the manifest was still loading, which aborts it: reload on return. */
  const stoppedEarly = useRef(false);
  const latest = useRef(props);
  latest.current = props;

  const attach = (src: string, startAt: number) => {
    const video = videoRef.current;
    if (!video) return;
    hlsRef.current?.destroy();
    hlsRef.current = null;
    recoveries.current = 0;
    manifestParsed.current = false;
    stoppedEarly.current = false;

    if (canUseHlsJs()) {
      const hls = new Hls({
        // Dev-only verbose logging: localStorage.setItem("rg.hlsDebug", "1")
        debug: import.meta.env.DEV && safeFlag("rg.hlsDebug"),
        startPosition: startAt > 0 ? startAt : -1,
        maxBufferLength: 30,
        maxMaxBufferLength: 60,
        capLevelToPlayerSize: true,
        startFragPrefetch: true,
        // Fast start: begin at a modest rendition, then adapt upwards.
        startLevel: 1,
        fragLoadPolicy: { default: { maxTimeToFirstByteMs: 8000, maxLoadTimeMs: 20000, timeoutRetry: { maxNumRetry: 2, retryDelayMs: 0, maxRetryDelayMs: 0 }, errorRetry: { maxNumRetry: 4, retryDelayMs: 500, maxRetryDelayMs: 4000 } } },
      });
      hls.on(Events.ERROR, (_e, data: ErrorData) => {
        if (import.meta.env.DEV) console.warn("[hls]", data.type, data.details, data.fatal ? "fatal" : "", data.response?.code ?? "", data.error?.message ?? "");
        if (!data.fatal) return;
        const status = data.response?.code;
        if (data.type === ErrorTypes.NETWORK_ERROR && (status === 403 || status === 401)) {
          latest.current.onExpired?.(video.currentTime);
          return;
        }
        if (recoveries.current++ < MAX_RECOVERIES) {
          if (data.type === ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
          else hls.startLoad();
          return;
        }
        latest.current.onError?.(data.details);
      });
      // Start playing as soon as the master playlist is understood (if this surface is visible).
      hls.on(Events.MANIFEST_PARSED, () => {
        manifestParsed.current = true;
        if (latest.current.active) tryPlay();
      });
      hls.loadSource(src);
      hls.attachMedia(video);
      hlsRef.current = hls;
      if (import.meta.env.DEV) (window as unknown as { __hls?: Hls }).__hls = hls; // debugging aid in dev builds only
    } else {
      video.src = src;
      if (startAt > 0) video.addEventListener("loadedmetadata", () => { video.currentTime = startAt; }, { once: true });
    }
  };

  const tryPlay = () => {
    const video = videoRef.current;
    if (!video) return;
    video.play().catch((err: unknown) => {
      // Unmuted autoplay blocked: retry muted, and let the UI offer an unmute button.
      if (err instanceof DOMException && err.name === "NotAllowedError" && !video.muted) {
        video.muted = true;
        latest.current.onAutoplayMuted?.();
        video.play().catch(() => {});
      }
    });
  };

  useEffect(() => {
    attach(props.src, props.startAt ?? 0);
    return () => {
      hlsRef.current?.destroy();
      hlsRef.current = null;
    };
    // Re-attach only when the URL changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.src]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    const hls = hlsRef.current;
    if (props.active) {
      if (hls && manifestParsed.current) hls.startLoad();
      else if (hls && stoppedEarly.current) {
        stoppedEarly.current = false;
        hls.loadSource(latest.current.src);
      }
      if (!hls || manifestParsed.current) tryPlay();
    } else {
      video.pause();
      if (hls) {
        if (!manifestParsed.current) stoppedEarly.current = true;
        hls.stopLoad();
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.active, props.src]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    video.muted = props.muted;
    video.volume = props.volume;
  }, [props.muted, props.volume]);

  useEffect(() => {
    if (videoRef.current) videoRef.current.playbackRate = props.rate;
  }, [props.rate]);

  useImperativeHandle(ref, () => ({
    el: () => videoRef.current,
    toggle: () => {
      const v = videoRef.current;
      if (!v) return;
      if (v.paused) tryPlay();
      else v.pause();
    },
    play: tryPlay,
    pause: () => videoRef.current?.pause(),
    seek: (t) => {
      const v = videoRef.current;
      if (v && Number.isFinite(t)) v.currentTime = Math.max(0, t);
    },
    reload: () => {
      const v = videoRef.current;
      attach(latest.current.src, v?.currentTime ?? 0);
      tryPlay();
    },
  }));

  return (
    <video
      ref={videoRef}
      poster={props.poster ?? undefined}
      playsInline
      // Starts muted so autoplay is allowed everywhere; unmuting is remembered.
      muted={props.muted}
      preload="auto"
      controls={props.controls}
      className="absolute inset-0 w-full h-full bg-transparent"
      style={{ objectFit: props.fit }}
      onTimeUpdate={(e) => props.onTime?.(e.currentTarget.currentTime)}
      onDurationChange={(e) => props.onDuration?.(e.currentTarget.duration)}
      onProgress={(e) => {
        const b = e.currentTarget.buffered;
        if (b.length) props.onBuffered?.(b.end(b.length - 1));
      }}
      onLoadedMetadata={(e) => props.onSize?.(e.currentTarget.videoWidth, e.currentTarget.videoHeight)}
      onResize={(e) => props.onSize?.(e.currentTarget.videoWidth, e.currentTarget.videoHeight)}
      onPlaying={() => props.onPlaying?.()}
      onPause={() => props.onPause?.()}
      onWaiting={() => props.onWaiting?.()}
      onEnded={() => props.onEnded?.()}
      onError={() => {
        if (!hlsRef.current) props.onError?.(videoRef.current?.error?.message || "media error");
      }}
    />
  );
});
