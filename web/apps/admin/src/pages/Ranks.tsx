import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, PageHeader, formatTime } from "../ui";

const KINDS = [["hot", "热播榜"], ["recommend", "推荐榜"], ["potential", "潜力榜"]] as const;

export function RanksAdminPage() {
  const qc = useQueryClient();
  const [kind, setKind] = useState<(typeof KINDS)[number][0]>("hot");
  const rows = useQuery({
    queryKey: ["admin", "ranks", kind],
    queryFn: async () => unwrap(await api.GET("/api/admin/ranks/{kind}", { params: { path: { kind } } })),
  });
  const recompute = useMutation({
    mutationFn: async () => {
      const r = await api.POST("/api/admin/ranks/recompute");
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["admin", "ranks"] }),
  });

  return (
    <>
      <PageHeader title="榜单"
        sub="热播榜按 7 天播放与互动计算；推荐榜由编辑推荐权重（剧集编辑页设置）优先，其次按完播率；潜力榜为 14 天内上线、增长最快的剧。后台每 10 分钟自动更新；各地区用户只看到其授权范围内的剧。"
        actions={<Button variant="primary" disabled={recompute.isPending} onClick={() => recompute.mutate()}>{recompute.isPending ? "计算中…" : "立即重新计算"}</Button>} />
      <ErrorText error={recompute.error} />
      <div className="flex gap-1 p-1 rounded-(--r-ctl) bg-s2 w-fit mb-4">
        {KINDS.map(([k, label]) => (
          <button key={k} onClick={() => setKind(k)} className={`px-4 h-8 rounded-(--r-ctl) text-[12.5px] ${kind === k ? "bg-s4 font-semibold" : "text-mut"}`}>{label}</button>
        ))}
      </div>
      <Card className="overflow-hidden">
        {rows.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr><th className="px-4 py-2.5 font-medium w-16">排名</th><th className="px-4 py-2.5 font-medium">剧集</th><th className="px-4 py-2.5 font-medium">指标</th><th className="px-4 py-2.5 font-medium">计算时间</th></tr>
            </thead>
            <tbody>
              {rows.data?.length === 0 && <tr><td colSpan={4} className="px-4 py-10 text-center text-dim">榜单为空：还没有播放数据或推荐。</td></tr>}
              {rows.data?.map((r) => (
                <tr key={r.position} className="border-t border-white/5">
                  <td className="px-4 py-2.5 font-semibold tabular-nums">{r.position}</td>
                  <td className="px-4 py-2.5">{r.title} <span className="text-dim">#{r.series_id}</span></td>
                  <td className="px-4 py-2.5 tabular-nums">{r.metric_value}</td>
                  <td className="px-4 py-2.5 text-mut">{formatTime(r.computed_at)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}
