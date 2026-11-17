import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle } from "lucide-react";
import { useEffect, useState } from "react";
import { api } from "../session";
import { Button, Dialog, ErrorText, StatusBadge } from "../ui";

type DeleteResult = Schemas["DeleteSeriesResult"];

/**
 * Confirms a permanent delete of one or more series. Shows what goes with each one first (episodes, buyers, comments)
 * and asks for an explicit tick when a series is on the site or has been bought.
 */
export function DeleteSeriesDialog({ ids, onClose, onDeleted }: {
  ids: number[] | null;
  onClose: () => void;
  onDeleted: (result: DeleteResult) => void;
}) {
  const qc = useQueryClient();
  const [confirmed, setConfirmed] = useState(false);
  // Shown instead of closing when some COS files could not be deleted, so the admin knows to clean them up.
  const [problem, setProblem] = useState<DeleteResult | null>(null);
  useEffect(() => { setConfirmed(false); setProblem(null); }, [ids]);

  const impact = useQuery({
    queryKey: ["admin", "series-delete-preview", ids],
    queryFn: async () => unwrap(await api.POST("/api/admin/series/delete-preview", { body: { ids: ids! } })),
    enabled: ids !== null && ids.length > 0,
    staleTime: 0,
    gcTime: 0,
  });
  const remove = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/admin/series/delete", { body: { ids: ids! } })),
    onSuccess: (result) => {
      for (const id of result.ids) qc.removeQueries({ queryKey: ["admin", "series", "detail", id] });
      void qc.invalidateQueries({ queryKey: ["admin", "series"] });
      if (result.cos_failed > 0) setProblem(result);
      else onDeleted(result);
    },
  });

  const rows = impact.data ?? [];
  const published = rows.filter((r) => r.status === "published").length;
  const buyers = rows.reduce((n, r) => n + r.buyers, 0);
  const cosFiles = rows.reduce((n, r) => n + r.cos_files, 0);
  const risky = published > 0 || buyers > 0;
  const only = rows.length === 1 ? rows[0] : undefined;
  const title = only ? `删除《${only.title}》` : `删除 ${rows.length || ids?.length || 0} 部剧`;

  return (
    <Dialog open={ids !== null} title={title} width={560} onClose={() => problem ? onDeleted(problem) : !remove.isPending && onClose()}>
      {problem ? (
        <>
          <p className="text-[13px]">已删除 {problem.deleted} 部剧{problem.cos_deleted > 0 && `，并从腾讯云 COS 删除了 ${problem.cos_deleted} 个视频文件`}。</p>
          <p className="mt-3 flex gap-1.5 text-[12.5px] text-danger leading-relaxed"><AlertTriangle size={14} className="shrink-0 mt-0.5" />
            但有 {problem.cos_failed} 个 COS 视频文件没能删除，最常见的原因是 COS 密钥没有删除权限（cos:DeleteObject）。
            文件名记录在「运行追踪 → 日志」中，请到 COS 控制台手动删除。</p>
          <div className="mt-5 flex justify-end"><Button variant="primary" onClick={() => onDeleted(problem)}>知道了</Button></div>
        </>
      ) : impact.isPending ? (
        <div className="flex justify-center py-8"><Spinner /></div>
      ) : impact.isError ? (
        <ErrorText error={impact.error} />
      ) : rows.length === 0 ? (
        <p className="text-[13px] text-mut">所选的剧已不存在，可能已被删除。</p>
      ) : (
        <>
          <ul className="max-h-[260px] overflow-auto rounded-(--r-sm) bg-s2 divide-y divide-white/5 text-[13px]">
            {rows.map((r) => (
              <li key={r.id} className="flex items-center gap-2.5 px-3 py-2">
                <StatusBadge status={r.status} />
                <span className="flex-1 truncate font-medium">{r.title}</span>
                <span className="text-[11.5px] text-dim whitespace-nowrap">
                  {r.episodes} 集{r.buyers > 0 && <> · <span className="text-danger">{r.buyers} 人购买过</span></>}{r.comments > 0 && ` · ${r.comments} 条评论`}
                </span>
              </li>
            ))}
          </ul>

          <div className="mt-4 space-y-2 text-[12.5px] text-mut leading-relaxed">
            <p>剧集信息、全部分集、封面和视频文件、评论、点赞收藏和观看记录会<strong className="text-on">永久删除，无法恢复</strong>。</p>
            {published > 0 && (
              <p className="flex gap-1.5 text-danger"><AlertTriangle size={14} className="shrink-0 mt-0.5" />
                {rows.length === 1 ? "这部剧正在上架" : `其中 ${published} 部正在上架`}，删除后用户立即无法观看。只想暂时不给用户看，请用「下架」。</p>
            )}
            {buyers > 0 && (
              <p className="flex gap-1.5 text-danger"><AlertTriangle size={14} className="shrink-0 mt-0.5" />
                共 {buyers} 位用户用金币购买过分集，购买记录会一起删除（金币流水保留，可在「会员 → 钱包」中查到）。</p>
            )}
            {cosFiles > 0 && (
              <p>其中 {cosFiles} 集的视频存放在腾讯云 COS，会一并从 COS 删除（仍被其他剧使用的文件除外）。</p>
            )}
          </div>

          {risky && (
            <label className="mt-4 flex items-center gap-2 text-[13px] cursor-pointer select-none">
              <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
              我已了解以上影响，确认删除
            </label>
          )}
        </>
      )}

      {!problem && (
        <>
          <ErrorText error={remove.error} />
          <div className="mt-5 flex justify-end gap-2">
            <Button onClick={onClose} disabled={remove.isPending}>取消</Button>
            <Button variant="danger" className="!bg-danger !text-on-danger font-bold"
              disabled={!rows.length || (risky && !confirmed) || remove.isPending} onClick={() => remove.mutate()}>
              {remove.isPending ? "删除中…" : rows.length > 1 ? `永久删除 ${rows.length} 部` : "永久删除"}
            </Button>
          </div>
        </>
      )}
    </Dialog>
  );
}
