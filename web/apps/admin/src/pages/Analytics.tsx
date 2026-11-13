import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { ColumnChart, StatTile } from "../charts";
import { regionName } from "../regions";
import { api } from "../session";
import { Card, ErrorText, PageHeader } from "../ui";

type Quality = Schemas["QualityDto"];

const seconds = (ms: number | null | undefined) => (ms == null ? "—" : `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)} s`);
const pct = (r: number | null | undefined) => (r == null ? "—" : `${(r * 100).toFixed(r < 0.1 ? 1 : 0)}%`);
const num = (n: number) => n.toLocaleString("en-US");

const DEVICES: Record<string, string> = { mobile: "手机", desktop: "电脑" };
const BROWSERS: Record<string, string> = { chrome: "Chrome", safari: "Safari", firefox: "Firefox", edge: "Edge", samsung: "三星浏览器", other: "其他" };

function QualityCells({ q }: { q: Quality }) {
  return (
    <>
      <td className="px-3 py-2.5 text-right tabular-nums">{num(q.samples)}</td>
      <td className="px-3 py-2.5 text-right tabular-nums">{seconds(q.startup_p50_ms)}</td>
      <td className="px-3 py-2.5 text-right tabular-nums">{pct(q.rebuffer_ratio)}</td>
      <td className="px-3 py-2.5 text-right tabular-nums">{pct(q.error_rate)}</td>
    </>
  );
}

function Breakdown({ title, rows, name }: { title: string; rows: Schemas["AnalyticsBreakdownDto"][]; name: (key: string) => string }) {
  return (
    <Card className="overflow-hidden">
      <h3 className="px-4 pt-3 pb-2 text-[13px] font-bold">{title}</h3>
      {rows.length === 0 ? <div className="px-4 pb-4 text-[12.5px] text-dim">暂无数据</div> : (
        <table className="w-full text-[12.5px]">
          <thead className="text-[11px] text-dim bg-s2">
            <tr>
              <th className="px-3 py-2 font-medium text-left"> </th>
              <th className="px-3 py-2 font-medium text-right">播放</th>
              <th className="px-3 py-2 font-medium text-right">起播</th>
              <th className="px-3 py-2 font-medium text-right">卡顿</th>
              <th className="px-3 py-2 font-medium text-right">失败</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.key} className="border-t border-white/5">
                <td className="px-3 py-2.5 truncate max-w-[140px]">{name(r.key)}</td>
                <QualityCells q={r.quality} />
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

/** 数据概览: plays and playback quality, from anonymous play events and samples (kept 30 days). */
export function AnalyticsPage() {
  const [days, setDays] = useState(7);
  const overview = useQuery({
    queryKey: ["admin", "analytics", days],
    queryFn: async () => unwrap(await api.GET("/api/admin/analytics/overview", { params: { query: { days } } })),
    placeholderData: keepPreviousData,
  });
  const o = overview.data;

  return (
    <>
      <PageHeader title="数据概览"
        sub="来自匿名播放事件与播放质量记录（不关联账号），按 UTC 日期统计，保留 30 天。起播 = 从开始播放到出现画面的时间（中位数）；卡顿 = 卡顿时长占观看时长的比例。" />

      <div className="flex items-center gap-1 p-1 rounded-(--r-ctl) bg-s2 w-fit mb-4" role="radiogroup" aria-label="时间范围">
        {[7, 14, 30].map((d) => (
          <button key={d} role="radio" aria-checked={days === d} onClick={() => setDays(d)}
            className={`h-8 px-4 rounded-(--r-ctl) text-[12.5px] font-semibold ${days === d ? "bg-s4 text-on" : "text-mut hover:text-on"}`}>近 {d} 天</button>
        ))}
      </div>

      {overview.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : overview.isError || !o ? <ErrorText error={overview.error} /> : (
        <div className={overview.isPlaceholderData ? "opacity-60 transition-opacity" : "transition-opacity"}>
          <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
            <StatTile label="播放次数" value={num(o.plays)} sub={`${num(o.sessions)} 个独立会话`} />
            <StatTile label="完播率" value={pct(o.completion_rate)} sub={`${num(o.completions)} 次看到 90% 以上`} />
            <StatTile label="观看时长" value={`${o.watch_hours.toFixed(o.watch_hours < 10 ? 1 : 0)} 小时`} sub="来自播放质量记录" />
            <StatTile label="新会员" value={num(o.new_members)} sub={`新评论 ${num(o.comments)} 条`} />
            <StatTile label="起播时间（中位数）" value={seconds(o.quality.startup_p50_ms)} sub={`95% 在 ${seconds(o.quality.startup_p95_ms)} 内`} />
            <StatTile label="卡顿率" value={pct(o.quality.rebuffer_ratio)} sub="卡顿时长 / 观看时长" />
            <StatTile label="播放失败率" value={pct(o.quality.error_rate)} sub={`${num(o.quality.samples)} 次播放记录`} />
          </div>

          <Card className="p-4 mt-4">
            <h2 className="text-[14px] font-bold mb-3">每日播放次数</h2>
            <ColumnChart title="每日播放次数" data={o.daily.map((d) => ({
              key: d.day, label: d.day.slice(5), value: d.plays, detail: `${num(d.sessions)} 个会话`,
            }))} />
          </Card>

          <Card className="overflow-hidden mt-4">
            <table className="w-full text-[12.5px]">
              <thead className="text-[11px] text-dim bg-s2">
                <tr>
                  <th className="px-3 py-2 font-medium text-left">日期（UTC）</th>
                  <th className="px-3 py-2 font-medium text-right">播放</th>
                  <th className="px-3 py-2 font-medium text-right">会话</th>
                  <th className="px-3 py-2 font-medium text-right">完播</th>
                  <th className="px-3 py-2 font-medium text-right">质量记录</th>
                  <th className="px-3 py-2 font-medium text-right">起播</th>
                  <th className="px-3 py-2 font-medium text-right">卡顿</th>
                  <th className="px-3 py-2 font-medium text-right">失败</th>
                </tr>
              </thead>
              <tbody>
                {[...o.daily].reverse().map((d) => (
                  <tr key={d.day} className="border-t border-white/5">
                    <td className="px-3 py-2.5 tabular-nums">{d.day}</td>
                    <td className="px-3 py-2.5 text-right tabular-nums">{num(d.plays)}</td>
                    <td className="px-3 py-2.5 text-right tabular-nums">{num(d.sessions)}</td>
                    <td className="px-3 py-2.5 text-right tabular-nums">{num(d.completions)}</td>
                    <QualityCells q={d.quality} />
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>

          <div className="grid lg:grid-cols-3 gap-3 mt-4">
            <Breakdown title="按地区" rows={o.by_country} name={(k) => `${regionName(k)}（${k}）`} />
            <Breakdown title="按设备" rows={o.by_device} name={(k) => DEVICES[k] ?? k} />
            <Breakdown title="按浏览器" rows={o.by_browser} name={(k) => BROWSERS[k] ?? k} />
          </div>

          <Card className="overflow-hidden mt-4">
            <h3 className="px-4 pt-3 pb-2 text-[13px] font-bold">热门剧集</h3>
            {o.top_series.length === 0 ? <div className="px-4 pb-4 text-[12.5px] text-dim">暂无数据</div> : (
              <table className="w-full text-[12.5px]">
                <thead className="text-[11px] text-dim bg-s2">
                  <tr>
                    <th className="px-3 py-2 font-medium text-left">剧集</th>
                    <th className="px-3 py-2 font-medium text-right">播放</th>
                    <th className="px-3 py-2 font-medium text-right">会话</th>
                    <th className="px-3 py-2 font-medium text-right">完播率</th>
                  </tr>
                </thead>
                <tbody>
                  {o.top_series.map((t) => (
                    <tr key={t.series_id} className="border-t border-white/5">
                      <td className="px-3 py-2.5">{t.title} <span className="text-dim">#{t.series_id}</span></td>
                      <td className="px-3 py-2.5 text-right tabular-nums">{num(t.plays)}</td>
                      <td className="px-3 py-2.5 text-right tabular-nums">{num(t.sessions)}</td>
                      <td className="px-3 py-2.5 text-right tabular-nums">{pct(t.completion_rate)}</td>
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
