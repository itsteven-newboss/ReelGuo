import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, ArrowLeft, ArrowUp, Plus, X } from "lucide-react";
import { useState } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, Field, PageHeader, StatusBadge, inputClass } from "../ui";

type Topic = Schemas["AdminTopicDto"];
type TopicSeries = Schemas["AdminTopicSeriesDto"];

const KEY = ["admin", "topics"] as const;

/** 专题: hand-picked series lists shown under 专题推荐 on the home page and at /topics. */
export function TopicsPage() {
  const [editing, setEditing] = useState<number | "new" | null>(null);
  const list = useQuery({ queryKey: KEY, queryFn: async () => unwrap(await api.GET("/api/admin/topics")) });

  if (editing !== null) {
    const topic = editing === "new" ? null : list.data?.find((t) => t.id === editing) ?? null;
    if (editing !== "new" && !topic) return <div className="flex justify-center py-16"><Spinner /></div>;
    return <TopicEditor key={String(editing)} topic={topic} onBack={() => setEditing(null)} onSaved={(id) => setEditing(id)} />;
  }

  return (
    <>
      <PageHeader title="专题"
        sub="专题是一组手动挑选的短剧，显示在首页「专题推荐」和专题页。只有已发布的专题对用户可见；专题里未发布的剧会自动隐藏。排序数字越小越靠前。"
        actions={<Button variant="primary" onClick={() => setEditing("new")}><span className="flex items-center gap-1.5"><Plus size={15} />新建专题</span></Button>} />
      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : (list.data ?? []).length === 0 ? (
          <p className="py-10 text-center text-[13px] text-mut">还没有专题</p>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">标题</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">短剧</th>
                <th className="px-4 py-2.5 font-medium">排序</th>
                <th className="px-4 py-2.5 font-medium">更新时间</th>
                <th className="px-4 py-2.5" />
              </tr>
            </thead>
            <tbody>
              {list.data!.map((t) => (
                <tr key={t.id} className="border-t border-white/5 hover:bg-s2/40 cursor-pointer" onClick={() => setEditing(t.id)}>
                  <td className="px-4 py-3">
                    <div className="font-medium">{t.title}</div>
                    {t.description && <div className="text-[11.5px] text-mut truncate max-w-[420px]">{t.description}</div>}
                  </td>
                  <td className="px-4 py-3"><StatusBadge status={t.published ? "published" : "draft"} /></td>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-1">
                      {t.series.slice(0, 5).map((s) => <Thumb key={s.id} series={s} size={28} />)}
                      <span className="ml-1.5 text-mut">{t.series.length} 部</span>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-mut">{t.sort_order}</td>
                  <td className="px-4 py-3 text-mut whitespace-nowrap">{new Date(t.updated_at).toLocaleString("zh-CN")}</td>
                  <td className="px-4 py-3 text-right"><span className="text-primary">编辑</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}

function Thumb({ series, size }: { series: TopicSeries; size: number }) {
  return series.cover_url
    ? <img src={series.cover_url} alt="" className="rounded object-cover bg-s3 shrink-0" style={{ width: size, height: Math.round(size * 4 / 3) }} />
    : <span className="rounded bg-s3 shrink-0" style={{ width: size, height: Math.round(size * 4 / 3) }} />;
}

function TopicEditor({ topic, onBack, onSaved }: { topic: Topic | null; onBack: () => void; onSaved: (id: number) => void }) {
  const qc = useQueryClient();
  const [title, setTitle] = useState(topic?.title ?? "");
  const [description, setDescription] = useState(topic?.description ?? "");
  const [published, setPublished] = useState(topic?.published ?? false);
  const [sort, setSort] = useState(topic?.sort_order ?? 0);
  const [series, setSeries] = useState<TopicSeries[]>(topic?.series ?? []);
  const [q, setQ] = useState("");

  const options = useQuery({
    queryKey: ["admin", "series-picker", q],
    enabled: q.trim().length > 0,
    queryFn: async () => unwrap(await api.GET("/api/admin/series", { params: { query: { q: q.trim(), size: 8 } } })),
  });

  const save = useMutation({
    mutationFn: async () => {
      const body = { title: title.trim(), description: description.trim() || null, published, sort_order: sort, series_ids: series.map((s) => s.id) };
      return topic
        ? unwrap(await api.PUT("/api/admin/topics/{id}", { params: { path: { id: topic.id } }, body }))
        : unwrap(await api.POST("/api/admin/topics", { body }));
    },
    onSuccess: async (saved) => { await qc.invalidateQueries({ queryKey: KEY }); onSaved(saved.id); },
  });
  const remove = useMutation({
    mutationFn: async () => {
      const r = await api.DELETE("/api/admin/topics/{id}", { params: { path: { id: topic!.id } } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: async () => { await qc.invalidateQueries({ queryKey: KEY }); onBack(); },
  });

  const move = (i: number, d: -1 | 1) => setSeries((list) => {
    const next = [...list];
    const [item] = next.splice(i, 1);
    if (item) next.splice(i + d, 0, item);
    return next;
  });
  const add = (s: TopicSeries) => {
    setSeries((list) => (list.some((x) => x.id === s.id) ? list : [...list, s]));
    setQ("");
  };

  return (
    <>
      <button onClick={onBack} className="flex items-center gap-1.5 text-[12.5px] text-mut hover:text-on mb-3"><ArrowLeft size={15} /> 返回列表</button>
      <PageHeader title={topic ? topic.title : "新建专题"}
        actions={(
          <>
            {topic && <Button variant="danger" disabled={remove.isPending} onClick={() => window.confirm(`确定删除专题「${topic.title}」？剧本身不受影响。`) && remove.mutate()}>删除</Button>}
            <Button variant="primary" disabled={save.isPending || !title.trim()} onClick={() => save.mutate()}>{save.isPending ? "保存中…" : "保存"}</Button>
          </>
        )} />
      <ErrorText error={save.error ?? remove.error} />

      <Card className="p-5 grid grid-cols-[1fr_140px] gap-4">
        <Field label="标题（最多 40 字）"><input value={title} maxLength={40} onChange={(e) => setTitle(e.target.value)} className={inputClass} placeholder="例如：逆袭爽剧合集" /></Field>
        <Field label="排序"><input type="number" value={sort} onChange={(e) => setSort(Number(e.target.value))} className={inputClass} /></Field>
        <Field label="简介（最多 500 字）" className="col-span-2">
          <textarea value={description} maxLength={500} rows={3} onChange={(e) => setDescription(e.target.value)}
            className={`${inputClass} h-auto py-2 resize-y`} placeholder="一两句话介绍这个专题" />
        </Field>
        <label className="col-span-2 flex items-center gap-2 text-[13px]">
          <input type="checkbox" checked={published} onChange={(e) => setPublished(e.target.checked)} className="accent-[var(--p-primary)]" />
          发布（勾选后用户可见）
        </label>
      </Card>

      <h2 className="mt-6 mb-2 text-[14px] font-bold">专题内的短剧 <span className="text-mut font-normal">（{series.length} 部，按此顺序显示）</span></h2>
      <Card className="p-4">
        <div className="relative max-w-[420px]">
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="搜索剧名添加…" className={`${inputClass} w-full`} />
          {q.trim() && (
            <div className="absolute left-0 right-0 top-[calc(100%+4px)] z-10 rounded-(--r-md) bg-s2 elev-3 overflow-hidden">
              {options.isPending ? <div className="flex justify-center py-4"><Spinner size={18} /></div>
                : (options.data?.items ?? []).length === 0 ? <p className="px-3 py-3 text-[12.5px] text-mut">没有匹配的剧</p>
                : options.data!.items.map((s) => {
                  const added = series.some((x) => x.id === s.id);
                  return (
                    <button key={s.id} disabled={added} onClick={() => add({ id: s.id, title: s.title, cover_url: s.cover_url, status: s.status })}
                      className="w-full flex items-center gap-2.5 px-3 py-2 text-left text-[13px] hover:bg-s3 disabled:opacity-50">
                      <Thumb series={{ id: s.id, title: s.title, cover_url: s.cover_url, status: s.status }} size={22} />
                      <span className="flex-1 truncate">{s.title}</span>
                      <StatusBadge status={s.status} />
                      {added && <span className="text-[11px] text-dim">已添加</span>}
                    </button>
                  );
                })}
            </div>
          )}
        </div>

        {series.length === 0 ? <p className="mt-4 text-[12.5px] text-mut">还没有添加短剧。</p> : (
          <ol className="mt-4 divide-y divide-white/5">
            {series.map((s, i) => (
              <li key={s.id} className="flex items-center gap-3 py-2">
                <span className="w-6 text-right text-[12px] text-dim tabular-nums">{i + 1}</span>
                <Thumb series={s} size={30} />
                <span className="flex-1 min-w-0 truncate text-[13px]">{s.title}</span>
                {s.status !== "published" && <span title="未发布的剧不会在专题里显示"><StatusBadge status={s.status} /></span>}
                <button aria-label="上移" disabled={i === 0} onClick={() => move(i, -1)} className="w-7 h-7 rounded-md text-mut hover:bg-s3 disabled:opacity-30 flex items-center justify-center"><ArrowUp size={14} /></button>
                <button aria-label="下移" disabled={i === series.length - 1} onClick={() => move(i, 1)} className="w-7 h-7 rounded-md text-mut hover:bg-s3 disabled:opacity-30 flex items-center justify-center"><ArrowDown size={14} /></button>
                <button aria-label="移除" onClick={() => setSeries((list) => list.filter((x) => x.id !== s.id))} className="w-7 h-7 rounded-md text-mut hover:bg-danger/15 hover:text-danger flex items-center justify-center"><X size={14} /></button>
              </li>
            ))}
          </ol>
        )}
      </Card>
    </>
  );
}
