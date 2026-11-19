import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { api } from "../session";
import { Button, Card, CARD_BADGES, CardBadgePills, ErrorText, PageHeader, StatusBadge, formatTime, inputClass, type CardBadge } from "../ui";
import { DeleteSeriesDialog } from "./DeleteSeriesDialog";

const STATUSES = [["", "全部状态"], ["draft", "草稿"], ["published", "已发布"], ["hidden", "已下架"]] as const;

/** 设置角标 for the selected series: 原创 on / off, or the status badge. */
const LABEL_ACTIONS: { key: string; label: string; body: { is_original?: boolean; card_badge?: CardBadge } }[] = [
  { key: "orig-on", label: "设为原创", body: { is_original: true } },
  { key: "orig-off", label: "取消原创", body: { is_original: false } },
  ...CARD_BADGES.map((b) => ({ key: `badge-${b.value}`, label: `状态角标：${b.label}`, body: { card_badge: b.value } })),
];

export function SeriesListPage({ onOpen }: { onOpen: (id: number | "new") => void }) {
  const [q, setQ] = useState("");
  const [status, setStatus] = useState<(typeof STATUSES)[number][0]>("");
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [deleting, setDeleting] = useState<number[] | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const qc = useQueryClient();
  const setLabels = useMutation({
    mutationFn: async (action: (typeof LABEL_ACTIONS)[number]) => {
      const r = unwrap(await api.POST("/api/admin/series/labels", {
        body: { ids: [...selected], is_original: action.body.is_original ?? null, card_badge: action.body.card_badge ?? null },
      }));
      return { ...r, label: action.label };
    },
    onSuccess: (r) => {
      setNotice(`已为 ${r.updated} 部剧「${r.label}」。`);
      void qc.invalidateQueries({ queryKey: ["admin", "series"] });
    },
  });
  // Selection is per page: changing the filter or page starts over.
  useEffect(() => setSelected(new Set()), [q, status, page]);

  const list = useQuery({
    queryKey: ["admin", "series", q, status, page],
    queryFn: async () => unwrap(await api.GET("/api/admin/series", {
      params: { query: { q: q || undefined, status: status || undefined, page, size: 20 } },
    })),
    placeholderData: keepPreviousData,
    // Keep processing counts fresh while encodes run.
    refetchInterval: (query) => (query.state.data?.items.some((s) => s.processing > 0) ? 4000 : false),
  });

  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.size)) : 1;
  const items = list.data?.items ?? [];
  const allSelected = items.length > 0 && items.every((s) => selected.has(s.id));
  const toggle = (id: number) => setSelected((cur) => {
    const next = new Set(cur);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });

  return (
    <>
      <PageHeader
        title="内容管理"
        sub="剧集至少有一集处理完成，才能发布。"
        actions={<Button variant="primary" onClick={() => onOpen("new")}><Plus size={15} className="inline -mt-0.5 mr-1" />新建剧集</Button>}
      />

      <div className="flex items-center gap-2 mb-4">
        <input value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} placeholder="搜索剧名或外部编号" className={`${inputClass} w-[280px]`} />
        <select value={status} onChange={(e) => { setStatus(e.target.value as typeof status); setPage(1); }} className={inputClass}>
          {STATUSES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        {selected.size > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <span className="text-[12.5px] text-mut">已选 {selected.size} 部</span>
            <Button variant="ghost" onClick={() => setSelected(new Set())}>取消选择</Button>
            <select value="" disabled={setLabels.isPending} className={`${inputClass} w-[150px]`} aria-label="设置角标"
              onChange={(e) => { const a = LABEL_ACTIONS.find((x) => x.key === e.target.value); if (a) { setNotice(null); setLabels.mutate(a); } }}>
              <option value="">{setLabels.isPending ? "设置中…" : "设置角标…"}</option>
              {LABEL_ACTIONS.map((a) => <option key={a.key} value={a.key}>{a.label}</option>)}
            </select>
            <Button variant="danger" onClick={() => { setNotice(null); setDeleting([...selected]); }}>
              <Trash2 size={14} className="inline -mt-0.5 mr-1" />删除
            </Button>
          </div>
        )}
      </div>
      {notice && <p className="mb-3 text-[12.5px] text-mut">{notice}</p>}
      <ErrorText error={setLabels.error} />

      <Card className="overflow-hidden">
        {list.isPending ? (
          <div className="flex justify-center py-10"><Spinner /></div>
        ) : list.isError ? (
          <p className="p-6 text-[13px] text-danger">加载失败</p>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="pl-4 py-2.5 w-[36px]">
                  <input type="checkbox" aria-label="全选本页" checked={allSelected} disabled={items.length === 0}
                    onChange={() => setSelected(allSelected ? new Set() : new Set(items.map((s) => s.id)))} />
                </th>
                <th className="px-4 py-2.5 font-medium w-[64px]">封面</th>
                <th className="px-4 py-2.5 font-medium">剧名</th>
                <th className="px-4 py-2.5 font-medium">频道</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">卡片角标</th>
                <th className="px-4 py-2.5 font-medium">分集</th>
                <th className="px-4 py-2.5 font-medium">更新时间</th>
              </tr>
            </thead>
            <tbody>
              {list.data.items.length === 0 && (
                <tr><td colSpan={8} className="px-4 py-10 text-center text-dim">还没有剧集。点「新建剧集」或使用「批量导入」。</td></tr>
              )}
              {list.data.items.map((s) => (
                <tr key={s.id} onClick={() => onOpen(s.id)}
                  className={`border-t border-white/5 cursor-pointer hover:bg-(--p-hover) ${selected.has(s.id) ? "bg-(--p-hover)" : ""}`}>
                  {/* The whole cell toggles the checkbox, so a near miss does not open the editor. */}
                  <td className="pl-4 py-2.5 cursor-default" onClick={(e) => { e.stopPropagation(); toggle(s.id); }}>
                    <input type="checkbox" aria-label={`选择《${s.title}》`} checked={selected.has(s.id)}
                      onClick={(e) => e.stopPropagation()} onChange={() => toggle(s.id)} />
                  </td>
                  <td className="px-4 py-2.5">
                    <div className="w-10 h-[53px] rounded-md bg-s3 overflow-hidden">
                      {s.cover_url && <img src={s.cover_url} alt="" className="w-full h-full object-cover" />}
                    </div>
                  </td>
                  <td className="px-4 py-2.5 font-medium">{s.title}</td>
                  <td className="px-4 py-2.5 text-mut">{s.category_name}</td>
                  <td className="px-4 py-2.5"><StatusBadge status={s.status} /></td>
                  <td className="px-4 py-2.5"><CardBadgePills original={s.is_original} badge={s.card_badge} /></td>
                  <td className="px-4 py-2.5 text-mut whitespace-nowrap">
                    就绪 {s.ready_episodes} / {s.episode_rows}
                    {s.processing > 0 && <span className="ml-2 text-primary">处理中 {s.processing}</span>}
                    {s.failed > 0 && <span className="ml-2 text-danger">失败 {s.failed}</span>}
                  </td>
                  <td className="px-4 py-2.5 text-mut whitespace-nowrap">{formatTime(s.updated_at)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      <DeleteSeriesDialog ids={deleting} onClose={() => setDeleting(null)}
        onDeleted={(r) => {
          setDeleting(null);
          setSelected(new Set());
          // A page left empty by the delete would show "no series": step back one.
          if (r.deleted >= items.length && page > 1) setPage((p) => p - 1);
          setNotice(`已删除 ${r.deleted} 部剧${r.cos_deleted > 0 ? `，并从腾讯云 COS 删除了 ${r.cos_deleted} 个视频文件` : ""}。`
            + (r.cos_kept > 0 ? `${r.cos_kept} 个视频文件仍被其他剧使用，已保留。` : "")
            + (r.cos_failed > 0 ? `${r.cos_failed} 个 COS 文件没能删除，见「运行追踪 → 日志」。` : ""));
        }} />

      {pages > 1 && (
        <div className="flex items-center justify-end gap-2 mt-3 text-[12.5px] text-mut">
          <Button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>上一页</Button>
          <span>{page} / {pages}</span>
          <Button disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>下一页</Button>
        </div>
      )}
    </>
  );
}
