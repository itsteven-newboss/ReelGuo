import { unwrap, type Schemas } from "@reelguo/api-client";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Film, RotateCcw, Trash2, Upload } from "lucide-react";
import { useState } from "react";
import { api } from "../session";
import { formatBytes, formatDuration, uploadFile } from "../upload";
import { Button, Card, ErrorText, StatusBadge } from "../ui";

type SeriesDto = Schemas["AdminSeriesDto"];
type EpisodeDto = Schemas["AdminEpisodeDto"];

type Pending = { name: string; size: number; progress: number; error?: string };

/** Episode list with multi-file upload. Files are numbered after the current last episode, in file-name order. */
export function EpisodesPanel({ series, onChanged }: { series: SeriesDto; onChanged: () => void }) {
  const qc = useQueryClient();
  const [pending, setPending] = useState<Pending[]>([]);
  const detailKey = ["admin", "series", "detail", series.id];
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: detailKey });
    onChanged();
  };

  async function uploadAll(files: File[]) {
    const sorted = [...files].sort((a, b) => a.name.localeCompare(b.name, "zh-CN", { numeric: true }));
    setPending(sorted.map((f) => ({ name: f.name, size: f.size, progress: 0 })));
    for (const [i, file] of sorted.entries()) {
      const update = (p: Partial<Pending>) => setPending((list) => list.map((x, j) => (j === i ? { ...x, ...p } : x)));
      try {
        const created = unwrap(await api.POST("/api/admin/series/{id}/episodes", {
          params: { path: { id: series.id } },
          body: { file_name: file.name, content_type: file.type || null, episode_number: null, title: null },
        }));
        await uploadFile(created.upload, file, (progress) => update({ progress }));
        unwrap(await api.POST("/api/admin/episodes/{id}/uploaded", { params: { path: { id: created.episode.id } } }));
        update({ progress: 1 });
        refresh();
      } catch (e) {
        update({ error: e instanceof Error ? e.message : "上传失败" });
      }
    }
    setPending((list) => list.filter((p) => p.error));
  }

  return (
    <Card className="mt-4">
      <div className="flex items-center gap-3 px-5 pt-4 pb-3">
        <div className="flex-1">
          <div className="text-[14px] font-semibold">分集</div>
          <p className="text-[11.5px] text-dim mt-0.5">支持 mp4、mov、mkv、webm 等常见格式。上传后自动转码为多清晰度 HLS，按文件名顺序编号。</p>
        </div>
        <label className="flex items-center gap-1.5 h-9 px-4 rounded-(--r-ctl) bg-primary text-on-primary text-[13px] font-bold cursor-pointer pressable">
          <Upload size={15} /> 上传分集
          <input type="file" multiple accept="video/*,.mkv,.mov" className="hidden"
            onChange={(e) => { const files = Array.from(e.target.files ?? []); if (files.length) void uploadAll(files); e.target.value = ""; }} />
        </label>
      </div>

      {pending.length > 0 && (
        <div className="px-5 pb-3 space-y-2">
          {pending.map((p) => (
            <div key={p.name} className="text-[12px]">
              <div className="flex justify-between text-mut"><span className="truncate">{p.name}</span><span>{formatBytes(p.size)} · {Math.round(p.progress * 100)}%</span></div>
              <div className="h-1.5 mt-1 rounded-full bg-s3 overflow-hidden"><div className="h-full bg-primary transition-all" style={{ width: `${p.progress * 100}%` }} /></div>
              {p.error && <p className="text-danger mt-0.5">{p.error}</p>}
            </div>
          ))}
        </div>
      )}

      <table className="w-full text-[13px]">
        <thead className="text-left text-[11.5px] text-dim bg-s2">
          <tr>
            <th className="px-5 py-2.5 font-medium w-[70px]">集数</th>
            <th className="px-3 py-2.5 font-medium w-[60px]">画面</th>
            <th className="px-3 py-2.5 font-medium">标题</th>
            <th className="px-3 py-2.5 font-medium">状态</th>
            <th className="px-3 py-2.5 font-medium">时长 / 尺寸</th>
            <th className="px-3 py-2.5 font-medium" title="勾选后即使整部剧设置了付费，这一集也免费">始终免费</th>
            <th className="px-5 py-2.5 font-medium text-right">操作</th>
          </tr>
        </thead>
        <tbody>
          {series.episodes.length === 0 && (
            <tr><td colSpan={7} className="px-5 py-8 text-center text-dim">还没有分集</td></tr>
          )}
          {series.episodes.map((e) => <EpisodeRow key={e.id} episode={e} onChanged={refresh} />)}
        </tbody>
      </table>
    </Card>
  );
}

function EpisodeRow({ episode: e, onChanged }: { episode: EpisodeDto; onChanged: () => void }) {
  const [title, setTitle] = useState(e.title);
  const path = { params: { path: { id: e.id } } };

  const update = useMutation({
    mutationFn: async (body: { title: string; is_free: boolean }) => unwrap(await api.PUT("/api/admin/episodes/{id}", { ...path, body })),
    onSuccess: onChanged,
  });
  const retry = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/admin/episodes/{id}/retry", path)),
    onSuccess: onChanged,
  });
  const remove = useMutation({
    mutationFn: async () => {
      const r = await api.DELETE("/api/admin/episodes/{id}", path);
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: onChanged,
  });
  const replace = useMutation({
    mutationFn: async (file: File) => {
      const ticket = unwrap(await api.POST("/api/admin/episodes/{id}/upload", { ...path, body: { file_name: file.name, content_type: file.type || null } }));
      await uploadFile(ticket.upload, file, () => {});
      unwrap(await api.POST("/api/admin/episodes/{id}/uploaded", path));
    },
    onSuccess: onChanged,
  });

  return (
    <tr className="border-t border-white/5 align-middle">
      <td className="px-5 py-2.5 font-semibold">第 {e.episode_number} 集</td>
      <td className="px-3 py-2.5">
        <div className="w-9 h-12 rounded bg-s3 overflow-hidden flex items-center justify-center text-dim">
          {e.poster_url ? <img src={e.poster_url} alt="" className="w-full h-full object-cover" /> : <Film size={14} />}
        </div>
      </td>
      <td className="px-3 py-2.5">
        <input value={title} onChange={(ev) => setTitle(ev.target.value)} onBlur={() => title !== e.title && update.mutate({ title, is_free: e.is_free })}
          placeholder="（无标题）" className="w-full bg-transparent outline-none focus:bg-s3 rounded px-1.5 py-1" />
      </td>
      <td className="px-3 py-2.5">
        <div className="flex items-center gap-2">
          <StatusBadge status={e.status} />
          {e.status === "processing" && <span className="text-[11.5px] text-mut">{e.progress}%</span>}
        </div>
        {e.error && <p className="text-[11px] text-danger mt-1 max-w-[260px]">{e.error}</p>}
      </td>
      <td className="px-3 py-2.5 text-mut whitespace-nowrap">{e.duration > 0 ? `${formatDuration(e.duration)} · ${e.width}×${e.height}` : "—"}</td>
      <td className="px-3 py-2.5">
        <input type="checkbox" checked={e.is_free} onChange={(ev) => update.mutate({ title, is_free: ev.target.checked })} />
      </td>
      <td className="px-5 py-2.5 text-right whitespace-nowrap">
        <label className="text-primary hover:underline cursor-pointer text-[12.5px]">
          {replace.isPending ? "上传中…" : "替换视频"}
          <input type="file" accept="video/*,.mkv,.mov" className="hidden" onChange={(ev) => { const f = ev.target.files?.[0]; if (f) replace.mutate(f); ev.target.value = ""; }} />
        </label>
        {e.status === "failed" && (
          <Button variant="ghost" className="ml-1 !h-7 !px-2" onClick={() => retry.mutate()} title="重试"><RotateCcw size={14} /></Button>
        )}
        <Button variant="ghost" className="ml-1 !h-7 !px-2 hover:!text-danger" title="删除"
          onClick={() => window.confirm(`确定删除第 ${e.episode_number} 集？视频文件也会被删除。`) && remove.mutate()}>
          <Trash2 size={14} />
        </Button>
        <ErrorText error={update.error ?? retry.error ?? remove.error ?? replace.error} />
      </td>
    </tr>
  );
}
