import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ColumnChart, StatTile } from "../charts";
import { regionName } from "../regions";
import { api } from "../session";
import { Button, Card, ErrorText, PageHeader, formatMoney, formatTime, inputClass } from "../ui";
import { contents } from "./Products";

type OrderStatus = Schemas["OrderStatus"];

export const ORDER_STATUS: Record<OrderStatus, [string, string]> = {
  pending: ["待支付", "bg-s4 text-mut"],
  paid: ["已支付", "bg-success/20 text-success"],
  failed: ["支付失败", "bg-danger/15 text-danger"],
  canceled: ["已取消", "bg-s4 text-mut"],
  expired: ["已过期", "bg-s4 text-dim"],
  refunded: ["已退款", "bg-star/20 text-star"],
  charged_back: ["已拒付", "bg-danger text-on-danger"],
};

export function OrderBadge({ status }: { status: OrderStatus }) {
  const [label, style] = ORDER_STATUS[status];
  return <span className={`inline-block px-2 py-0.5 rounded-md text-[11px] font-semibold whitespace-nowrap ${style}`}>{label}</span>;
}

const pct = (r: number) => `${(r * 100).toFixed(r > 0 && r < 0.1 ? 2 : 1)}%`;
const num = (n: number) => n.toLocaleString("en-US");

function Revenue() {
  const [days, setDays] = useState(30);
  const revenue = useQuery({
    queryKey: ["admin", "revenue", days],
    queryFn: async () => unwrap(await api.GET("/api/admin/analytics/revenue", { params: { query: { days } } })),
    placeholderData: keepPreviousData,
  });
  const r = revenue.data;
  const money = (cents: number) => (r ? formatMoney(cents, r.currency) : "—");

  return (
    <>
      <div className="flex items-center gap-3 mb-3">
        <h2 className="text-[15px] font-bold">营收</h2>
        <div className="flex items-center gap-1 p-1 rounded-(--r-ctl) bg-s2" role="radiogroup" aria-label="时间范围">
          {[7, 30, 90].map((d) => (
            <button key={d} role="radio" aria-checked={days === d} onClick={() => setDays(d)}
              className={`h-7 px-3 rounded-(--r-ctl) text-[12px] font-semibold ${days === d ? "bg-s4 text-on" : "text-mut hover:text-on"}`}>近 {d} 天</button>
          ))}
        </div>
        <span className="text-[11.5px] text-dim">按 UTC 日期；收入按支付日，退款与拒付按发生日</span>
      </div>
      {revenue.isPending ? <div className="flex justify-center py-8"><Spinner /></div> : revenue.isError || !r ? <ErrorText error={revenue.error} /> : (
        <div className={revenue.isPlaceholderData ? "opacity-60 transition-opacity" : "transition-opacity"}>
          <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
            <StatTile label="收入" value={money(r.revenue_cents)} sub={`净收入 ${money(r.net_cents)}（扣除退款与拒付）`} />
            <StatTile label="付费订单" value={num(r.paid_orders)} sub={`${num(r.buyers)} 位付费用户 · 人均 ${money(r.arppu_cents)}`} />
            <StatTile label="退款 / 拒付" value={`${num(r.refunds)} / ${num(r.chargebacks)}`} sub={`拒付率 ${pct(r.chargeback_rate)} · 共 ${money(r.reversed_cents)}`} />
            <StatTile label="金币消耗" value={num(r.coins_spent)} sub={`解锁 ${num(r.unlocks)} 集 · VIP 订单 ${num(r.vip_orders)}`} />
          </div>
          <Card className="p-4 mt-3">
            <h3 className="text-[13px] font-bold mb-3">每日收入（{r.currency}）</h3>
            <ColumnChart title={`每日收入（${r.currency}）`} data={r.daily.map((d) => ({
              key: d.day, label: d.day.slice(5), value: Math.round(d.revenue_cents) / 100,
              detail: `${num(d.orders)} 单 · ${num(d.buyers)} 人${d.reversed_cents ? ` · 退款/拒付 ${money(d.reversed_cents)}` : ""}`,
            }))} />
          </Card>
          <div className="grid lg:grid-cols-2 gap-3 mt-3">
            <Card className="overflow-hidden">
              <h3 className="px-4 pt-3 pb-2 text-[13px] font-bold">每日明细</h3>
              <div className="max-h-[260px] overflow-auto">
                <table className="w-full text-[12.5px]">
                  <thead className="text-[11px] text-dim bg-s2 sticky top-0">
                    <tr>
                      <th className="px-3 py-2 font-medium text-left">日期</th>
                      <th className="px-3 py-2 font-medium text-right">收入</th>
                      <th className="px-3 py-2 font-medium text-right">订单</th>
                      <th className="px-3 py-2 font-medium text-right">付费用户</th>
                      <th className="px-3 py-2 font-medium text-right">退款/拒付</th>
                    </tr>
                  </thead>
                  <tbody>
                    {[...r.daily].reverse().map((d) => (
                      <tr key={d.day} className="border-t border-white/5">
                        <td className="px-3 py-2 tabular-nums">{d.day}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{money(d.revenue_cents)}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{num(d.orders)}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{num(d.buyers)}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{d.reversed_cents ? money(d.reversed_cents) : "—"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </Card>
            <Card className="overflow-hidden">
              <h3 className="px-4 pt-3 pb-2 text-[13px] font-bold">金币消耗最多的剧集</h3>
              {r.top_series.length === 0 ? <div className="px-4 pb-4 text-[12.5px] text-dim">暂无解锁记录</div> : (
                <table className="w-full text-[12.5px]">
                  <thead className="text-[11px] text-dim bg-s2">
                    <tr>
                      <th className="px-3 py-2 font-medium text-left">剧集</th>
                      <th className="px-3 py-2 font-medium text-right">金币</th>
                      <th className="px-3 py-2 font-medium text-right">解锁集数</th>
                      <th className="px-3 py-2 font-medium text-right">购买人数</th>
                    </tr>
                  </thead>
                  <tbody>
                    {r.top_series.map((t) => (
                      <tr key={t.series_id} className="border-t border-white/5">
                        <td className="px-3 py-2">{t.title} <span className="text-dim">#{t.series_id}</span></td>
                        <td className="px-3 py-2 text-right tabular-nums">{num(t.coins)}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{num(t.unlocks)}</td>
                        <td className="px-3 py-2 text-right tabular-nums">{num(t.buyers)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Card>
          </div>
        </div>
      )}
    </>
  );
}

/** 订单与营收: revenue at the top, every order below, refunds through the order's payment provider. */
export function OrdersPage() {
  const qc = useQueryClient();
  const [q, setQ] = useState("");
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<OrderStatus | "all">("all");
  const [page, setPage] = useState(1);
  const [notice, setNotice] = useState<string | null>(null);
  const list = useQuery({
    queryKey: ["admin", "orders", query, status, page],
    queryFn: async () => unwrap(await api.GET("/api/admin/orders", {
      params: { query: { q: query || undefined, status: status === "all" ? undefined : status, page } },
    })),
    placeholderData: keepPreviousData,
  });
  const refund = useMutation({
    mutationFn: async ({ id, note }: { id: number; note: string | null }) =>
      unwrap(await api.POST("/api/admin/orders/{id}/refund", { params: { path: { id } }, body: { note } })),
    onSuccess: (r) => {
      setNotice(r.pending ? `已向 ${r.order.provider} 提交退款，渠道确认后订单会变为已退款。` : `订单 ${r.order.order_no} 已退款，对应金币或 VIP 天数已扣回。`);
      void qc.invalidateQueries({ queryKey: ["admin", "orders"] });
      void qc.invalidateQueries({ queryKey: ["admin", "revenue"] });
    },
  });

  const items = list.data?.items ?? [];
  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.size)) : 1;

  return (
    <>
      <PageHeader title="订单与营收"
        sub="订单保存下单时的商品价格与内容。只有支付渠道确认后才会到账；退款与拒付会扣回该订单发放的金币（已花掉的部分会让余额变为负数）或 VIP 天数，已解锁的剧集不受影响。" />

      <Revenue />

      <h2 className="text-[15px] font-bold mt-6 mb-3">订单</h2>
      <div className="flex flex-wrap items-center gap-3 mb-3">
        <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); setQuery(q.trim()); setPage(1); }}>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="订单号 / 渠道流水号 / 会员" className={`${inputClass} w-[260px]`} />
          <Button type="submit">搜索</Button>
        </form>
        <select value={status} onChange={(e) => { setStatus(e.target.value as OrderStatus | "all"); setPage(1); }} className={`${inputClass} w-[140px]`} aria-label="订单状态">
          <option value="all">全部状态</option>
          {(Object.keys(ORDER_STATUS) as OrderStatus[]).map((k) => <option key={k} value={k}>{ORDER_STATUS[k][0]}</option>)}
        </select>
        <span className="ml-auto text-[12px] text-dim">共 {list.data?.total ?? 0} 单</span>
      </div>
      {notice && <p className="mb-3 text-[12.5px] text-success">{notice}</p>}
      <div className="mb-3"><ErrorText error={refund.error} /></div>

      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : list.isError ? <div className="p-5"><ErrorText error={list.error} /></div> : items.length === 0 ? (
          <div className="py-12 text-center text-[13px] text-dim">没有订单</div>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">订单</th>
                <th className="px-4 py-2.5 font-medium">会员</th>
                <th className="px-4 py-2.5 font-medium">商品</th>
                <th className="px-4 py-2.5 font-medium text-right">金额</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">支付渠道</th>
                <th className="px-4 py-2.5 font-medium">支付时间</th>
                <th className="px-4 py-2.5 font-medium text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {items.map((o) => (
                <tr key={o.id} className="border-t border-white/5 align-top">
                  <td className="px-4 py-3">
                    <div className="font-mono text-[12px]">{o.order_no}</div>
                    <div className="text-[11.5px] text-dim whitespace-nowrap">{formatTime(o.created_at)} · {regionName(o.country)}</div>
                  </td>
                  <td className="px-4 py-3">
                    <div className="truncate max-w-[140px]">{o.nickname}</div>
                    <div className="text-[11.5px] text-dim">@{o.username} · #{o.user_id}</div>
                  </td>
                  <td className="px-4 py-3">
                    <div>{o.product_name}</div>
                    <div className="text-[11.5px] text-dim">{contents(o)}</div>
                  </td>
                  <td className="px-4 py-3 text-right tabular-nums whitespace-nowrap">{formatMoney(o.price_cents, o.currency)}</td>
                  <td className="px-4 py-3">
                    <OrderBadge status={o.status} />
                    {o.status_reason && <div className="text-[11px] text-dim mt-1 max-w-[160px]">{o.status_reason}</div>}
                  </td>
                  <td className="px-4 py-3">
                    <div>{o.provider}</div>
                    {o.provider_ref && <div className="text-[11px] text-dim font-mono truncate max-w-[160px]" title={o.provider_ref}>{o.provider_ref}</div>}
                  </td>
                  <td className="px-4 py-3 text-mut whitespace-nowrap">
                    {formatTime(o.paid_at)}
                    {o.reversed_at && <div className="text-[11px] text-dim">撤销于 {formatTime(o.reversed_at)}</div>}
                  </td>
                  <td className="px-4 py-3 text-right whitespace-nowrap">
                    {o.status === "paid" && (
                      <button disabled={refund.isPending} className="text-danger hover:underline"
                        onClick={() => {
                          const note = window.prompt(`退款 ${formatMoney(o.price_cents, o.currency)} 给 @${o.username}？将通过 ${o.provider} 原路退回，并扣回「${contents(o)}」。请填写原因：`, "");
                          if (note !== null) { setNotice(null); refund.mutate({ id: o.id, note: note.trim() || null }); }
                        }}>退款</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

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
