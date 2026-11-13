import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState } from "react";
import { api } from "../session";
import { Button, Card, PageHeader, StatusBadge, formatTime, inputClass } from "../ui";

const STATUSES = [["", "全部状态"], ["draft", "草稿"], ["published", "已发布"], ["hidden", "已下架"]] as const;

export function SeriesListPage({ onOpen }: { onOpen: (id: number | "new") => void }) {
  const [q, setQ] = useState("");
  const [status, setStatus] = useState<(typeof STATUSES)[number][0]>("");
  const [page, setPage] = useState(1);

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

  return (
    <>
      <PageHeader
        title="内容管理"
        sub="剧集需要填写完整的版权与合规信息、并至少有一集处理完成，才能发布。"
        actions={<Button variant="primary" onClick={() => onOpen("new")}><Plus size={15} className="inline -mt-0.5 mr-1" />新建剧集</Button>}
      />

      <div className="flex gap-2 mb-4">
        <input value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} placeholder="搜索剧名或外部编号" className={`${inputClass} w-[280px]`} />
        <select value={status} onChange={(e) => { setStatus(e.target.value as typeof status); setPage(1); }} className={inputClass}>
          {STATUSES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
      </div>

      <Card className="overflow-hidden">
        {list.isPending ? (
          <div className="flex justify-center py-10"><Spinner /></div>
        ) : list.isError ? (
          <p className="p-6 text-[13px] text-danger">加载失败</p>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium w-[64px]">封面</th>
                <th className="px-4 py-2.5 font-medium">剧名</th>
                <th className="px-4 py-2.5 font-medium">频道</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">分集</th>
                <th className="px-4 py-2.5 font-medium">更新时间</th>
              </tr>
            </thead>
            <tbody>
              {list.data.items.length === 0 && (
                <tr><td colSpan={6} className="px-4 py-10 text-center text-dim">还没有剧集。点「新建剧集」或使用「批量导入」。</td></tr>
              )}
              {list.data.items.map((s) => (
                <tr key={s.id} onClick={() => onOpen(s.id)} className="border-t border-white/5 cursor-pointer hover:bg-(--p-hover)">
                  <td className="px-4 py-2.5">
                    <div className="w-10 h-[53px] rounded-md bg-s3 overflow-hidden">
                      {s.cover_url && <img src={s.cover_url} alt="" className="w-full h-full object-cover" />}
                    </div>
                  </td>
                  <td className="px-4 py-2.5 font-medium">{s.title}</td>
                  <td className="px-4 py-2.5 text-mut">{s.category_name}</td>
                  <td className="px-4 py-2.5"><StatusBadge status={s.status} /></td>
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
