import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, RefreshCw, Search } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { StatTile } from "../charts";
import { api } from "../session";
import { Button, Card, Dialog, ErrorText, PageHeader, formatTime, inputClass } from "../ui";

type Trace = Schemas["TraceSummaryDto"];
type Span = Schemas["SpanDto"];
type Log = Schemas["TelemetryLogDto"];

const SHOW: [string, string][] = [["problems", "有问题的"], ["errors", "失败"], ["slow", "慢"], ["all", "全部已保存"]];
const HOURS: [number, string][] = [[1, "1 小时"], [24, "24 小时"], [168, "7 天"]];
const SERVICES: [string, string][] = [["", "全部服务"], ["reelguo-api", "API"], ["reelguo-worker", "Worker"]];
const LEVEL: Record<string, [string, string]> = {
  warning: ["警告", "bg-star/20 text-star"],
  error: ["错误", "bg-danger/20 text-danger"],
  critical: ["严重", "bg-danger text-on-danger"],
};

function ms(value: number) {
  if (value >= 1000) return `${(value / 1000).toFixed(value >= 10_000 ? 1 : 2)} s`;
  return `${value.toFixed(value < 10 ? 1 : 0)} ms`;
}

const serviceName = (s: string) => (s === "reelguo-api" ? "API" : s === "reelguo-worker" ? "Worker" : s);

/**
 * 运行追踪: requests and worker jobs that failed or were slow, step by step (database commands, outgoing calls), and
 * the warning/error log. Stored by the API and the worker in the database (backend Telemetry/LocalTelemetry.cs).
 */
export function TracingPage() {
  const overview = useQuery({
    queryKey: ["admin", "telemetry"],
    queryFn: async () => unwrap(await api.GET("/api/admin/telemetry")),
    refetchInterval: 30_000,
  });
  const [tab, setTab] = useState<"traces" | "logs">("traces");
  const [show, setShow] = useState("problems");
  const [level, setLevel] = useState("");
  const [service, setService] = useState("");
  const [hours, setHours] = useState(24);
  const [input, setInput] = useState("");
  const [q, setQ] = useState("");
  const [open, setOpen] = useState<string | null>(null);

  const traces = useInfiniteQuery({
    queryKey: ["admin", "telemetry", "traces", show, service, hours, q],
    enabled: tab === "traces",
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/admin/telemetry/traces", {
      params: { query: { show, service: service || undefined, hours, q: q || undefined, before: pageParam } },
    })),
    getNextPageParam: (last) => (last.more ? last.items[last.items.length - 1]?.started_at : undefined),
  });
  const logs = useInfiniteQuery({
    queryKey: ["admin", "telemetry", "logs", level, service, hours, q],
    enabled: tab === "logs",
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/admin/telemetry/logs", {
      params: { query: { level: level || undefined, service: service || undefined, hours, q: q || undefined, before: pageParam } },
    })),
    getNextPageParam: (last) => (last.more ? last.items[last.items.length - 1]?.at : undefined),
  });

  const o = overview.data;
  const search = (e: FormEvent) => { e.preventDefault(); setQ(input.trim()); };
  const refresh = () => { void overview.refetch(); void (tab === "traces" ? traces.refetch() : logs.refetch()); };
  const traceItems = traces.data?.pages.flatMap((p) => p.items) ?? [];
  const logItems = logs.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <>
      <PageHeader
        title="运行追踪"
        sub={<>出错、失败或变慢（≥ {o?.slow_ms ?? 1000} ms）的请求和后台任务，逐步显示每一步的耗时和错误，以及所有警告与错误日志。
          正常请求按 {Math.round((o?.sample_rate ?? 0.02) * 100)}% 抽样保存作对照。数据保存 {o?.retention_days ?? 7} 天。</>}
        actions={<Button onClick={refresh}><RefreshCw size={14} className="inline -mt-0.5 mr-1" />刷新</Button>}
      />
      <ErrorText error={overview.error} />
      {o && (
        <>
          <div className="grid grid-cols-4 gap-3 mb-3">
            <StatTile label="失败的请求 / 任务（24 小时）" value={o.failed_traces24h} />
            <StatTile label={`慢请求 / 任务（≥ ${o.slow_ms} ms）`} value={o.slow_traces24h} />
            <StatTile label="错误日志（24 小时）" value={o.errors24h} />
            <StatTile label="警告日志（24 小时）" value={o.warnings24h} />
          </div>
          <p className="mb-4 text-[12px] text-mut">
            {o.store ? "本机保存：已开启" : "本机保存：已关闭（Telemetry:Local:Enabled）"}
            {o.store && ` · 最近写入 ${o.last_write ? formatTime(o.last_write) : "（还没有数据）"}`}
            {o.dropped > 0 && ` · 高峰时丢弃 ${o.dropped} 条`}
            {` · ${o.export ? "另外导出到 OTLP（OTEL_EXPORTER_OTLP_ENDPOINT）" : "未导出到外部监控（可选，见部署文档）"}`}
            {o.writer_error && <span className="text-danger"> · 写入失败：{o.writer_error}</span>}
          </p>
        </>
      )}

      <Card className="p-4 mb-4">
        <div className="flex items-center gap-2 flex-wrap">
          <div className="flex rounded-(--r-sm) bg-s3 p-0.5">
            {([["traces", "请求与任务"], ["logs", "日志"]] as const).map(([k, label]) => (
              <button key={k} onClick={() => setTab(k)} className={`h-8 px-3 rounded-(--r-sm) text-[13px] ${tab === k ? "bg-s1 text-on font-semibold" : "text-mut"}`}>{label}</button>
            ))}
          </div>
          {tab === "traces" ? (
            <select value={show} onChange={(e) => setShow(e.target.value)} className={inputClass}>
              {SHOW.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
            </select>
          ) : (
            <select value={level} onChange={(e) => setLevel(e.target.value)} className={inputClass}>
              <option value="">警告和错误</option><option value="error">只看错误</option><option value="warning">只看警告</option>
            </select>
          )}
          <select value={service} onChange={(e) => setService(e.target.value)} className={inputClass}>
            {SERVICES.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
          </select>
          <select value={hours} onChange={(e) => setHours(Number(e.target.value))} className={inputClass}>
            {HOURS.map(([h, label]) => <option key={h} value={h}>最近 {label}</option>)}
          </select>
          <form onSubmit={search} className="flex items-center gap-2 ml-auto">
            <input value={input} onChange={(e) => setInput(e.target.value)} placeholder={tab === "traces" ? "搜索名称、错误或追踪 ID" : "搜索消息、来源或异常"} className={`${inputClass} w-[260px]`} />
            <Button type="submit"><Search size={14} className="inline -mt-0.5 mr-1" />搜索</Button>
          </form>
        </div>
      </Card>

      {tab === "traces" ? (
        <Card className="overflow-hidden">
          <ErrorText error={traces.error} />
          <table className="w-full text-[12.5px]">
            <thead className="bg-s2 text-mut text-left">
              <tr><th className="px-4 py-2 font-normal w-[170px]">时间</th><th className="px-3 py-2 font-normal w-[80px]">服务</th><th className="px-3 py-2 font-normal">请求 / 任务</th><th className="px-3 py-2 font-normal w-[120px]">结果</th><th className="px-3 py-2 font-normal w-[90px] text-right">耗时</th><th className="px-4 py-2 font-normal w-[70px] text-right">步骤</th></tr>
            </thead>
            <tbody>
              {traceItems.map((t) => <TraceRow key={`${t.trace_id}/${t.started_at}`} t={t} slow={o?.slow_ms ?? 1000} onOpen={() => setOpen(t.trace_id)} />)}
            </tbody>
          </table>
          {traces.isLoading && <div className="py-10 flex justify-center"><Spinner size={24} /></div>}
          {!traces.isLoading && traceItems.length === 0 && <p className="py-10 text-center text-[13px] text-mut">{show === "problems" ? "这段时间没有失败或变慢的请求。" : "没有符合条件的记录。"}</p>}
          {traces.hasNextPage && <div className="p-3 text-center"><Button disabled={traces.isFetchingNextPage} onClick={() => void traces.fetchNextPage()}>{traces.isFetchingNextPage ? "加载中…" : "加载更多"}</Button></div>}
        </Card>
      ) : (
        <Card className="overflow-hidden">
          <ErrorText error={logs.error} />
          <div>{logItems.map((l) => <LogRow key={l.id} log={l} onOpenTrace={l.trace_id ? () => setOpen(l.trace_id!) : undefined} />)}</div>
          {logs.isLoading && <div className="py-10 flex justify-center"><Spinner size={24} /></div>}
          {!logs.isLoading && logItems.length === 0 && <p className="py-10 text-center text-[13px] text-mut">这段时间没有警告或错误日志。</p>}
          {logs.hasNextPage && <div className="p-3 text-center"><Button disabled={logs.isFetchingNextPage} onClick={() => void logs.fetchNextPage()}>{logs.isFetchingNextPage ? "加载中…" : "加载更多"}</Button></div>}
        </Card>
      )}

      <Dialog open={open !== null} title="追踪详情" width={1000} onClose={() => setOpen(null)}>
        {open && <TraceDetail traceId={open} slow={o?.slow_ms ?? 1000} />}
      </Dialog>
    </>
  );
}

function TraceRow({ t, slow, onOpen }: { t: Trace; slow: number; onOpen: () => void }) {
  return (
    <tr onClick={onOpen} className="border-t border-white/[.04] hover:bg-s2 cursor-pointer">
      <td className="px-4 py-2 text-mut whitespace-nowrap">{formatTime(t.started_at)}</td>
      <td className="px-3 py-2">{serviceName(t.service)}</td>
      <td className="px-3 py-2 min-w-0">
        <div className="truncate max-w-[520px]" title={t.name}>{t.name}</div>
        {t.status_message && <div className="truncate max-w-[520px] text-[11.5px] text-danger" title={t.status_message}>{t.status_message}</div>}
      </td>
      <td className="px-3 py-2">
        {t.is_error ? <span className="px-1.5 py-0.5 rounded text-[11.5px] bg-danger/20 text-danger">失败{t.status_code ? ` ${t.status_code}` : ""}</span>
          : <span className="text-mut">{t.status_code ?? "完成"}</span>}
      </td>
      <td className={`px-3 py-2 text-right whitespace-nowrap ${t.duration_ms >= slow ? "text-star font-semibold" : ""}`}>{ms(t.duration_ms)}</td>
      <td className="px-4 py-2 text-right text-mut">{t.spans}</td>
    </tr>
  );
}

function LogRow({ log, onOpenTrace }: { log: Log; onOpenTrace?: () => void }) {
  const [expanded, setExpanded] = useState(false);
  const [label, style] = LEVEL[log.level] ?? LEVEL.warning!;
  return (
    <div className="border-t border-white/[.04] px-4 py-2 text-[12.5px]">
      <div className="flex items-start gap-3">
        <button onClick={() => setExpanded(!expanded)} disabled={!log.exception} className="mt-0.5 text-mut disabled:opacity-0" aria-label="展开">
          {expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
        </button>
        <span className="text-mut whitespace-nowrap w-[150px] shrink-0">{formatTime(log.at)}</span>
        <span className={`px-1.5 py-0.5 rounded text-[11.5px] shrink-0 ${style}`}>{label}</span>
        <span className="w-[56px] shrink-0">{serviceName(log.service)}</span>
        <div className="flex-1 min-w-0">
          <div className="break-words">{log.message}</div>
          <div className="text-[11.5px] text-dim truncate" title={log.category}>{log.category}</div>
        </div>
        {onOpenTrace && <Button className="shrink-0" onClick={onOpenTrace}>查看追踪</Button>}
      </div>
      {expanded && log.exception && <pre className="mt-2 ml-7 p-3 rounded bg-s2 text-[11.5px] text-mut whitespace-pre-wrap break-words max-h-[320px] overflow-auto">{log.exception}</pre>}
    </div>
  );
}

/** One trace: its steps as a timeline (indented by nesting), each with its attributes, then its log lines. */
function TraceDetail({ traceId, slow }: { traceId: string; slow: number }) {
  const detail = useQuery({
    queryKey: ["admin", "telemetry", "trace", traceId],
    queryFn: async () => unwrap(await api.GET("/api/admin/telemetry/traces/{traceId}", { params: { path: { traceId } } })),
  });
  const [openSpan, setOpenSpan] = useState<string | null>(null);

  const rows = useMemo(() => {
    const spans = detail.data?.spans ?? [];
    const ids = new Set(spans.map((s) => s.span_id));
    const children = new Map<string | null, Span[]>();
    for (const s of spans) {
      const parent = s.parent_span_id && ids.has(s.parent_span_id) ? s.parent_span_id : null;
      children.set(parent, [...(children.get(parent) ?? []), s]);
    }
    const ordered: { span: Span; depth: number }[] = [];
    const walk = (parent: string | null, depth: number) => {
      for (const s of (children.get(parent) ?? []).sort((a, b) => a.started_at.localeCompare(b.started_at))) {
        ordered.push({ span: s, depth });
        walk(s.span_id, depth + 1);
      }
    };
    walk(null, 0);
    return ordered;
  }, [detail.data]);

  if (detail.isLoading) return <div className="py-10 flex justify-center"><Spinner size={24} /></div>;
  if (detail.error) return <ErrorText error={detail.error} />;
  const d = detail.data!;
  const start = Math.min(...d.spans.map((s) => Date.parse(s.started_at)));
  const end = Math.max(...d.spans.map((s) => Date.parse(s.started_at) + s.duration_ms));
  const total = Math.max(1, end - start);

  return (
    <div className="text-[12.5px]">
      <div className="flex items-center gap-3 flex-wrap text-mut mb-3">
        <span>追踪 ID <code className="text-on select-all">{d.trace_id}</code></span>
        {d.spans.length > 0 && <span>开始 {formatTime(new Date(start).toISOString())} · 共 {ms(total)} · {d.spans.length} 个步骤</span>}
      </div>
      {rows.length > 0 && (
        <div className="rounded-(--r-md) border border-white/[.06] overflow-hidden">
          {rows.map(({ span, depth }) => {
            const left = ((Date.parse(span.started_at) - start) / total) * 100;
            const width = Math.max(0.5, (span.duration_ms / total) * 100);
            const isOpen = openSpan === span.span_id;
            return (
              <div key={span.span_id} className="border-t first:border-t-0 border-white/[.04]">
                <button onClick={() => setOpenSpan(isOpen ? null : span.span_id)} className="w-full flex items-center gap-3 px-3 py-1.5 hover:bg-s2 text-left">
                  <div className="w-[380px] shrink-0 min-w-0 flex items-center gap-1.5" style={{ paddingLeft: depth * 14 }}>
                    {isOpen ? <ChevronDown size={13} className="shrink-0 text-mut" /> : <ChevronRight size={13} className="shrink-0 text-mut" />}
                    <span className={`truncate ${span.is_error ? "text-danger font-semibold" : ""}`} title={span.name}>{span.name}</span>
                    <span className="shrink-0 text-[10.5px] text-dim">{serviceName(span.service)}</span>
                  </div>
                  <div className="flex-1 relative h-3 rounded bg-s3">
                    <div className={`absolute top-0 h-3 rounded ${span.is_error ? "bg-danger" : span.duration_ms >= slow ? "bg-star" : "bg-primary/70"}`} style={{ left: `${left}%`, width: `${width}%` }} />
                  </div>
                  <span className={`w-[76px] shrink-0 text-right ${span.duration_ms >= slow ? "text-star" : "text-mut"}`}>{ms(span.duration_ms)}</span>
                </button>
                {isOpen && (
                  <div className="px-3 pb-2" style={{ paddingLeft: 12 + depth * 14 + 20 }}>
                    {span.status_message && <p className="mb-1.5 text-danger break-words">{span.status_message}</p>}
                    <table className="text-[11.5px]">
                      <tbody>
                        <tr><td className="pr-3 text-dim align-top">类型</td><td className="text-mut">{span.kind} · 开始 {formatTime(span.started_at)}</td></tr>
                        {Object.entries(span.attributes).map(([k, v]) => (
                          <tr key={k}><td className="pr-3 text-dim align-top whitespace-nowrap">{k}</td><td className="text-mut break-all whitespace-pre-wrap">{v}</td></tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
      {d.spans.length === 0 && <p className="text-mut">这条追踪没有保存步骤（只有日志）。</p>}
      {d.logs.length > 0 && (
        <>
          <div className="mt-4 mb-1 text-[13px] font-semibold">这次请求里写下的日志</div>
          <div className="rounded-(--r-md) border border-white/[.06]">{d.logs.map((l) => <LogRow key={l.id} log={l} />)}</div>
        </>
      )}
    </div>
  );
}
