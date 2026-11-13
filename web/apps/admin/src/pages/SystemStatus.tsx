import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, OctagonAlert } from "lucide-react";
import { StatTile } from "../charts";
import { api } from "../session";
import { Card, ErrorText, PageHeader, formatTime } from "../ui";

function ago(iso: string | null | undefined): string {
  if (!iso) return "从未";
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return `${Math.round(s)} 秒前`;
  if (s < 3600) return `${Math.round(s / 60)} 分钟前`;
  if (s < 86_400) return `${Math.round(s / 3600)} 小时前`;
  return `${Math.round(s / 86_400)} 天前`;
}

function bytes(n: number): string {
  if (n >= 1024 ** 3) return `${(n / 1024 ** 3).toFixed(1)} GB`;
  if (n >= 1024 ** 2) return `${(n / 1024 ** 2).toFixed(0)} MB`;
  return `${Math.round(n / 1024)} KB`;
}

const ok = (good: boolean, yes: string, no: string) => (
  <span className="inline-flex items-center gap-1.5">
    {good ? <CheckCircle2 size={18} className="text-success" /> : <OctagonAlert size={18} className="text-danger" />}
    {good ? yes : no}
  </span>
);

/** 系统状态: what the on-call person checks first. Refreshes every 30 s. */
export function SystemStatusPage() {
  const status = useQuery({
    queryKey: ["admin", "system-status"],
    queryFn: async () => unwrap(await api.GET("/api/admin/system/status")),
    refetchInterval: 30_000,
  });
  const s = status.data;

  return (
    <>
      <PageHeader title="系统状态"
        sub={s ? `每 30 秒刷新 · 版本 ${s.api_version} · ${s.environment} · 实例 ${s.instance} · 运行自 ${formatTime(s.started_at)}` : "每 30 秒刷新"} />
      {status.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : status.isError || !s ? <ErrorText error={status.error} /> : (
        <div className={status.isFetching ? "opacity-90 transition-opacity" : ""}>
          <Card className="p-4 mb-4">
            {s.alerts.length === 0 ? (
              <div className="flex items-center gap-2 text-[13.5px]"><CheckCircle2 size={18} className="text-success" />一切正常</div>
            ) : (
              <ul className="flex flex-col gap-2">
                {s.alerts.map((a) => (
                  <li key={a.message} className="flex items-start gap-2.5 text-[13px]">
                    {a.level === "critical"
                      ? <span className="shrink-0 inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[11px] font-bold bg-danger text-on-danger"><OctagonAlert size={12} />严重</span>
                      : <span className="shrink-0 inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[11px] font-bold bg-star/20 text-star"><AlertTriangle size={12} />注意</span>}
                    <span className="text-on">{a.message}</span>
                  </li>
                ))}
              </ul>
            )}
          </Card>

          <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
            <StatTile label="数据库" value={ok(s.database_ok, `${Math.round(s.database_latency_ms)} ms`, "无法连接")} sub={`占用 ${bytes(s.database_bytes)}`} />
            <StatTile label="媒体存储" value={ok(s.storage_ok, "正常", "异常")} sub={s.storage_provider} />
            <StatTile label="在线 Worker" value={ok(s.workers.some((w) => w.alive), `${s.workers.filter((w) => w.alive).length} 个`, "0 个")}
              sub={`共登记 ${s.workers.length} 个`} />
            <StatTile label="转码队列" value={`${s.jobs_queued} 等待 · ${s.jobs_running} 处理中`}
              sub={s.oldest_queued_seconds != null ? `最早已等待 ${Math.round(s.oldest_queued_seconds / 60)} 分钟` : "没有积压"} />
            <StatTile label="失败任务（24 小时）" value={s.jobs_failed24h} sub="在内容管理中查看并重试" />
            <StatTile label="榜单更新" value={ago(s.rankings_computed_at)} sub="每 10 分钟由 Worker 计算" />
            <StatTile label="最近 1 小时播放" value={s.plays_last_hour.toLocaleString("en-US")} sub={`播放失败 ${s.playback_errors_last_hour} 次`} />
            <StatTile label="监控数据导出" value={ok(s.telemetry_export, "已开启", "未开启")} sub="OTEL_EXPORTER_OTLP_ENDPOINT" />
            <StatTile label="待审核评论" value={s.pending_comments} />
            <StatTile label="待处理举报" value={s.open_reports} sub={s.urgent_reports > 0 ? `其中紧急 ${s.urgent_reports} 条` : "无紧急举报"} />
            <StatTile label="已下架剧集" value={s.revoked_series} sub="这些剧集的播放链接已失效" />
            <StatTile label="屏蔽 / 严格验证地区" value={`${s.blocked_countries} / ${s.strict_age_countries}`} />
          </div>

          <h2 className="text-[15px] font-bold mt-6 mb-2">支付</h2>
          <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
            <StatTile label="支付渠道"
              value={!s.payments.enabled ? "付费未开启" : ok(!!s.payments.provider, s.payments.provider ?? "", "不可用")}
              sub={s.payments.sandbox ? "模拟支付：不会产生真实扣款" : s.payments.enabled ? "新订单使用此渠道" : "所有剧集免费，充值中心关闭"} />
            <StatTile label="超过 1 小时未支付" value={s.payments.stale_pending_orders} sub="买家未完成支付，24 小时后自动过期" />
            <StatTile label="异常支付通知（24 小时）" value={s.payments.rejected_notices24h} sub="签名不符或金额与订单不一致" />
            <StatTile label="拒付率（30 天）" value={`${(s.payments.chargeback_rate30d * 100).toFixed(2)}%`}
              sub={`${s.payments.chargebacks30d} 笔拒付 / ${s.payments.paid_orders30d} 笔订单`} />
          </div>

          <h2 className="text-[15px] font-bold mt-6 mb-2">转码 Worker</h2>
          <Card className="overflow-hidden">
            {s.workers.length === 0 ? <div className="py-8 text-center text-[13px] text-dim">还没有 Worker 上报过心跳</div> : (
              <table className="w-full text-[13px]">
                <thead className="text-left text-[11.5px] text-dim bg-s2">
                  <tr>
                    <th className="px-4 py-2.5 font-medium">Worker</th>
                    <th className="px-4 py-2.5 font-medium">状态</th>
                    <th className="px-4 py-2.5 font-medium">版本</th>
                    <th className="px-4 py-2.5 font-medium">启动时间</th>
                    <th className="px-4 py-2.5 font-medium">最近心跳</th>
                    <th className="px-4 py-2.5 font-medium text-right">成功 / 失败</th>
                    <th className="px-4 py-2.5 font-medium text-right">当前任务</th>
                  </tr>
                </thead>
                <tbody>
                  {s.workers.map((w) => (
                    <tr key={w.worker_id} className="border-t border-white/5">
                      <td className="px-4 py-3 font-mono text-[12px]">{w.worker_id}</td>
                      <td className="px-4 py-3">{ok(w.alive, "在线", "离线")}</td>
                      <td className="px-4 py-3 text-mut">{w.version}</td>
                      <td className="px-4 py-3 text-mut whitespace-nowrap">{formatTime(w.started_at)}</td>
                      <td className="px-4 py-3 text-mut">{ago(w.seen_at)}</td>
                      <td className="px-4 py-3 text-right tabular-nums">{w.jobs_succeeded} / {w.jobs_failed}</td>
                      <td className="px-4 py-3 text-right tabular-nums text-mut">{w.current_job_id ?? "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </Card>
        </div>
      )}
    </>
  );
}
