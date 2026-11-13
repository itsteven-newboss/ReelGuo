import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Pin } from "lucide-react";
import { useEffect, useState } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, PageHeader, StatusBadge, formatTime, inputClass } from "../ui";

type Status = Schemas["CommentStatus"];
type Row = Schemas["AdminCommentDto"];

const TABS: [Status, string][] = [["pending", "待审核"], ["approved", "已通过"], ["rejected", "已拒绝"]];

/** 评论审核: the queue (oldest first), bulk approve / reject, and the sensitive-term list. */
export function CommentsPage() {
  const qc = useQueryClient();
  const [status, setStatus] = useState<Status>("pending");
  const [q, setQ] = useState("");
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [reason, setReason] = useState("");

  const list = useQuery({
    queryKey: ["admin", "comments", status, query, page],
    queryFn: async () => unwrap(await api.GET("/api/admin/comments", { params: { query: { status, q: query || undefined, page } } })),
  });
  useEffect(() => setSelected(new Set()), [status, query, page]);
  const refresh = () => {
    setSelected(new Set());
    void qc.invalidateQueries({ queryKey: ["admin", "comments"] });
    void qc.invalidateQueries({ queryKey: ["admin", "moderation-summary"] });
  };

  const moderate = useMutation({
    mutationFn: async ({ ids, decision }: { ids: number[]; decision: Schemas["CommentDecision"] }) =>
      unwrap(await api.POST("/api/admin/comments/moderate", { body: { ids, decision, reason: reason.trim() || null } })),
    onSuccess: refresh,
  });
  const pin = useMutation({
    mutationFn: async ({ id, pinned }: { id: number; pinned: boolean }) => {
      const r = await api.POST("/api/admin/comments/{id}/pin", { params: { path: { id } }, body: { pinned } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: refresh,
  });

  const items = list.data?.items ?? [];
  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.size)) : 1;
  const allSelected = items.length > 0 && items.every((c) => selected.has(c.id));
  const toggle = (id: number) => setSelected((s) => {
    const next = new Set(s);
    if (!next.delete(id)) next.add(id);
    return next;
  });

  return (
    <>
      <PageHeader title="评论审核"
        sub="新评论默认进入待审核，通过后才对其他人可见；作者自己能看到“审核中”。命中敏感词的评论会被自动拒绝，作者不会收到提示。" />

      <div className="flex flex-wrap items-center gap-3 mb-3">
        <div className="flex gap-1 p-1 rounded-(--r-ctl) bg-s2">
          {TABS.map(([k, label]) => (
            <button key={k} onClick={() => { setStatus(k); setPage(1); }}
              className={`h-8 px-4 rounded-(--r-ctl) text-[12.5px] font-semibold ${status === k ? "bg-s4 text-on" : "text-mut hover:text-on"}`}>{label}</button>
          ))}
        </div>
        <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); setQuery(q.trim()); setPage(1); }}>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="搜索内容或账号" className={`${inputClass} w-[220px]`} />
          <Button type="submit">搜索</Button>
        </form>
        <span className="ml-auto text-[12px] text-dim">共 {list.data?.total ?? 0} 条</span>
      </div>

      {status !== "approved" || selected.size > 0 ? (
        <Card className="p-3 mb-3 flex flex-wrap items-center gap-2">
          <span className="text-[12.5px] text-mut mr-1">已选 {selected.size} 条</span>
          <Button variant="primary" disabled={!selected.size || moderate.isPending}
            onClick={() => moderate.mutate({ ids: [...selected], decision: "approve" })}>通过</Button>
          <Button variant="danger" disabled={!selected.size || moderate.isPending}
            onClick={() => moderate.mutate({ ids: [...selected], decision: "reject" })}>拒绝</Button>
          <input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={200} placeholder="拒绝原因（可选，默认“不符合社区规范”）"
            className={`${inputClass} flex-1 min-w-[240px]`} />
          <ErrorText error={moderate.error ?? pin.error} />
        </Card>
      ) : null}

      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : list.isError ? (
          <div className="p-5"><ErrorText error={list.error} /></div>
        ) : items.length === 0 ? (
          <div className="py-12 text-center text-[13px] text-dim">{status === "pending" ? "没有待审核的评论" : "暂无记录"}</div>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="pl-4 py-2.5 w-8">
                  <input type="checkbox" aria-label="全选" checked={allSelected}
                    onChange={() => setSelected(allSelected ? new Set() : new Set(items.map((c) => c.id)))} />
                </th>
                <th className="px-3 py-2.5 font-medium">评论</th>
                <th className="px-3 py-2.5 font-medium">位置</th>
                <th className="px-3 py-2.5 font-medium">作者</th>
                <th className="px-3 py-2.5 font-medium">时间</th>
                <th className="px-4 py-2.5 font-medium text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {items.map((c) => <CommentRow key={c.id} c={c} checked={selected.has(c.id)} onCheck={() => toggle(c.id)}
                busy={moderate.isPending || pin.isPending}
                onDecide={(decision) => moderate.mutate({ ids: [c.id], decision })}
                onPin={(pinned) => pin.mutate({ id: c.id, pinned })} />)}
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

      <TermsEditor />
    </>
  );
}

function CommentRow({ c, checked, onCheck, busy, onDecide, onPin }: {
  c: Row; checked: boolean; onCheck: () => void; busy: boolean;
  onDecide: (d: Schemas["CommentDecision"]) => void; onPin: (pinned: boolean) => void;
}) {
  return (
    <tr className={`border-t border-white/5 align-top ${checked ? "bg-sec/40" : ""}`}>
      <td className="pl-4 py-3"><input type="checkbox" aria-label={`选择评论 ${c.id}`} checked={checked} onChange={onCheck} /></td>
      <td className="px-3 py-3 max-w-[420px]">
        <div className="flex items-center gap-1.5 mb-1">
          <StatusBadge status={c.status} />
          {c.parent_id && <span className="text-[11px] text-dim">回复 #{c.parent_id}</span>}
          {c.pinned && <span className="text-[11px] text-primary flex items-center gap-0.5"><Pin size={11} />置顶</span>}
        </div>
        <p className="whitespace-pre-wrap break-words leading-relaxed">{c.text}</p>
        {c.reject_reason && <p className="mt-1 text-[11.5px] text-danger">原因：{c.reject_reason}</p>}
        {c.moderated_by && <p className="mt-0.5 text-[11px] text-dim">{c.moderated_by === "system" ? "敏感词自动处理" : c.moderated_by} · {formatTime(c.moderated_at)}</p>}
      </td>
      <td className="px-3 py-3 text-mut">
        <div className="max-w-[200px] truncate">{c.target_type === "series" ? "剧集" : "文章"} · {c.target_title}</div>
        {c.target_type === "series" && <div className="text-[11.5px] text-dim">第 {c.episode_number} 集</div>}
      </td>
      <td className="px-3 py-3">
        <div>{c.nickname}</div>
        <div className="text-[11.5px] text-dim">@{c.username} · {c.country}</div>
      </td>
      <td className="px-3 py-3 text-mut whitespace-nowrap">{formatTime(c.created_at)}</td>
      <td className="px-4 py-3 text-right whitespace-nowrap">
        {c.status !== "approved" && <button disabled={busy} className="text-success hover:underline" onClick={() => onDecide("approve")}>通过</button>}
        {c.status !== "rejected" && <button disabled={busy} className="ml-3 text-danger hover:underline" onClick={() => onDecide("reject")}>拒绝</button>}
        {c.status === "approved" && !c.parent_id && (
          <button disabled={busy} className="ml-3 text-primary hover:underline" onClick={() => onPin(!c.pinned)}>{c.pinned ? "取消置顶" : "置顶"}</button>
        )}
      </td>
    </tr>
  );
}

function TermsEditor() {
  const qc = useQueryClient();
  const terms = useQuery({ queryKey: ["admin", "moderation-terms"], queryFn: async () => unwrap(await api.GET("/api/admin/moderation/terms")) });
  const [text, setText] = useState<string | null>(null);
  const value = text ?? terms.data?.terms.join("\n") ?? "";
  const save = useMutation({
    mutationFn: async () => unwrap(await api.PUT("/api/admin/moderation/terms", { body: { terms: value.split(/\r?\n/) } })),
    onSuccess: (data) => {
      qc.setQueryData(["admin", "moderation-terms"], data);
      setText(null);
    },
  });
  const count = value.split(/\r?\n/).filter((t) => t.trim()).length;

  return (
    <Card className="p-5 mt-6">
      <div className="flex items-center gap-3 mb-2">
        <h2 className="text-[15px] font-bold">敏感词</h2>
        <span className="text-[12px] text-dim">{count} 个 · 每行一个，不区分大小写，包含即命中</span>
        <div className="ml-auto flex gap-2">
          {text !== null && <Button variant="ghost" onClick={() => setText(null)}>撤销修改</Button>}
          <Button variant="primary" disabled={text === null || save.isPending} onClick={() => save.mutate()}>保存</Button>
        </div>
      </div>
      {terms.isPending ? <div className="flex justify-center py-6"><Spinner /></div> : (
        <textarea value={value} onChange={(e) => setText(e.target.value)} rows={8} spellCheck={false}
          placeholder={"例如：\n加微信\n私聊约"}
          className="w-full px-3 py-2 rounded-(--r-sm) bg-s3 text-on text-[13px] font-mono outline-none focus:ring-2 ring-primary placeholder:text-dim" />
      )}
      <div className="mt-2"><ErrorText error={save.error} /></div>
      {save.isSuccess && text === null && <p className="mt-1 text-[12px] text-success">已保存，1 分钟内对新评论生效。</p>}
    </Card>
  );
}
