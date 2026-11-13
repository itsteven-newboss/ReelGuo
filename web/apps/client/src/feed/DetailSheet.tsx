import { Img } from "@reelguo/ui";
import { Flag, Lock, UserRound } from "lucide-react";
import { useState } from "react";
import type { SeriesDetail } from "../api";
import { fmtCount } from "../player/format";
import { useReport } from "../social/Report";
import { toggleFollow, useAuth } from "../state/auth";
import { CloseButton } from "../ui/Overlays";

export type Panel = "detail" | "episodes" | "comments";

/** Whether an episode still needs unlocking for this viewer. */
export type LockedFn = (episode: SeriesDetail["episodes"][number]) => boolean;

/** 4-column episode grid with newest/oldest toggle (UI-REFERENCE §5). Paid episodes not yet unlocked show a lock. */
export function EpisodeGrid({ series, current, onPick, locked }: { series: SeriesDetail; current: number; onPick: (ep: number) => void; locked?: LockedFn }) {
  const [asc, setAsc] = useState(true);
  const list = asc ? series.episodes : [...series.episodes].reverse();
  const anyLocked = !!locked && series.episodes.some(locked);
  return (
    <div className="flex flex-col h-full min-h-0">
      <div className="shrink-0 flex items-center gap-2 px-4 pb-2">
        <span className="text-[10.5px] text-mut">共 {series.episodes.length} 集{series.total_episodes > series.episodes.length && ` · 更新至第 ${series.episodes.at(-1)?.ep_num ?? 0} 集`}</span>
        {anyLocked && <span className="text-[10.5px] text-dim flex items-center gap-0.5"><Lock size={9} />需解锁</span>}
        <button onClick={() => setAsc((v) => !v)} className="ml-auto text-[11px] text-primary pressable">{asc ? "倒序" : "正序"}</button>
      </div>
      <div className="flex-1 scroll-y px-4 pb-4">
        <div className="grid grid-cols-4 gap-2">
          {list.map((e) => {
            const isLocked = locked?.(e) ?? false;
            return (
              <button key={e.ep_num} onClick={() => onPick(e.ep_num)} aria-label={isLocked ? `第 ${e.ep_num} 集（需解锁）` : `第 ${e.ep_num} 集`}
                className={`relative h-10 rounded-(--r-sm) text-[12px] font-semibold pressable ${e.ep_num === current ? "bg-primary text-on-primary" : "bg-s3 text-on/80"}`}>
                {e.ep_num}
                {isLocked && <Lock size={9} className={`absolute top-1 right-1 ${e.ep_num === current ? "text-on-primary/80" : "text-mut"}`} />}
              </button>
            );
          })}
        </div>
        {series.episodes.length === 0 && <div className="text-center text-[11.5px] text-dim py-8">暂无剧集</div>}
      </div>
    </div>
  );
}

/** Mobile sheet with the 详情 / 选集 segmented control. */
export function DetailPanel({ series, panel, current, onPanel, onPick, onClose, locked }: {
  series: SeriesDetail; panel: Panel; current: number; onPanel: (p: Panel) => void; onPick: (ep: number) => void; onClose: () => void; locked?: LockedFn;
}) {
  const following = useAuth((s) => !!series.author && s.follows.includes(series.author.id));
  const openReport = useReport((s) => s.open);
  return (
    <div className="flex flex-col flex-1 min-h-0">
      <div className="shrink-0 flex items-center gap-2 px-4 pb-2">
        <div className="flex flex-1 bg-s3 rounded-(--r-ctl) p-1">
          {([["detail", "详情"], ["episodes", "选集"]] as const).map(([k, label]) => (
            <button key={k} onClick={() => onPanel(k)}
              className={`flex-1 h-8 rounded-(--r-ctl) text-[12px] font-semibold pressable ${panel === k ? "bg-sec text-primary" : "text-mut"}`}>{label}</button>
          ))}
        </div>
        <CloseButton onClick={onClose} />
      </div>

      <div className="flex-1 min-h-0">
        {panel === "episodes" ? (
          <EpisodeGrid series={series} current={current} onPick={onPick} locked={locked} />
        ) : (
          <div className="h-full scroll-y px-4 pb-5">
            <div className="flex gap-3">
              <div className="relative w-[84px] shrink-0 rounded-(--r-sm) overflow-hidden bg-s3" style={{ aspectRatio: "3 / 4" }}>
                <Img src={series.cover} />
              </div>
              <div className="flex-1 min-w-0">
                <div className="text-[13.5px] font-bold text-on leading-snug clamp-2">{series.title}</div>
                <div className="flex items-center gap-2 mt-1.5">
                  {series.score > 0 && <span className="text-[12px] font-bold text-star">{series.score.toFixed(1)} 分</span>}
                  {series.episodes.length > 0 && <span className="text-[10.5px] text-mut">共 {series.episodes.length} 集</span>}
                </div>
                <div className="flex gap-3 mt-2 text-[10.5px] text-mut">
                  <span>点赞 {fmtCount(series.counts.like)}</span>
                  <span>收藏 {fmtCount(series.counts.collect)}</span>
                  <span>评论 {fmtCount(series.counts.comment)}</span>
                </div>
              </div>
            </div>

            {series.author && (
              <div className="mt-4 flex items-center gap-2.5 rounded-(--r-md) bg-s2 p-2.5">
                <span className="w-[34px] h-[34px] rounded-full bg-s4 flex items-center justify-center text-mut shrink-0"><UserRound size={17} /></span>
                <div className="flex-1 min-w-0">
                  <div className="text-[12.5px] font-semibold text-on truncate">{series.author.name}</div>
                  <div className="text-[10px] text-mut">作者</div>
                </div>
                <button onClick={() => void toggleFollow(series.author!.id, series.author!.name)} aria-pressed={following}
                  className={`h-7 px-3 rounded-full text-[11px] font-semibold pressable ${following ? "bg-s4 text-mut" : "bg-primary text-on-primary"}`}>
                  {following ? "已关注" : "+ 关注"}
                </button>
              </div>
            )}

            <div className="mt-4">
              <div className="text-[11px] font-semibold text-dim mb-1.5">简介</div>
              <p className="text-[11.5px] leading-relaxed text-mut whitespace-pre-wrap">{series.description || "暂无简介"}</p>
            </div>
            {series.tags.length > 0 && (
              <div className="mt-3 flex flex-wrap gap-1.5">
                {series.tags.map((t) => <span key={t} className="px-2 h-6 inline-flex items-center rounded-full bg-s3 text-[10.5px] text-mut">{t}</span>)}
              </div>
            )}
            <button onClick={() => openReport({ type: "series", id: series.id, label: `剧集：${series.title}` })}
              className="mt-5 flex items-center gap-1 text-[10.5px] text-dim hover:text-mut pressable">
              <Flag size={11} />举报该剧
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
