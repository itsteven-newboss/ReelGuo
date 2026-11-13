import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { api } from "../session";
import { Button, Dialog, ErrorText, Field, formatMoney, formatTime, inputClass } from "../ui";
import { OrderBadge } from "./Orders";
import { contents } from "./Products";

const KIND: Record<string, string> = { purchase: "充值", unlock: "解锁", refund: "退款", chargeback: "拒付", adjustment: "客服调整" };
const signed = (n: number) => (n > 0 ? `+${n.toLocaleString("en-US")}` : n.toLocaleString("en-US"));

/** A member's wallet: balances, a support adjustment form, the ledger and recent orders. */
export function MemberWalletDialog({ member, onClose }: { member: { id: number; username: string; nickname: string }; onClose: () => void }) {
  const qc = useQueryClient();
  const wallet = useQuery({
    queryKey: ["admin", "member-wallet", member.id],
    queryFn: async () => unwrap(await api.GET("/api/admin/members/{id}/wallet", { params: { path: { id: member.id } } })),
  });
  const [coins, setCoins] = useState("0");
  const [bonus, setBonus] = useState("0");
  const [vipDays, setVipDays] = useState("0");
  const [note, setNote] = useState("");
  const adjust = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/admin/members/{id}/wallet/adjust", {
      params: { path: { id: member.id } },
      body: { coins: Number(coins) || 0, bonus_coins: Number(bonus) || 0, vip_days: Number(vipDays) || 0, note: note.trim() },
    })),
    onSuccess: () => {
      setCoins("0");
      setBonus("0");
      setVipDays("0");
      setNote("");
      void qc.invalidateQueries({ queryKey: ["admin", "member-wallet", member.id] });
      void qc.invalidateQueries({ queryKey: ["admin", "members"] });
    },
  });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const summary = [Number(coins) && `金币 ${signed(Number(coins))}`, Number(bonus) && `赠送金币 ${signed(Number(bonus))}`, Number(vipDays) && `VIP ${signed(Number(vipDays))} 天`]
      .filter(Boolean).join("、");
    if (summary && window.confirm(`确认为 @${member.username} 调整：${summary}？`)) adjust.mutate();
  };

  const w = wallet.data;
  return (
    <Dialog open title={`钱包 · ${member.nickname}（@${member.username}）`} onClose={onClose} width={860}>
      {wallet.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : wallet.isError || !w ? <ErrorText error={wallet.error} /> : (
        <>
          <div className="grid grid-cols-4 gap-3 text-[12px]">
            <Stat label="金币余额" value={w.wallet.balance.toLocaleString("en-US")} sub={`购买 ${w.wallet.coins.toLocaleString("en-US")} · 赠送 ${w.wallet.bonus_coins.toLocaleString("en-US")}`}
              danger={w.wallet.coins < 0} />
            <Stat label="VIP" value={w.wallet.vip_active ? "有效" : "未开通"} sub={w.wallet.vip_active ? `至 ${formatTime(w.wallet.vip_until)}` : " "} />
            <Stat label="已解锁剧集" value={w.unlocks.toLocaleString("en-US")} sub={w.wallet.auto_unlock ? "已开启自动解锁" : "未开启自动解锁"} />
            <Stat label="已支付订单" value={w.paid_orders.toLocaleString("en-US")} sub={w.orders[0] ? `累计 ${formatMoney(w.spent_cents, w.orders[0].currency)}` : " "} />
          </div>

          <form onSubmit={submit} className="mt-5 rounded-(--r-md) bg-s2 p-4 grid grid-cols-4 gap-3">
            <div className="col-span-4 text-[13px] font-semibold">客服调整<span className="ml-2 text-[11.5px] text-dim font-normal">正数发放、负数扣除；会员的消费记录显示为「客服调整」，原因记入操作日志</span></div>
            <Field label="金币"><input type="number" value={coins} onChange={(e) => setCoins(e.target.value)} className={inputClass} /></Field>
            <Field label="赠送金币"><input type="number" value={bonus} onChange={(e) => setBonus(e.target.value)} className={inputClass} /></Field>
            <Field label="VIP 天数"><input type="number" value={vipDays} onChange={(e) => setVipDays(e.target.value)} className={inputClass} /></Field>
            <Field label="原因（必填）"><input required maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} placeholder="如：播放故障补偿" className={inputClass} /></Field>
            <div className="col-span-4 flex items-center gap-3">
              <Button type="submit" variant="primary" disabled={adjust.isPending || !note.trim()}>{adjust.isPending ? "提交中…" : "确认调整"}</Button>
              <ErrorText error={adjust.error} />
            </div>
          </form>

          <h3 className="mt-5 mb-2 text-[13px] font-bold">消费记录（最近 50 条）</h3>
          <div className="rounded-(--r-md) bg-s2 overflow-hidden">
            {w.entries.length === 0 ? <div className="py-6 text-center text-[12.5px] text-dim">暂无记录</div> : (
              <table className="w-full text-[12.5px]">
                <thead className="text-left text-[11px] text-dim bg-s3">
                  <tr>
                    <th className="px-3 py-2 font-medium">时间</th>
                    <th className="px-3 py-2 font-medium">类型</th>
                    <th className="px-3 py-2 font-medium">说明</th>
                    <th className="px-3 py-2 font-medium text-right">金币</th>
                    <th className="px-3 py-2 font-medium text-right">赠送</th>
                    <th className="px-3 py-2 font-medium text-right">VIP</th>
                    <th className="px-3 py-2 font-medium text-right">余额</th>
                  </tr>
                </thead>
                <tbody>
                  {w.entries.map((e) => (
                    <tr key={e.id} className="border-t border-white/5 align-top">
                      <td className="px-3 py-2 text-mut whitespace-nowrap">{formatTime(e.at)}</td>
                      <td className="px-3 py-2 whitespace-nowrap">{KIND[e.kind] ?? e.kind}</td>
                      <td className="px-3 py-2">
                        {e.description}
                        {(e.order_no || e.actor || e.note) && (
                          <div className="text-[11px] text-dim">{[e.order_no, e.actor, e.note].filter(Boolean).join(" · ")}</div>
                        )}
                      </td>
                      <td className="px-3 py-2 text-right tabular-nums">{e.coins ? signed(e.coins) : ""}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{e.bonus_coins ? signed(e.bonus_coins) : ""}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{e.vip_days ? `${signed(e.vip_days)} 天` : ""}</td>
                      <td className="px-3 py-2 text-right tabular-nums text-mut">{(e.coins_after + e.bonus_after).toLocaleString("en-US")}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          <h3 className="mt-5 mb-2 text-[13px] font-bold">最近订单</h3>
          <div className="rounded-(--r-md) bg-s2 overflow-hidden">
            {w.orders.length === 0 ? <div className="py-6 text-center text-[12.5px] text-dim">暂无订单</div> : (
              <table className="w-full text-[12.5px]">
                <tbody>
                  {w.orders.map((o) => (
                    <tr key={o.id} className="border-t border-white/5 first:border-0">
                      <td className="px-3 py-2 font-mono text-[11.5px]">{o.order_no}</td>
                      <td className="px-3 py-2">{o.product_name} <span className="text-dim">{contents(o)}</span></td>
                      <td className="px-3 py-2 text-right tabular-nums">{formatMoney(o.price_cents, o.currency)}</td>
                      <td className="px-3 py-2"><OrderBadge status={o.status} /></td>
                      <td className="px-3 py-2 text-mut whitespace-nowrap">{formatTime(o.created_at)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </>
      )}
    </Dialog>
  );
}

function Stat({ label, value, sub, danger }: { label: string; value: string; sub: string; danger?: boolean }) {
  return (
    <div className="rounded-(--r-md) bg-s2 p-3 min-w-0">
      <div className="text-mut">{label}</div>
      <div className={`mt-1 text-[18px] font-semibold tabular-nums ${danger ? "text-danger" : "text-on"}`}>{value}</div>
      <div className="mt-0.5 text-[11px] text-dim truncate">{sub}</div>
    </div>
  );
}
