import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, Field, PageHeader, StatusBadge, formatTime, inputClass } from "../ui";

export function AdminsPage({ currentUserId }: { currentUserId: number }) {
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ["admin", "users"], queryFn: async () => unwrap(await api.GET("/api/admin/users")) });
  const refresh = () => void qc.invalidateQueries({ queryKey: ["admin", "users"] });

  const [username, setUsername] = useState("");
  const [nickname, setNickname] = useState("");
  const [password, setPassword] = useState("");
  const create = useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/admin/users", { body: { username, nickname, password } })),
    onSuccess: () => { setUsername(""); setNickname(""); setPassword(""); refresh(); },
  });

  const setStatus = useMutation({
    mutationFn: async ({ id, active }: { id: number; active: boolean }) => {
      const r = await api.POST("/api/admin/users/{id}/status", { params: { path: { id } }, body: { active } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: refresh,
  });
  const resetPassword = useMutation({
    mutationFn: async ({ id, password }: { id: number; password: string }) => {
      const r = await api.POST("/api/admin/users/{id}/password", { params: { path: { id } }, body: { password } });
      if (!r.response.ok) unwrap(r);
    },
  });

  function submit(e: FormEvent) {
    e.preventDefault();
    create.mutate();
  }

  return (
    <>
      <PageHeader title="管理员" sub="每位成员（包括法务）使用自己的账号，操作日志会记录到个人。停用账号会立即结束其登录。" />

      <Card className="p-5 mb-4">
        <form onSubmit={submit} className="flex flex-wrap items-end gap-3">
          <Field label="账号"><input required pattern="[A-Za-z0-9_.\-]{3,32}" value={username} onChange={(e) => setUsername(e.target.value)} className={inputClass} /></Field>
          <Field label="显示名称"><input value={nickname} onChange={(e) => setNickname(e.target.value)} placeholder="法务-张三" className={inputClass} /></Field>
          <Field label="初始密码（至少 10 位）"><input required type="password" minLength={10} value={password} onChange={(e) => setPassword(e.target.value)} className={inputClass} /></Field>
          <Button type="submit" variant="primary" disabled={create.isPending}>添加管理员</Button>
        </form>
        <div className="mt-2"><ErrorText error={create.error} /></div>
      </Card>

      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">账号</th>
                <th className="px-4 py-2.5 font-medium">显示名称</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">最近登录</th>
                <th className="px-4 py-2.5 font-medium text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {list.data?.map((u) => (
                <tr key={u.id} className="border-t border-white/5">
                  <td className="px-4 py-3 font-medium">{u.username}{u.id === currentUserId && <span className="ml-2 text-[11px] text-dim">（你）</span>}</td>
                  <td className="px-4 py-3 text-mut">{u.nickname}</td>
                  <td className="px-4 py-3"><StatusBadge status={u.status} /></td>
                  <td className="px-4 py-3 text-mut">{formatTime(u.last_login_at)}</td>
                  <td className="px-4 py-3 text-right whitespace-nowrap">
                    <button className="text-primary hover:underline" onClick={() => {
                      const pw = window.prompt(`为 ${u.username} 设置新密码（至少 10 位）`);
                      if (pw) resetPassword.mutate({ id: u.id, password: pw });
                    }}>重置密码</button>
                    {u.id !== currentUserId && (
                      <button className={`ml-4 hover:underline ${u.status === "active" ? "text-danger" : "text-success"}`}
                        onClick={() => setStatus.mutate({ id: u.id, active: u.status !== "active" })}>
                        {u.status === "active" ? "停用" : "启用"}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
      <div className="mt-2"><ErrorText error={setStatus.error ?? resetPassword.error} /></div>
      {resetPassword.isSuccess && <p className="mt-2 text-[12.5px] text-success">密码已更新</p>}
    </>
  );
}
