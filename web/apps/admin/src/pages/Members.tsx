import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, PageHeader, formatTime, inputClass } from "../ui";
import { MemberWalletDialog } from "./MemberWallet";

type Status = Schemas["UserStatus"] | "all";

/** 会员: viewer accounts. Disabling one signs it out on every device at once. */
export function MembersPage() {
  const qc = useQueryClient();
  const [q, setQ] = useState("");
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<Status>("all");
  const [page, setPage] = useState(1);
  const [walletOf, setWalletOf] = useState<{ id: number; username: string; nickname: string } | null>(null);
  const list = useQuery({
    queryKey: ["admin", "members", query, status, page],
    queryFn: async () => unwrap(await api.GET("/api/admin/members", {
      params: { query: { q: query || undefined, status: status === "all" ? undefined : status, page } },
    })),
  });
  const setActive = useMutation({
    mutationFn: async ({ id, active, note }: { id: number; active: boolean; note: string | null }) => {
      const r = await api.POST("/api/admin/members/{id}/status", { params: { path: { id } }, body: { active, note } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["admin", "members"] }),
  });

  const setAgeVerified = useMutation({
    mutationFn: async ({ id, verified, note }: { id: number; verified: boolean; note: string | null }) => {
      const r = await api.POST("/api/admin/members/{id}/age-verification", { params: { path: { id } }, body: { verified, note } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["admin", "members"] }),
  });

  const items = list.data?.items ?? [];
  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.size)) : 1;

  return (
    <>
      <PageHeader title="会员" sub="观众账号。封禁会立即让该账号在所有设备上退出，且无法再登录；解封后可正常使用。" />

      <div className="flex flex-wrap items-center gap-3 mb-3">
        <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); setQuery(q.trim()); setPage(1); }}>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="账号或昵称" className={`${inputClass} w-[220px]`} />
          <Button type="submit">搜索</Button>
        </form>
        <div className="flex gap-1 p-1 rounded-(--r-ctl) bg-s2">
          {([["all", "全部"], ["active", "正常"], ["disabled", "已封禁"]] as const).map(([k, label]) => (
            <button key={k} onClick={() => { setStatus(k); setPage(1); }}
              className={`h-8 px-4 rounded-(--r-ctl) text-[12.5px] font-semibold ${status === k ? "bg-s4 text-on" : "text-mut hover:text-on"}`}>{label}</button>
          ))}
        </div>
        <span className="ml-auto text-[12px] text-dim">共 {list.data?.total ?? 0} 人</span>
      </div>
      <div className="mb-3"><ErrorText error={setActive.error ?? setAgeVerified.error} /></div>

      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : list.isError ? (
          <div className="p-5"><ErrorText error={list.error} /></div>
        ) : items.length === 0 ? (
          <div className="py-12 text-center text-[13px] text-dim">没有找到会员</div>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">会员</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">年龄验证</th>
                <th className="px-4 py-2.5 font-medium">注册时间</th>
                <th className="px-4 py-2.5 font-medium">最近登录</th>
                <th className="px-4 py-2.5 font-medium text-right">评论</th>
                <th className="px-4 py-2.5 font-medium text-right">被举报</th>
                <th className="px-4 py-2.5 font-medium text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {items.map((m) => (
                <tr key={m.id} className="border-t border-white/5">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2.5">
                      <span className="w-7 h-7 rounded-full flex items-center justify-center text-[12px] font-bold text-white shrink-0" style={{ background: m.avatar_color }}>
                        {Array.from(m.nickname)[0] ?? "?"}
                      </span>
                      <div className="min-w-0">
                        <div className="truncate">{m.nickname}</div>
                        <div className="text-[11.5px] text-dim">@{m.username} · #{m.id}</div>
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <span className={`inline-block px-2 py-0.5 rounded-md text-[11px] font-semibold ${m.status === "active" ? "bg-success/20 text-success" : "bg-danger/20 text-danger"}`}>
                      {m.status === "active" ? "正常" : "已封禁"}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    {m.age_verified_at
                      ? <span className="text-success text-[12px]" title={formatTime(m.age_verified_at)}>已验证</span>
                      : <span className="text-dim text-[12px]">未验证</span>}
                  </td>
                  <td className="px-4 py-3 text-mut whitespace-nowrap">{formatTime(m.created_at)}</td>
                  <td className="px-4 py-3 text-mut whitespace-nowrap">{formatTime(m.last_login_at)}</td>
                  <td className="px-4 py-3 text-right tabular-nums">{m.comment_count}</td>
                  <td className={`px-4 py-3 text-right tabular-nums ${m.report_count > 0 ? "text-star" : ""}`}>{m.report_count}</td>
                  <td className="px-4 py-3 text-right whitespace-nowrap">
                    <button className="mr-4 text-primary hover:underline" onClick={() => setWalletOf(m)}>钱包</button>
                    <button disabled={setAgeVerified.isPending} className="mr-4 text-primary hover:underline"
                      onClick={() => {
                        const verified = !m.age_verified_at;
                        const note = window.prompt(verified
                          ? `确认已核验 ${m.username} 年满 18 周岁？请填写核验方式（会记录在操作日志）：`
                          : `撤销 ${m.username} 的年龄验证？严格验证地区将无法再观看。可填写原因：`, "");
                        if (note !== null) setAgeVerified.mutate({ id: m.id, verified, note: note.trim() || null });
                      }}>
                      {m.age_verified_at ? "撤销验证" : "标记已验证"}
                    </button>
                    <button disabled={setActive.isPending}
                      className={`hover:underline ${m.status === "active" ? "text-danger" : "text-success"}`}
                      onClick={() => {
                        const active = m.status !== "active";
                        const note = window.prompt(active ? `解封 ${m.username}？可填写备注：` : `封禁 ${m.username}？该账号会立即在所有设备上退出。可填写原因：`, "");
                        if (note !== null) setActive.mutate({ id: m.id, active, note: note.trim() || null });
                      }}>
                      {m.status === "active" ? "封禁" : "解封"}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {walletOf && <MemberWalletDialog member={walletOf} onClose={() => setWalletOf(null)} />}

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
