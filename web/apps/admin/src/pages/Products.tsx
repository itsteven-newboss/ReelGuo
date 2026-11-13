import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState, type FormEvent } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, Field, PageHeader, formatMoney, inputClass } from "../ui";

type Product = Schemas["AdminProductDto"];
type Kind = Schemas["ProductKind"];

type FormState = { kind: Kind; name: string; price: string; currency: string; coins: string; bonus: string; vipDays: string; badge: string; sort: string; active: boolean };

const EMPTY: FormState = { kind: "coins", name: "", price: "", currency: "USD", coins: "", bonus: "0", vipDays: "0", badge: "", sort: "100", active: true };

const toForm = (p: Product): FormState => ({
  kind: p.kind, name: p.name, price: (p.price_cents / 100).toFixed(2), currency: p.currency, coins: String(p.coins), bonus: String(p.bonus_coins),
  vipDays: String(p.vip_days), badge: p.badge ?? "", sort: String(p.sort_order), active: p.active,
});

const toInput = (f: FormState) => ({
  kind: f.kind, name: f.name.trim(), price_cents: Math.round(Number(f.price) * 100), currency: f.currency.trim().toUpperCase(),
  coins: f.kind === "coins" ? Number(f.coins) || 0 : Number(f.coins) || 0, bonus_coins: Number(f.bonus) || 0,
  vip_days: f.kind === "vip" ? Number(f.vipDays) || 0 : 0, badge: f.badge.trim() || null, active: f.active, sort_order: Number(f.sort) || 0,
});

export function contents(p: { kind: Kind; coins: number; bonus_coins: number; vip_days: number }) {
  const parts: string[] = [];
  if (p.vip_days) parts.push(`VIP ${p.vip_days} 天`);
  if (p.coins) parts.push(`${p.coins.toLocaleString("en-US")} 金币`);
  if (p.bonus_coins) parts.push(`赠送 ${p.bonus_coins.toLocaleString("en-US")}`);
  return parts.join(" + ") || "—";
}

/** 商品: coin packs and VIP passes sold in the app's 充值中心. */
export function ProductsPage() {
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ["admin", "products"], queryFn: async () => unwrap(await api.GET("/api/admin/products")) });
  const [editing, setEditing] = useState<number | "new" | null>(null);
  const [form, setForm] = useState<FormState>(EMPTY);
  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => setForm((f) => ({ ...f, [k]: v }));

  const save = useMutation({
    mutationFn: async () => editing === "new"
      ? unwrap(await api.POST("/api/admin/products", { body: toInput(form) }))
      : unwrap(await api.PUT("/api/admin/products/{id}", { params: { path: { id: editing as number } }, body: toInput(form) })),
    onSuccess: () => {
      setEditing(null);
      void qc.invalidateQueries({ queryKey: ["admin", "products"] });
    },
  });

  const open = (p: Product | null) => {
    save.reset();
    setForm(p ? toForm(p) : EMPTY);
    setEditing(p ? p.id : "new");
  };
  const submit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };

  return (
    <>
      <PageHeader title="商品"
        sub="充值中心出售的金币礼包与 VIP 会员。修改只影响之后的新订单（订单会保存下单时的价格与内容）；停售的商品不再出现在充值中心。VIP 为一次性购买，不自动续费。"
        actions={<Button variant="primary" onClick={() => open(null)}><span className="inline-flex items-center gap-1.5"><Plus size={15} />新增商品</span></Button>} />

      {editing !== null && (
        <Card className="p-5 mb-4">
          <form onSubmit={submit} className="grid grid-cols-4 gap-4">
            <div className="col-span-4 text-[14px] font-semibold">{editing === "new" ? "新增商品" : `编辑商品 #${editing}`}</div>
            <Field label="类型">
              <select value={form.kind} onChange={(e) => set("kind", e.target.value as Kind)} className={inputClass}>
                <option value="coins">金币礼包</option>
                <option value="vip">VIP 会员</option>
              </select>
            </Field>
            <Field label="名称" hint="显示在充值中心，最多 32 字" className="col-span-2">
              <input required maxLength={32} value={form.name} onChange={(e) => set("name", e.target.value)} placeholder={form.kind === "vip" ? "VIP 月卡" : "500 金币"} className={inputClass} />
            </Field>
            <Field label="角标" hint="如 热门、超值，可留空">
              <input maxLength={8} value={form.badge} onChange={(e) => set("badge", e.target.value)} className={inputClass} />
            </Field>
            <Field label="价格">
              <input required type="number" min={0.01} step={0.01} value={form.price} onChange={(e) => set("price", e.target.value)} placeholder="4.99" className={inputClass} />
            </Field>
            <Field label="币种" hint="须与支付配置的币种一致">
              <input required maxLength={3} value={form.currency} onChange={(e) => set("currency", e.target.value)} className={inputClass} />
            </Field>
            {form.kind === "vip" ? (
              <Field label="VIP 天数"><input required type="number" min={1} max={3660} value={form.vipDays} onChange={(e) => set("vipDays", e.target.value)} className={inputClass} /></Field>
            ) : (
              <Field label="金币"><input required type="number" min={1} value={form.coins} onChange={(e) => set("coins", e.target.value)} className={inputClass} /></Field>
            )}
            <Field label="赠送金币" hint="先于购买的金币消耗">
              <input type="number" min={0} value={form.bonus} onChange={(e) => set("bonus", e.target.value)} className={inputClass} />
            </Field>
            <Field label="排序" hint="小的在前"><input type="number" value={form.sort} onChange={(e) => set("sort", e.target.value)} className={inputClass} /></Field>
            <label className="flex items-center gap-2 text-[13px] self-end h-9"><input type="checkbox" checked={form.active} onChange={(e) => set("active", e.target.checked)} />在售</label>
            <div className="col-span-4 flex items-center gap-3">
              <Button type="submit" variant="primary" disabled={save.isPending}>{save.isPending ? "保存中…" : "保存"}</Button>
              <Button variant="ghost" onClick={() => setEditing(null)}>取消</Button>
              <ErrorText error={save.error} />
            </div>
          </form>
        </Card>
      )}

      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : list.isError ? <div className="p-5"><ErrorText error={list.error} /></div> : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">商品</th>
                <th className="px-4 py-2.5 font-medium">内容</th>
                <th className="px-4 py-2.5 font-medium text-right">价格</th>
                <th className="px-4 py-2.5 font-medium text-right">排序</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium text-right">已售</th>
                <th className="px-4 py-2.5 font-medium text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((p) => (
                <tr key={p.id} className={`border-t border-white/5 ${p.active ? "" : "text-mut"}`}>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      {p.name}
                      {p.badge && <span className="px-1.5 py-0.5 rounded bg-pc text-on-pc text-[10.5px] font-semibold">{p.badge}</span>}
                    </div>
                    <div className="text-[11.5px] text-dim">{p.kind === "vip" ? "VIP 会员" : "金币礼包"} · #{p.id}</div>
                  </td>
                  <td className="px-4 py-3">{contents(p)}</td>
                  <td className="px-4 py-3 text-right tabular-nums whitespace-nowrap">{formatMoney(p.price_cents, p.currency)}</td>
                  <td className="px-4 py-3 text-right tabular-nums text-mut">{p.sort_order}</td>
                  <td className="px-4 py-3">
                    <span className={`inline-block px-2 py-0.5 rounded-md text-[11px] font-semibold ${p.active ? "bg-success/20 text-success" : "bg-s4 text-mut"}`}>{p.active ? "在售" : "停售"}</span>
                  </td>
                  <td className="px-4 py-3 text-right tabular-nums">{p.paid_orders.toLocaleString("en-US")}</td>
                  <td className="px-4 py-3 text-right"><button className="text-primary hover:underline" onClick={() => open(p)}>编辑</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}
