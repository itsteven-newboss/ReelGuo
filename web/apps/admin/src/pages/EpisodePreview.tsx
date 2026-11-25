import { unwrap, type Schemas } from "@reelguo/api-client";
import { useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, Info, RotateCcw } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import type Hls from "hls.js";
import { api } from "../session";
import { formatDuration } from "../upload";
import { Button, Dialog } from "../ui";

type EpisodeDto = Schemas["AdminEpisodeDto"];
type PreviewDto = Schemas["EpisodePreviewDto"];

/** An episode can be previewed once its upload has finished (processed or not). */
export function canPreview(e: EpisodeDto) {
  return e.has_source && e.status !== "uploading";
}

/** Plays one episode inside the series editor, with previous / next to step through the series. */
export function EpisodePreviewDialog({ episodes, episodeId, onSelect, onClose }: {
  episodes: EpisodeDto[];
  episodeId: number | null;
  onSelect: (id: number) => void;
  onClose: () => void;
}) {
  const playable = episodes.filter(canPreview);
  const index = playable.findIndex((e) => e.id === episodeId);
  const episode = episodes.find((e) => e.id === episodeId);
  const prev = index > 0 ? playable[index - 1] : undefined;
  const next = index >= 0 ? playable[index + 1] : undefined;
  const [size, setSize] = useState<{ w: number; h: number } | null>(null);

  const preview = useQuery({
    queryKey: ["admin", "episode-preview", episodeId],
    queryFn: async () => unwrap(await api.GET("/api/admin/episodes/{id}/preview", { params: { path: { id: episodeId! } } })),
    enabled: episodeId !== null,
    // Links can be short-lived (COS) or tied to the current encode, so never reuse an old one.
    staleTime: 0,
    gcTime: 0,
    retry: false,
  });

  useEffect(() => setSize(null), [episodeId]);

  const w = size?.w || preview.data?.width || episode?.width || 0;
  const h = size?.h || preview.data?.height || episode?.height || 0;
  const landscape = w > 0 && h > 0 && w > h;
  const title = episode ? `第 ${episode.episode_number} 集${episode.title ? ` · ${episode.title}` : ""}` : "预览";

  return (
    <Dialog open={episodeId !== null} title={title} width={landscape ? 960 : 460} onClose={onClose}>
      <div className="bg-black rounded-(--r-md) overflow-hidden">
        {preview.data ? (
          <PreviewPlayer key={`${preview.data.url}#${preview.dataUpdatedAt}`} preview={preview.data} aspect={w && h ? w / h : 9 / 16}
            onSize={(vw, vh) => setSize({ w: vw, h: vh })} onRetry={() => void preview.refetch()} />
        ) : (
          <div className="flex items-center justify-center text-[13px] text-mut px-6 text-center" style={{ aspectRatio: landscape ? "16 / 9" : "9 / 16", maxHeight: "70vh", margin: "0 auto" }}>
            {preview.isError ? (preview.error instanceof Error ? preview.error.message : "无法预览") : "加载中…"}
          </div>
        )}
      </div>

      {preview.data?.note && (
        <p className="mt-3 flex items-start gap-1.5 text-[12px] text-mut bg-s2 rounded-(--r-sm) px-3 py-2">
          <Info size={14} className="shrink-0 mt-px" /> {preview.data.note}
        </p>
      )}

      <div className="mt-3 flex items-center gap-2 text-[12px] text-dim">
        <span className="px-2 py-0.5 rounded-full bg-s3 text-mut">{sourceLabel(preview.data)}</span>
        {episode && episode.duration > 0 && <span>{formatDuration(episode.duration)} · {episode.width}×{episode.height}</span>}
        <span className="ml-auto flex gap-1.5">
          <Button variant="secondary" className="!h-8 !px-3" disabled={!prev} onClick={() => prev && onSelect(prev.id)}>
            <span className="flex items-center gap-1"><ChevronLeft size={14} /> 上一集</span>
          </Button>
          <Button variant="secondary" className="!h-8 !px-3" disabled={!next} onClick={() => next && onSelect(next.id)}>
            <span className="flex items-center gap-1">下一集 <ChevronRight size={14} /></span>
          </Button>
        </span>
      </div>
    </Dialog>
  );
}

function sourceLabel(p: PreviewDto | undefined) {
  if (!p) return "—";
  if (p.source === "original") return "上传的原始文件";
  return p.kind === "hls" ? "线上版本 · HLS" : "线上版本 · MP4";
}

export function PreviewPlayer({ preview, aspect, onSize, onRetry, onEnded }: {
  preview: PreviewDto;
  aspect: number;
  onSize: (w: number, h: number) => void;
  onRetry: () => void;
  /** The story preview (试玩) moves on when a scene ends. */
  onEnded?: () => void;
}) {
  const ref = useRef<HTMLVideoElement>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const video = ref.current;
    if (!video) return undefined;
    let hls: Hls | null = null;
    let cancelled = false;
    if (preview.kind === "hls") {
      // hls.js only loads when an HLS episode is opened; Safari plays HLS natively.
      import("hls.js").then(({ default: HlsJs }) => {
        if (cancelled) return;
        if (!HlsJs.isSupported()) { video.src = preview.url; return; }
        hls = new HlsJs();
        hls.on(HlsJs.Events.ERROR, (_e, data) => {
          if (!data.fatal) return;
          setError(data.type === HlsJs.ErrorTypes.NETWORK_ERROR ? "视频加载失败，可能是链接已过期" : "视频解码失败");
          hls?.destroy();
          hls = null;
        });
        hls.loadSource(preview.url);
        hls.attachMedia(video);
      }).catch(() => setError("播放器加载失败，请刷新页面"));
    } else {
      video.src = preview.url;
    }
    return () => {
      cancelled = true;
      hls?.destroy();
      // Stops the download when the dialog closes or moves to another episode.
      video.removeAttribute("src");
      video.load();
    };
  }, [preview.kind, preview.url]);

  function onMediaError() {
    const code = ref.current?.error?.code;
    if (code === undefined) return;
    setError(code === 4
      ? preview.source === "original"
        ? "浏览器无法播放这个原始文件的格式。可以先用「文件夹上传 → 优化视频」转成 MP4 再替换。"
        : "浏览器无法播放这个视频，或链接已过期"
      : "视频加载失败");
  }

  if (error) {
    return (
      <div className="flex flex-col items-center justify-center gap-3 text-[13px] text-mut px-6 text-center" style={{ aspectRatio: String(aspect), maxHeight: "70vh", margin: "0 auto" }}>
        {error}
        <Button variant="secondary" className="!h-8" onClick={onRetry}><span className="flex items-center gap-1"><RotateCcw size={13} /> 重试</span></Button>
      </div>
    );
  }
  return (
    <video ref={ref} controls autoPlay playsInline poster={preview.poster_url ?? undefined}
      className="block w-full max-h-[70vh] object-contain bg-black" style={{ aspectRatio: String(aspect) }}
      onLoadedMetadata={(e) => onSize(e.currentTarget.videoWidth, e.currentTarget.videoHeight)}
      onEnded={onEnded} onError={onMediaError} />
  );
}
