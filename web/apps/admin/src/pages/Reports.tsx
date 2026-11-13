import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle } from "lucide-react";
import { useState } from "react";
import { regionName } from "../regions";
import { api } from "../session";
import { Button, Card, ErrorText, PageHeader, StatusBadge, formatTime } from "../ui";

type Status = Schemas["ReportStatus"];
type Row = Schemas["AdminReportDto"];

export const REPORT_REASONS: Record<Schemas["ReportReason"], string> = {
  minor: "涉及未成年人",
  non_consensual: "未经同意的真人形象",
  copyright: "侵犯版权",
  illegal: "违法违规",
  spam: "垃圾广告",
  other: "其他",
};

const TARGETS: Record<Schemas["ReportTarget"], string> = { series: "剧集", post: "文章", comment: "评论", user: "会员" };

/** What a takedown does to each kind of target. */
const TAKEDOWN: Record<Schemas["ReportTarget"], string> = {
  series: "下架剧集",
  post: "下架文章",
  comment: "删除评论",
  user: "封禁会员",
};

const TABS: [Status, string][] = [["open", "待处理"], ["resolved", "已处理"], ["dismissed", "已驳回"]];

/** 举报: urgent reports (minors, non-consensual real people) first, then oldest first. */
export function ReportsPage() {
  const qc = useQueryClient();
  const [status, setStatus] = useState<Status>("open");
  const [urgentOnly, setUrgentOnly] = useState(false);
  const [page, setPage] = useState(1);
  const list = useQuery({
    queryKey: ["admin", "reports", status, urgentOnly, page],
    queryFn: async () => unwrap(await api.GET("/api/admin/reports", { params: { query: { status, urgent: urgentOnly ? true : undefined, page } } })),
  });
  const resolve = useMutation({
    mutationFn: async ({ id, action, note }: { id: number; action: Schemas["ReportResolution"]; note: string | null }) =>
      unwrap(await api.POST("/api/admin/reports/{id}/resolve", { params: { path: { id } }, body: { action, note } })),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["admin", "reports"] });
      void qc.invalidateQueries({ queryKey: ["admin", "moderation-summary"] });
    },
  });

  const items = list.data?.items ?? [];
  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.size)) : 1;

  const act = (r: Row, action: Schemas["ReportResolution"]) => {
    const what = action === "takedown" ? `${TAKEDOWN[r.target_type]}并关闭该对象的全部 ${r.open_on_target} 条举报` : "驳回这条举报";
    const note = window.prompt(`确认${what}？\n可填写处理说明（会记录在操作日志）：`, "");
    if (note === null) return;
    resolve.mutate({ id: r.id, action, note: note.trim() || null });
  };

  return (
    <>
      <PageHeader title="举报"
        sub="任何人（包括未登录访客）都可以举报。涉及未成年人或未经同意的真人形象会标记为紧急并排在最前面。下架会同时关闭该对象的全部待处理举报。" />

      <div className="flex flex-wrap items-center gap-3 mb-3">
        <div className="flex gap-1 p-1 rounded-(--r-ctl) bg-s2">
          {TABS.map(([k, label]) => (
            <button key={k} onClick={() => { setStatus(k); setPage(1); }}
              className={`h-8 px-4 rounded-(--r-ctl) text-[12.5px] font-semibold ${status === k ? "bg-s4 text-on" : "text-mut hover:text-on"}`}>{label}</button>
          ))}
        </div>
        <label className="flex items-center gap-2 text-[12.5px] text-mut">
          <input type="checkbox" checked={urgentOnly} onChange={(e) => { setUrgentOnly(e.target.checked); setPage(1); }} />只看紧急
        </label>
        <span className="ml-auto text-[12px] text-dim">共 {list.data?.total ?? 0} 条</span>
      </div>
      <div className="mb-3"><ErrorText error={resolve.error} /></div>

      <div className="flex flex-col gap-2.5">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : list.isError ? (
          <ErrorText error={list.error} />
        ) : items.length === 0 ? (
          <Card className="py-12 text-center text-[13px] text-dim">{status === "open" ? "没有待处理的举报" : "暂无记录"}</Card>
        ) : items.map((r) => (
          <Card key={r.id} className={`p-4 ${r.urgent && r.status === "open" ? "ring-1 ring-danger/60" : ""}`}>
            <div className="flex items-start gap-4">
              <div className="flex-1 min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  {r.urgent && (
                    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-md text-[11px] font-bold bg-danger text-on-danger">
                      <AlertTriangle size={11} />紧急
                    </span>
                  )}
                  <span className="text-[13px] font-semibold">{REPORT_REASONS[r.reason]}</span>
                  <span className="text-[12px] text-dim">· {TARGETS[r.target_type]} #{r.target_id}</span>
                  {r.status === "open" && r.open_on_target > 1 && <span className="text-[11.5px] text-star">该对象共 {r.open_on_target} 条待处理举报</span>}
                  {r.status !== "open" && <StatusBadge status={r.status} />}
                </div>
                <p className="mt-1.5 text-[13px] text-on/90 break-words">{r.target_summary}</p>
                {r.detail && <p className="mt-1.5 text-[12.5px] text-mut whitespace-pre-wrap break-words">说明：{r.detail}</p>}
                <p className="mt-2 text-[11.5px] text-dim">
                  {r.reporter_name ? `@${r.reporter_name}` : "未登录访客"} · {regionName(r.country)} · {formatTime(r.created_at)}
                </p>
                {r.status !== "open" && (
                  <p className="mt-1 text-[11.5px] text-dim">{r.resolved_by} · {formatTime(r.resolved_at)}{r.resolution_note && ` · ${r.resolution_note}`}</p>
                )}
              </div>
              {r.status === "open" && (
                <div className="flex flex-col gap-2 shrink-0">
                  <Button variant="danger" disabled={resolve.isPending} onClick={() => act(r, "takedown")}>{TAKEDOWN[r.target_type]}</Button>
                  <Button variant="ghost" disabled={resolve.isPending} onClick={() => act(r, "dismiss")}>驳回</Button>
                </div>
              )}
            </div>
          </Card>
        ))}
      </div>

      {pages > 1 && (
        <div className="flex items-center justify-center gap-3 mt-3 text-[12.5px] text-mut">
          <Button variant="ghost" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>上一页</Button>
          {page} / {pages}
          <Button variant="ghost" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>下一页</Button>
        </div>
      )}
    </>
  );
}
