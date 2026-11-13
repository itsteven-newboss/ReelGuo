import {
  ChevronDown, ChevronLeft, ChevronRight, ChevronUp, Gauge, Heart, Link2, ListVideo, Lock, LockOpen, Maximize,
  MessageCircle, Minimize, Pause, PictureInPicture2, Play, RectangleHorizontal, SkipForward, Star, Volume2, VolumeX, Zap,
} from "lucide-react";
import { useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { fmtCount, fmtTime } from "../player/format";

/* ---------------------------------------------------------------------------------------------
 * Mobile overlays (UI-REFERENCE §3). Colours are fixed white-on-dark in both themes: they sit on video.
 * ------------------------------------------------------------------------------------------- */

/** Right action rail, as on the reference's phone player: the author, then 点赞 · 收藏 · 评论 · 分享 in round buttons
 * with counts, and the playback speed. */
export function MobileRail({ bottom, author, liked, likeCount, onLike, fav, favCount, onFav, comments, onComments, onShare, rate, onRate }: {
  bottom: string; author?: string;
  liked: boolean; likeCount: number; onLike: () => void;
  fav: boolean; favCount: number; onFav: () => void;
  comments: number; onComments: () => void; onShare: () => void;
  rate: number; onRate: () => void;
}) {
  return (
    <div className="absolute right-2 z-20 w-[68px] flex flex-col items-center gap-3.5" style={{ bottom }}>
      {author && <span className="max-w-full text-[12.5px] ov-ink-4 ov-shadow truncate">{author}</span>}
      <RailButton label="点赞" count={likeCount} pressed={liked} onClick={onLike}
        icon={<Heart size={19} className={liked ? "text-[#ff4d6d] fill-current" : "ov-ink fill-current"} />} />
      <RailButton label="收藏" count={favCount} pressed={fav} onClick={onFav}
        icon={<Star size={19} className={fav ? "text-[#f3c969] fill-current" : "ov-ink fill-current"} />} />
      <RailButton label="评论" count={comments} onClick={onComments} icon={<MessageCircle size={19} className="ov-ink fill-current" />} />
      <RailButton label="分享" onClick={onShare} icon={<Link2 size={19} className="ov-ink" />} />
      <button onClick={onRate} aria-label={`倍速 ${rate} 倍`} className="h-8 px-1 text-[15px] font-bold ov-ink ov-shadow tabular-nums pressable">{rate}X</button>
    </div>
  );
}

function RailButton({ label, icon, count, onClick, pressed }: { label: string; icon: ReactNode; count?: number; onClick: () => void; pressed?: boolean }) {
  return (
    <button onClick={onClick} aria-label={label} aria-pressed={pressed} className="flex flex-col items-center gap-1 pressable">
      <span className="w-9 h-9 rounded-full flex items-center justify-center backdrop-blur-sm" style={{ background: "rgba(0,0,0,.38)" }}>{icon}</span>
      <span className="text-[11.5px] ov-ink ov-shadow tabular-nums">{count !== undefined ? fmtCount(count) : label}</span>
    </button>
  );
}

/** Top-right: mute, and 反馈 (opens the report sheet), as on the reference's phone player. */
export function MobileTopChips({ muted, onMute, onReport }: { muted: boolean; onMute: () => void; onReport: () => void }) {
  return (
    <div className="absolute right-3 z-40 flex items-center gap-2" style={{ top: "calc(var(--sat) + 6px)" }}>
      <Chip label={muted ? "取消静音" : "静音"} onClick={onMute}>{muted ? <VolumeX size={17} /> : <Volume2 size={17} />}</Chip>
      <button onClick={onReport} className="ov-chip h-9 px-3.5 rounded-full text-[13px] font-semibold pressable elev-1 backdrop-blur-md">反馈</button>
    </div>
  );
}

export function Chip({ label, onClick, children, strong = false }: { label: string; onClick: () => void; children: ReactNode; strong?: boolean }) {
  return (
    <button onClick={onClick} aria-label={label} title={label}
      className={`${strong ? "ov-chip-strong" : "ov-chip"} w-9 h-9 rounded-full flex items-center justify-center pressable elev-1 backdrop-blur-md`}>
      {children}
    </button>
  );
}

/** Context header for feeds opened from a list: back · title · "3 / 20". */
export function ContextHeader({ title, index, total, onBack }: { title: string; index: number; total: number; onBack: () => void }) {
  return (
    <div className="absolute inset-x-0 z-20 flex items-center gap-2 px-2.5" style={{ top: "calc(var(--sat) + 8px)", paddingRight: 148 }}>
      <Chip label="返回" onClick={onBack}><ChevronLeft size={20} /></Chip>
      <span className="text-[14.5px] font-semibold ov-ink ov-shadow truncate">{title}</span>
      {total > 1 && <span className="ml-auto shrink-0 whitespace-nowrap tabular-nums px-2 py-0.5 rounded-full ov-chip text-[11px] font-semibold">{index + 1} / {total}</span>}
    </div>
  );
}

/** Seek line with a large invisible hit area; drags preview the time and seek on release. */
export function SeekLine({ progress, duration, height, onSeek, thick = false }: {
  progress: number; duration: number; height: number; onSeek: (t: number) => void; thick?: boolean;
}) {
  const [dragPct, setDragPct] = useState<number | null>(null);
  const dragging = useRef(false);

  const pctAt = (e: ReactPointerEvent<HTMLDivElement>) => {
    const r = e.currentTarget.getBoundingClientRect();
    return r.width ? Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)) : 0;
  };

  const shown = dragPct ?? progress;
  return (
    <div className="relative" style={{ height, touchAction: "none" }}
      onPointerDown={(e) => { e.stopPropagation(); dragging.current = true; e.currentTarget.setPointerCapture(e.pointerId); setDragPct(pctAt(e)); }}
      onPointerMove={(e) => { if (dragging.current) { e.stopPropagation(); setDragPct(pctAt(e)); } }}
      onPointerUp={(e) => {
        if (!dragging.current) return;
        e.stopPropagation();
        dragging.current = false;
        const pct = pctAt(e);
        setDragPct(null);
        if (duration > 0) onSeek(Math.min(duration - 0.15, pct * duration));
      }}
      onPointerCancel={() => { dragging.current = false; setDragPct(null); }}>
      <div className={`absolute inset-x-0 top-1/2 -translate-y-1/2 rounded-full transition-all ${dragPct !== null ? "h-[5px]" : thick ? "h-[3px]" : "h-[2.5px]"}`}
        style={{ background: "rgba(255,255,255,.3)" }}>
        <div className="h-full rounded-full bg-white" style={{ width: `${shown * 100}%`, transition: dragPct !== null ? "none" : "width .25s linear" }} />
      </div>
      {dragPct !== null && (
        <div className="absolute left-1/2 -translate-x-1/2 -top-8 px-2.5 py-1 rounded-md ov-chip-strong backdrop-blur-md text-[12px] font-bold tabular-nums whitespace-nowrap">
          {fmtTime(dragPct * duration)} / {fmtTime(duration)}
        </div>
      )}
    </div>
  );
}

/** Bottom of the phone player, as on the reference's mobile series page: title, one line of synopsis, the seek line,
 * then "选集 · 全集 N 集 · 第 N 集", which opens the episode list. */
export function MobileInfo({ bottom, title, description, total, episode, onDetail, onFullscreen, seek }: {
  bottom: string; title: string; description?: string; total?: number; episode: number; onDetail: () => void; onFullscreen?: () => void; seek?: ReactNode;
}) {
  return (
    <div className="absolute inset-x-0 z-20" style={{ bottom }}>
      <div className="absolute inset-x-0 bottom-0 h-[220px] pointer-events-none"
        style={{ background: "linear-gradient(0deg, rgba(0,0,0,.88) 0%, rgba(0,0,0,.45) 50%, transparent 100%)" }} />
      <p className="relative px-4 text-[18px] font-bold leading-snug ov-ink ov-shadow clamp-1">{title}</p>
      {description && <p className="relative px-4 mt-1 text-[13px] ov-ink-4 ov-shadow clamp-1">{description}</p>}
      {seek && <div className="relative mx-4 mt-3">{seek}</div>}
      <div className="relative flex items-center" style={{ background: "rgba(10,10,10,.6)" }}>
        <button onClick={onDetail} className="flex-1 min-w-0 h-11 px-4 flex items-center text-[13.5px] font-medium ov-ink pressable">
          选集 · {total ? `全集 ${total} 集 · ` : ""}第 {episode} 集<ChevronRight size={13} className="ml-0.5 opacity-70" />
        </button>
        {onFullscreen && (
          <button onClick={onFullscreen} aria-label="全屏" className="w-12 h-11 flex items-center justify-center ov-ink pressable"><Maximize size={18} /></button>
        )}
      </div>
    </div>
  );
}

/** Scrub overlay: big time, total, and a 62%-wide bar. */
export function ScrubOverlay({ target, fraction, duration }: { target: number; fraction: number; duration: number }) {
  return (
    <div className="absolute inset-0 z-30 flex flex-col items-center justify-center pointer-events-none" style={{ background: "rgba(0,0,0,.25)" }}>
      <span className="text-[26px] font-bold ov-ink tabular-nums ov-shadow">{fmtTime(target)}</span>
      <span className="text-[11px] ov-ink-4 mt-1 tabular-nums">/ {fmtTime(duration)}</span>
      <div className="mt-4 w-[62%] h-[3px] rounded-full overflow-hidden" style={{ background: "rgba(255,255,255,.25)" }}>
        <div className="h-full bg-white" style={{ width: `${fraction * 100}%` }} />
      </div>
    </div>
  );
}

export function PauseIndicator() {
  return (
    <div className="absolute inset-0 z-10 flex items-center justify-center pointer-events-none">
      <span className="w-[66px] h-[66px] rounded-full flex items-center justify-center backdrop-blur-md"
        style={{ background: "rgba(0,0,0,.55)", boxShadow: "inset 0 0 0 1px rgba(255,255,255,.12)" }}>
        <Play size={28} className="ov-ink fill-current ml-0.5" />
      </span>
    </div>
  );
}

export function ErrorCard({ detail, onReload }: { detail: string; onReload: () => void }) {
  return (
    <div className="absolute inset-0 z-30 flex items-center justify-center">
      <div className="rounded-xl p-5 flex flex-col items-center gap-3 text-[13px] ov-ink-2" style={{ background: "rgba(0,0,0,.74)" }}>
        <span>视频暂时无法加载</span>
        <span className="text-[10.5px] ov-ink-4 max-w-[240px] text-center break-all">{detail}</span>
        <button className="px-4 py-1.5 rounded-full ov-chip-strong text-[#8fd0ff] text-[12.5px] font-semibold pressable" onClick={onReload}>重新加载</button>
      </div>
    </div>
  );
}

/** "2x 倍速中 · 松开恢复" / "2x 已锁定 · 点按解除" chip. */
export function SpeedChip({ rate, locked, holding, top, onClick }: { rate: number; locked: boolean; holding: boolean; top: string; onClick: () => void }) {
  return (
    <button onClick={onClick}
      className={`absolute left-1/2 -translate-x-1/2 z-30 flex items-center gap-1.5 px-3.5 h-8 rounded-full backdrop-blur-md text-[12px] font-bold elev-2 pressable ${locked ? "bg-primary text-on-primary" : "ov-chip text-[#f3c969]"}`}
      style={{ top }}>
      <Zap size={13} className="fill-current" />
      {rate}x {locked ? "已锁定 · 点按解除" : holding ? "倍速中 · 松开恢复" : "倍速"}
    </button>
  );
}

export function LockHint({ armed }: { armed: boolean }) {
  return (
    <div className={`absolute left-1/2 -translate-x-1/2 z-30 flex items-center gap-2 px-4 h-10 rounded-xl backdrop-blur-md elev-2 transition-all duration-200 ${armed ? "bg-primary text-on-primary scale-110" : "ov-chip-strong ov-ink-2"}`}
      style={{ top: "38%" }}>
      <Lock size={16} />
      <span className="text-[12.5px] font-bold">{armed ? "松开锁定 2.0x" : "上滑到此锁定倍速"}</span>
    </div>
  );
}

/** Controls lock: blocks every touch; a faint unlock button shows after a tap. */
export function ControlsLock({ onUnlock }: { onUnlock: () => void }) {
  const [visible, setVisible] = useState(true);
  const timer = useRef<number | undefined>(undefined);
  const show = () => {
    setVisible(true);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setVisible(false), 2000);
  };
  return (
    <div className="absolute inset-0 z-50" onPointerDown={show}>
      <button onClick={onUnlock} aria-label="解锁播放控制"
        className={`absolute left-4 top-1/2 -translate-y-1/2 w-11 h-11 rounded-full flex items-center justify-center transition-opacity duration-300 ${visible ? "opacity-100" : "opacity-0"}`}
        style={{ color: "rgba(255,255,255,.75)", background: "rgba(0,0,0,.35)" }}>
        <LockOpen size={19} />
      </button>
    </div>
  );
}

/** Immersive fullscreen controls (mobile). */
export function FullscreenControls({ paused, time, duration, rate, rotated, onToggle, onSeek, onRate, onLock, onExit }: {
  paused: boolean; time: number; duration: number; rate: number; rotated: boolean;
  onToggle: () => void; onSeek: (t: number) => void; onRate: () => void; onLock: () => void; onExit: () => void;
}) {
  const edge = rotated ? "14px" : "calc(var(--sat) + 8px)";
  const base = rotated ? 10 : 0;
  return (
    <>
      <div className="absolute right-4 z-40 flex items-center gap-2" style={{ top: edge }}>
        <Chip label="锁定播放控制" onClick={onLock} strong><Lock size={16} /></Chip>
        <Chip label="退出全屏" onClick={onExit} strong><Minimize size={18} /></Chip>
      </div>
      <div className="absolute inset-x-0 z-30 px-4" style={{ bottom: rotated ? 46 : `calc(var(--sab) + ${52 + base}px)` }}>
        <SeekLine progress={duration ? time / duration : 0} duration={duration} height={26} onSeek={onSeek} thick />
      </div>
      <div className="absolute inset-x-0 z-40 flex items-center gap-3 px-4" style={{ bottom: rotated ? 10 : `calc(var(--sab) + 10px)` }}>
        <Chip label={paused ? "播放" : "暂停"} onClick={onToggle}>{paused ? <Play size={18} className="fill-current" /> : <Pause size={17} className="fill-current" />}</Chip>
        <span className="text-[11.5px] font-semibold ov-ink-2 tabular-nums">{fmtTime(time)}</span>
        <button onClick={onRate}
          className={`ml-auto flex items-center gap-1 px-2.5 h-8 rounded-full text-[12px] font-bold pressable ${rate !== 1 ? "bg-primary text-on-primary" : "ov-chip ov-ink-2"}`}>
          <Zap size={12} className="fill-current" />{rate}x
        </button>
        <span className="text-[11.5px] ov-ink-4 tabular-nums">{fmtTime(duration)}</span>
      </div>
    </>
  );
}

/* ---------------------------------------------------------------------------------------------
 * Desktop overlays (UI-REFERENCE §4)
 * ------------------------------------------------------------------------------------------- */

export function DesktopContextHeader({ title, index, total, onBack }: { title: string; index: number; total: number; onBack: () => void }) {
  return (
    <div className="absolute top-[18px] left-5 right-5 z-20 flex items-center gap-3 text-[12.5px] text-[#e8ecf2]">
      <button onClick={onBack} aria-label="返回" className="w-8 h-8 grid place-items-center rounded-full bg-white/[.06] hover:bg-white/[.14] pressable">
        <ChevronLeft size={17} />
      </button>
      <span className="truncate max-w-[70%] font-semibold ov-shadow">{title}</span>
      <small className="ml-auto text-[11px] text-[#9aa0aa] tabular-nums">{index + 1} / {total}</small>
    </div>
  );
}

/** Right column: previous/next with counter, then like · comments · favourite · episodes · share. */
export function DesktopRail({ index, total, onPrev, onNext, liked, likeCount, onLike, comments, onComments, fav, favCount, onFav, onEpisodes, onShare }: {
  index: number; total: number; onPrev: () => void; onNext: () => void;
  liked: boolean; likeCount: number; onLike: () => void; comments: number; onComments: () => void;
  fav: boolean; favCount: number; onFav: () => void; onEpisodes: () => void; onShare: () => void;
}) {
  const item = "flex flex-col items-center gap-1 w-[46px] text-[#e3e6ec] hover:text-white hover:-translate-y-0.5 transition pressable";
  const count = "text-[10.5px] font-semibold text-white/90 tabular-nums";
  const nav = "w-10 h-10 grid place-items-center rounded-full bg-white/[.06] text-[#cdd3dd] hover:bg-white/[.15] disabled:text-[#5e6570] disabled:bg-white/[.02]";
  return (
    <div className="desktop-fade absolute right-[22px] bottom-[92px] z-20 flex flex-col items-center gap-4">
      <div className="flex flex-col gap-[9px] items-center pb-3.5 mb-1 border-b border-white/10">
        <button className={nav} onClick={onPrev} disabled={index === 0} aria-label="上一个" title="上一个 ↑"><ChevronUp size={19} /></button>
        <button className={nav} onClick={onNext} disabled={index >= total - 1} aria-label="下一个" title="下一个 ↓"><ChevronDown size={19} /></button>
        <small className="text-[9.5px] text-[#8b929d] tabular-nums tracking-wide">{String(index + 1).padStart(2, "0")} / {String(total).padStart(2, "0")}</small>
      </div>
      <button className={item} onClick={onLike} aria-pressed={liked} title="喜欢 L">
        <Heart size={30} className={liked ? "text-like fill-current" : "ov-shadow"} /><span className={count}>{fmtCount(likeCount)}</span>
      </button>
      <button className={item} onClick={onComments} title="评论 C">
        <MessageCircle size={27} className="fill-current ov-shadow" /><span className={count}>{fmtCount(comments)}</span>
      </button>
      <button className={item} onClick={onFav} aria-pressed={fav} title="收藏 S">
        <Star size={27} className={fav ? "text-[#f3c969] fill-current" : "ov-shadow"} /><span className={count}>{fmtCount(favCount)}</span>
      </button>
      <button className={item} onClick={onEpisodes} title="选集 E">
        <ListVideo size={26} className="ov-shadow" /><span className={count}>选集</span>
      </button>
      <button className={item} onClick={onShare} title="分享">
        <Link2 size={25} className="ov-shadow" /><span className={count}>分享</span>
      </button>
    </div>
  );
}

export function DesktopMeta({ author, episode, total, score, title, description }: {
  author: string; episode: number; total: number; score: number; title: string; description: string;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div className="desktop-fade absolute left-[26px] right-[108px] bottom-[84px] z-10 max-w-[620px]">
      <div className="flex items-center gap-2">
        <span className="text-[15px] font-bold text-white ov-shadow truncate">@{author}</span>
        {total > 0 && <span className="shrink-0 px-1.5 py-px rounded-md text-[10px] font-bold text-white bg-white/15">第 {episode}/{total} 集</span>}
        {score > 0 && <span className="shrink-0 text-[11px] font-bold text-[#f3c969] ov-shadow">{score.toFixed(1)} 分</span>}
      </div>
      <p className="mt-1 text-[13.5px] leading-snug ov-ink-2 ov-shadow clamp-2">{title}</p>
      <button onClick={() => setOpen((v) => !v)} className="mt-1 text-left w-full">
        <p className={`text-[11.5px] leading-relaxed ov-ink-4 ${open ? "" : "clamp-1"}`}>{description || "暂无简介"}</p>
        <span className="text-[10.5px] text-[#8fd0ff] font-semibold">{open ? "收起" : "展开简介"}</span>
      </button>
    </div>
  );
}

const SPEEDS = [0.75, 1, 1.25, 1.5, 2];

/** Desktop controls bar: seek, play, time, next, speed, volume, picture-in-picture, theatre, fullscreen. */
export function DesktopControls({ paused, time, duration, buffered, rate, muted, volume, theater, fullscreen, nextTitle, nextDisabled,
  onToggle, onSeek, onNext, onRate, onMute, onVolume, onPip, onLock, onTheater, onFullscreen }: {
  paused: boolean; time: number; duration: number; buffered: number; rate: number; muted: boolean; volume: number;
  theater: boolean; fullscreen: boolean; nextTitle: string; nextDisabled: boolean;
  onToggle: () => void; onSeek: (t: number) => void; onNext: () => void; onRate: (r: number) => void; onMute: () => void;
  onVolume: (v: number) => void; onPip: () => void; onLock?: () => void; onTheater?: () => void; onFullscreen: () => void;
}) {
  const [speedOpen, setSpeedOpen] = useState(false);
  const [preview, setPreview] = useState<number | null>(null);
  const control = "h-8 min-w-8 px-2 rounded-[7px] inline-flex items-center justify-center text-[#c8ced8] hover:bg-white/10 hover:text-white pressable disabled:text-[#5e6570]";
  const played = duration ? ((preview ?? time) / duration) * 100 : 0;

  return (
    <div className="desktop-fade absolute inset-x-0 bottom-0 z-25 px-[22px] pb-2.5 text-[#bcc3cd]"
      style={{ background: "linear-gradient(0deg, rgba(6,9,13,.82), rgba(6,9,13,.28) 62%, transparent)" }}>
      <div className="group relative h-[19px] flex items-center cursor-pointer">
        <div className="pointer-events-none absolute inset-x-0 h-[2px] group-hover:h-1 rounded-sm overflow-hidden bg-white/15 transition-all">
          <div className="absolute h-full bg-white/10" style={{ width: `${duration ? (buffered / duration) * 100 : 0}%` }} />
          <div className="absolute h-full bg-primary rounded-sm" style={{ width: `${played}%` }} />
        </div>
        <input type="range" min={0} max={duration || 0} step={0.1} value={preview ?? time} aria-label="播放进度"
          className="absolute inset-0 w-full opacity-0 cursor-pointer"
          onChange={(e) => setPreview(Number(e.target.value))}
          onPointerUp={() => { if (preview !== null) { onSeek(preview); setPreview(null); } }}
          onKeyUp={() => { if (preview !== null) { onSeek(preview); setPreview(null); } }} />
      </div>
      <div className="flex items-center gap-[5px] h-[38px]">
        <button className={`${control} text-white`} onClick={onToggle} aria-label={paused ? "播放 (空格)" : "暂停 (空格)"}>
          {paused ? <Play size={18} className="fill-current" /> : <Pause size={18} className="fill-current" />}
        </button>
        <span className="inline-flex gap-1 items-baseline text-[11.5px] tabular-nums text-[#d3dae4] ml-1.5">
          {fmtTime(preview ?? time)}<span className="text-[#6b7480]">/</span>{fmtTime(duration)}
        </span>
        <button className={control} onClick={onNext} disabled={nextDisabled} title={nextTitle} aria-label={nextTitle}><SkipForward size={17} /></button>
        <span className="flex-1" />
        <div className="relative">
          <button className={`${control} text-[11.5px] font-semibold tabular-nums`} onClick={() => setSpeedOpen((v) => !v)} aria-label="倍速">
            {rate === 1 ? <Gauge size={17} /> : `${rate}x`}
          </button>
          {speedOpen && (
            <div className="absolute bottom-[calc(100%+10px)] left-1/2 -translate-x-1/2 w-[132px] p-1.5 rounded-[10px] bg-[#252a31]/[.98] shadow-[0_12px_42px_rgba(0,0,0,.5)] flex flex-col gap-px a-scale-in">
              <div className="text-[10px] text-[#78828f] px-2 pt-1.5 pb-2">播放速度</div>
              {SPEEDS.map((s) => (
                <button key={s} role="menuitemradio" aria-checked={s === rate} onClick={() => { onRate(s); setSpeedOpen(false); }}
                  className={`flex items-center justify-between h-[30px] px-2 rounded-md text-[12px] hover:bg-white/[.07] ${s === rate ? "text-primary" : "text-[#c9d1dc]"}`}>
                  {s === 1 ? "正常" : `${s}x`}
                </button>
              ))}
            </div>
          )}
        </div>
        <div className="group flex items-center">
          <button className={control} onClick={onMute} aria-label={muted ? "取消静音 (M)" : "静音 (M)"}>{muted || volume === 0 ? <VolumeX size={17} /> : <Volume2 size={17} />}</button>
          <div className="w-0 group-hover:w-[74px] group-focus-within:w-[74px] overflow-hidden transition-[width] duration-200 flex items-center">
            <input type="range" min={0} max={1} step={0.05} value={muted ? 0 : volume} aria-label="音量" className="w-[68px] mx-[3px] accent-[var(--p-primary)]"
              onChange={(e) => onVolume(Number(e.target.value))} />
          </div>
        </div>
        <button className={control} onClick={onPip} aria-label="画中画"><PictureInPicture2 size={17} /></button>
        {onLock && <button className={control} onClick={onLock} aria-label="锁定播放控制"><Lock size={16} /></button>}
        {onTheater && <button className={`${control} ${theater ? "text-primary" : ""}`} onClick={onTheater} aria-label="影院模式 (T)"><RectangleHorizontal size={17} /></button>}
        <button className={control} onClick={onFullscreen} aria-label="全屏 (F)">{fullscreen ? <Minimize size={17} /> : <Maximize size={17} />}</button>
      </div>
    </div>
  );
}
