import { ApiError, unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, ImagePlus, Plus } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { api, CSRF_HEADER } from "../session";
import { Button, Card, ErrorText, Field, PageHeader, StatusBadge, formatTime, inputClass } from "../ui";

type PostDto = Schemas["AdminPostDto"];

async function uploadImage(postId: number, kind: "cover" | "images", file: File): Promise<string> {
  const res = await fetch(`/api/admin/posts/${postId}/${kind}`, { method: "POST", body: file, headers: { [CSRF_HEADER]: "1", "Content-Type": file.type } });
  const body = await res.json().catch(() => undefined);
  if (!res.ok) throw new ApiError(res.status, body?.error?.code ?? "error", body?.error?.message ?? "上传失败");
  return body.url as string;
}

export function PostsAdminPage() {
  const [editing, setEditing] = useState<number | "new" | null>(null);
  const list = useQuery({
    queryKey: ["admin", "posts"],
    queryFn: async () => unwrap(await api.GET("/api/admin/posts", { params: { query: { page: 1 } } })),
    enabled: editing === null,
  });

  if (editing !== null) return <PostEditor id={editing} onBack={() => setEditing(null)} onCreated={setEditing} />;

  return (
    <>
      <PageHeader title="吃瓜文章" sub="用 Markdown 撰写，保存时会自动清理不安全的内容。需要封面和正文才能发布。"
        actions={<Button variant="primary" onClick={() => setEditing("new")}><Plus size={15} className="inline -mt-0.5 mr-1" />写文章</Button>} />
      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium w-[64px]">封面</th>
                <th className="px-4 py-2.5 font-medium">标题</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">阅读</th>
                <th className="px-4 py-2.5 font-medium">更新时间</th>
              </tr>
            </thead>
            <tbody>
              {list.data?.items.length === 0 && <tr><td colSpan={5} className="px-4 py-10 text-center text-dim">还没有文章</td></tr>}
              {list.data?.items.map((p) => (
                <tr key={p.id} onClick={() => setEditing(p.id)} className="border-t border-white/5 cursor-pointer hover:bg-(--p-hover)">
                  <td className="px-4 py-2.5"><div className="w-10 h-[53px] rounded-md bg-s3 overflow-hidden">{p.cover_url && <img src={p.cover_url} alt="" className="w-full h-full object-cover" />}</div></td>
                  <td className="px-4 py-2.5 font-medium">{p.title}</td>
                  <td className="px-4 py-2.5"><StatusBadge status={p.status} /></td>
                  <td className="px-4 py-2.5 text-mut">{p.view_count}</td>
                  <td className="px-4 py-2.5 text-mut whitespace-nowrap">{formatTime(p.updated_at)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}

function PostEditor({ id, onBack, onCreated }: { id: number | "new"; onBack: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const isNew = id === "new";
  const post = useQuery({
    queryKey: ["admin", "post", id],
    enabled: !isNew,
    queryFn: async () => unwrap(await api.GET("/api/admin/posts/{id}", { params: { path: { id: id as number } } })),
  });

  const [title, setTitle] = useState("");
  const [summary, setSummary] = useState("");
  const [tags, setTags] = useState("");
  const [markdown, setMarkdown] = useState("");
  const [seriesId, setSeriesId] = useState<number | null>(null);
  const [seriesQuery, setSeriesQuery] = useState("");
  const loaded = useRef<number | null>(null);
  const textarea = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    const p = post.data;
    if (p && loaded.current !== p.id) {
      loaded.current = p.id;
      setTitle(p.title); setSummary(p.summary); setTags(p.tags.join("，")); setMarkdown(p.content_markdown); setSeriesId(p.series_id ?? null);
      setSeriesQuery(p.series_title ?? "");
    }
  }, [post.data]);

  // Live preview rendered by the server, so it matches exactly what gets published.
  const [preview, setPreview] = useState("");
  useEffect(() => {
    const t = window.setTimeout(async () => {
      try {
        setPreview(unwrap(await api.POST("/api/admin/posts/preview", { body: { markdown } })).html);
      } catch { /* keep the last preview */ }
    }, 400);
    return () => window.clearTimeout(t);
  }, [markdown]);

  const seriesOptions = useQuery({
    queryKey: ["admin", "series-picker", seriesQuery],
    enabled: seriesQuery.trim().length > 0,
    queryFn: async () => unwrap(await api.GET("/api/admin/series", { params: { query: { q: seriesQuery.trim(), size: 8 } } })),
  });

  const body = () => ({
    title, summary, content_markdown: markdown, series_id: seriesId,
    tags: tags.split(/[,，|、\s]+/).map((x) => x.trim()).filter(Boolean),
  });
  const refresh = (saved: PostDto) => {
    qc.setQueryData(["admin", "post", saved.id], saved);
    void qc.invalidateQueries({ queryKey: ["admin", "posts"] });
  };

  const save = useMutation({
    mutationFn: async () => isNew
      ? unwrap(await api.POST("/api/admin/posts", { body: body() }))
      : unwrap(await api.PUT("/api/admin/posts/{id}", { params: { path: { id: id as number } }, body: body() })),
    onSuccess: (saved) => { refresh(saved); if (isNew) onCreated(saved.id); },
  });
  const publish = useMutation({
    mutationFn: async (on: boolean) => {
      const path = { params: { path: { id: id as number } } };
      if (on) return refresh(unwrap(await api.POST("/api/admin/posts/{id}/publish", path)));
      const r = await api.POST("/api/admin/posts/{id}/unpublish", path);
      if (!r.response.ok) unwrap(r);
      void qc.invalidateQueries({ queryKey: ["admin", "post", id] });
    },
  });
  const remove = useMutation({
    mutationFn: async () => {
      const r = await api.DELETE("/api/admin/posts/{id}", { params: { path: { id: id as number } } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: () => { void qc.invalidateQueries({ queryKey: ["admin", "posts"] }); onBack(); },
  });
  const cover = useMutation({
    mutationFn: (file: File) => uploadImage(id as number, "cover", file),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["admin", "post", id] }),
  });
  const inline = useMutation({
    mutationFn: (file: File) => uploadImage(id as number, "images", file),
    onSuccess: (url) => {
      // Insert the image at the cursor.
      const el = textarea.current;
      const at = el?.selectionStart ?? markdown.length;
      setMarkdown((m) => `${m.slice(0, at)}\n\n![](${url})\n\n${m.slice(at)}`);
    },
  });

  if (!isNew && post.isPending) return <div className="flex justify-center py-16"><Spinner /></div>;
  const p = post.data;
  const publishError = publish.error instanceof ApiError ? publish.error : null;

  return (
    <>
      <button onClick={onBack} className="flex items-center gap-1.5 text-[12.5px] text-mut hover:text-on mb-3"><ArrowLeft size={15} /> 返回列表</button>
      <PageHeader title={isNew ? "写文章" : p!.title} sub={p && <StatusBadge status={p.status} />}
        actions={p && (
          <>
            <Button variant="danger" onClick={() => window.confirm("确定删除这篇文章？") && remove.mutate()}>删除</Button>
            {p.status === "published"
              ? <Button onClick={() => publish.mutate(false)}>下架</Button>
              : <Button variant="primary" disabled={publish.isPending} onClick={() => publish.mutate(true)}>发布</Button>}
          </>
        )} />
      {publishError && (
        <Card className="p-4 mb-4">
          <ErrorText error={publishError} />
          {publishError.details.length > 0 && <ul className="list-disc pl-5 mt-1 text-[12.5px] text-mut">{publishError.details.map((d) => <li key={d}>{d}</li>)}</ul>}
        </Card>
      )}

      <div className="grid grid-cols-[1fr_220px] gap-4 items-start">
        <Card className="p-5 space-y-4">
          <Field label="标题"><input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} className={inputClass} /></Field>
          <Field label="摘要"><input value={summary} onChange={(e) => setSummary(e.target.value)} maxLength={500} className={inputClass} /></Field>
          <div className="grid grid-cols-2 gap-4">
            <Field label="标签" hint="用逗号分隔"><input value={tags} onChange={(e) => setTags(e.target.value)} className={inputClass} /></Field>
            <Field label="关联剧集（观看正片）" hint={seriesId ? `已关联 #${seriesId}` : "输入剧名搜索"}>
              <div className="relative">
                <input value={seriesQuery} onChange={(e) => { setSeriesQuery(e.target.value); if (!e.target.value) setSeriesId(null); }} className={`${inputClass} w-full`} />
                {seriesQuery && seriesOptions.data && !seriesOptions.data.items.some((s) => s.id === seriesId && s.title === seriesQuery) && (
                  <div className="absolute z-10 left-0 right-0 top-10 rounded-(--r-sm) bg-s3 elev-3 max-h-[220px] overflow-auto">
                    {seriesOptions.data.items.map((s) => (
                      <button key={s.id} type="button" onClick={() => { setSeriesId(s.id); setSeriesQuery(s.title); }}
                        className="w-full text-left px-3 py-2 text-[12.5px] hover:bg-(--p-hover)">{s.title} <span className="text-dim">#{s.id}</span></button>
                    ))}
                  </div>
                )}
              </div>
            </Field>
          </div>
          <div className="grid grid-cols-2 gap-4">
            <Field label="正文（Markdown）">
              <textarea ref={textarea} value={markdown} onChange={(e) => setMarkdown(e.target.value)} rows={18} spellCheck={false}
                placeholder={"## 小标题\n\n正文段落，**粗体**，[链接](https://...)\n\n> 引用"}
                className="rounded-(--r-sm) bg-s3 p-3 font-mono text-[12.5px] text-on outline-none focus:ring-2 ring-primary" />
            </Field>
            <Field label="预览">
              <div className="article-preview rounded-(--r-sm) bg-s2 p-3 min-h-[380px] text-[13px] leading-[1.85] overflow-auto" dangerouslySetInnerHTML={{ __html: preview }} />
            </Field>
          </div>
          <div className="flex items-center gap-3">
            <Button variant="primary" disabled={save.isPending} onClick={() => save.mutate()}>{save.isPending ? "保存中…" : isNew ? "创建" : "保存"}</Button>
            {!isNew && (
              <label className="h-9 px-4 rounded-(--r-ctl) bg-s3 hover:bg-s4 text-[13px] cursor-pointer flex items-center gap-1.5">
                <ImagePlus size={15} /> {inline.isPending ? "上传中…" : "插入图片"}
                <input type="file" accept="image/jpeg,image/png,image/webp" className="hidden"
                  onChange={(e) => { const f = e.target.files?.[0]; if (f) inline.mutate(f); e.target.value = ""; }} />
              </label>
            )}
            {save.isSuccess && <span className="text-[12.5px] text-success">已保存</span>}
            <ErrorText error={save.error ?? inline.error} />
          </div>
        </Card>

        {p && (
          <Card className="p-4">
            <div className="text-[13px] font-semibold mb-2">封面</div>
            <div className="w-full aspect-[3/4] rounded-(--r-sm) bg-s3 overflow-hidden">{p.cover_url && <img src={p.cover_url} alt="" className="w-full h-full object-cover" />}</div>
            <label className="mt-3 flex items-center justify-center gap-1.5 h-9 rounded-(--r-ctl) bg-s3 hover:bg-s4 text-[12.5px] cursor-pointer">
              <ImagePlus size={15} /> {cover.isPending ? "上传中…" : "上传封面"}
              <input type="file" accept="image/jpeg,image/png,image/webp" className="hidden"
                onChange={(e) => { const f = e.target.files?.[0]; if (f) cover.mutate(f); e.target.value = ""; }} />
            </label>
            <ErrorText error={cover.error} />
            <p className="mt-3 text-[11px] text-dim">阅读 {p.view_count} · 更新于 {formatTime(p.updated_at)}</p>
          </Card>
        )}
      </div>
    </>
  );
}
