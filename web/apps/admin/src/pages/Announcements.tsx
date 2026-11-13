import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, PageHeader, StatusBadge, inputClass } from "../ui";

type Announcement = Schemas["AdminAnnouncementDto"];

const KEY = ["admin", "announcements"] as const;

/** 公告: the scrolling line under the home-page banner. Every active entry is shown, in sort order. */
export function AnnouncementsPage() {
  const list = useQuery({ queryKey: KEY, queryFn: async () => unwrap(await api.GET("/api/admin/announcements")) });
  const [adding, setAdding] = useState(false);
  return (
    <>
      <PageHeader title="公告"
        sub="首页横幅下方的滚动公告栏。所有「启用」的公告按排序依次滚动显示；没有启用的公告时整条公告栏隐藏。链接可填站内路径（以 / 开头）或 https 地址。"
        actions={!adding && <Button variant="primary" onClick={() => setAdding(true)}><span className="flex items-center gap-1.5"><Plus size={15} />新增公告</span></Button>} />
      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">内容</th>
                <th className="px-4 py-2.5 font-medium">链接</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">排序</th>
                <th className="px-4 py-2.5" />
              </tr>
            </thead>
            <tbody>
              {adding && <Row item={null} onDone={() => setAdding(false)} />}
              {list.data?.map((a) => <Row key={a.id} item={a} />)}
              {!adding && list.data?.length === 0 && (
                <tr><td colSpan={5} className="px-4 py-10 text-center text-mut">还没有公告</td></tr>
              )}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}

function Row({ item, onDone }: { item: Announcement | null; onDone?: () => void }) {
  const qc = useQueryClient();
  const [edit, setEdit] = useState(item === null);
  const [text, setText] = useState(item?.text ?? "");
  const [url, setUrl] = useState(item?.url ?? "");
  const [active, setActive] = useState(item?.active ?? true);
  const [sort, setSort] = useState(item?.sort_order ?? 0);

  const done = () => { setEdit(false); onDone?.(); };
  const save = useMutation({
    mutationFn: async () => {
      const body = { text: text.trim(), url: url.trim() || null, active, sort_order: sort };
      return item
        ? unwrap(await api.PUT("/api/admin/announcements/{id}", { params: { path: { id: item.id } }, body }))
        : unwrap(await api.POST("/api/admin/announcements", { body }));
    },
    onSuccess: async () => { await qc.invalidateQueries({ queryKey: KEY }); done(); },
  });
  const remove = useMutation({
    mutationFn: async () => {
      const r = await api.DELETE("/api/admin/announcements/{id}", { params: { path: { id: item!.id } } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });

  if (!edit && item) {
    return (
      <tr className="border-t border-white/5">
        <td className="px-4 py-3 max-w-[460px]">{item.text}</td>
        <td className="px-4 py-3 text-mut font-mono text-[12px] truncate max-w-[200px]">{item.url || "—"}</td>
        <td className="px-4 py-3"><StatusBadge status={item.active ? "active" : "hidden"} /></td>
        <td className="px-4 py-3 text-mut">{item.sort_order}</td>
        <td className="px-4 py-3 text-right whitespace-nowrap">
          <button onClick={() => setEdit(true)} className="text-primary hover:underline">编辑</button>
          <button onClick={() => window.confirm("确定删除这条公告？") && remove.mutate()} className="ml-4 text-danger hover:underline">删除</button>
          <ErrorText error={remove.error} />
        </td>
      </tr>
    );
  }

  return (
    <tr className="border-t border-white/5 bg-s2/50 align-top">
      <td className="px-4 py-3">
        <input value={text} maxLength={200} onChange={(e) => setText(e.target.value)} placeholder="公告内容（最多 200 字）" className={`${inputClass} w-full`} autoFocus />
      </td>
      <td className="px-4 py-3"><input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="/topics 或 https://…" className={`${inputClass} w-full`} /></td>
      <td className="px-4 py-3">
        <label className="flex items-center gap-1.5 h-9 whitespace-nowrap">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} className="accent-[var(--p-primary)]" />启用
        </label>
      </td>
      <td className="px-4 py-3"><input type="number" value={sort} onChange={(e) => setSort(Number(e.target.value))} className={`${inputClass} w-20`} /></td>
      <td className="px-4 py-3 text-right whitespace-nowrap">
        <Button variant="primary" disabled={save.isPending || !text.trim()} onClick={() => save.mutate()}>保存</Button>
        <Button variant="ghost" onClick={done}>取消</Button>
        <ErrorText error={save.error} />
      </td>
    </tr>
  );
}
