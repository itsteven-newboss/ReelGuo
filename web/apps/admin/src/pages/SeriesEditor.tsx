import { ApiError, unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, GitBranch, ImagePlus, Trash2 } from "lucide-react";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { api, CSRF_HEADER } from "../session";
import { Button, Card, CARD_BADGES, CardBadgePills, ErrorText, Field, FieldGroup, PageHeader, StatusBadge, inputClass, type CardBadge } from "../ui";
import { DeleteSeriesDialog } from "./DeleteSeriesDialog";
import { EpisodesPanel } from "./Episodes";

type SeriesDto = Schemas["AdminSeriesDto"];
type SeriesInput = Schemas["SeriesInput"];

/** Form state: lists are edited as text and parsed on save. */
type FormState = {
  kind: "standard" | "interactive";
  category_slug: string;
  title: string;
  description: string;
  tags: string;
  score: string;
  total_episodes: string;
  is_finished: boolean;
  is_original: boolean;
  card_badge: CardBadge;
  author_name: string;
  external_id: string;
  rights_holder: string;
  contract_ref: string;
  licence_start: string;
  licence_end: string;
  territories: string;
  ai_generated: "" | "yes" | "no";
  has_real_person: "" | "yes" | "no";
  consent_ref: string;
  recommend_weight: string;
  episode_price: string;
  free_episodes: string;
};

const EMPTY: FormState = {
  kind: "standard", category_slug: "", title: "", description: "", tags: "", score: "0", total_episodes: "0",
  // New series start as 原创 + 新片 (the server's default too).
  is_finished: false, is_original: true, card_badge: "new", author_name: "", external_id: "",
  // 版权与合规 is optional; a new series starts as the server's defaults (PublishRules.ApplyNewSeriesDefaults).
  rights_holder: "huangyueai", contract_ref: "", licence_start: "", licence_end: "", territories: "*",
  ai_generated: "no", has_real_person: "no", consent_ref: "", recommend_weight: "0", episode_price: "0", free_episodes: "3",
};

const tri = (v: boolean | null | undefined): FormState["ai_generated"] => (v === true ? "yes" : v === false ? "no" : "");
const fromTri = (v: FormState["ai_generated"]) => (v === "yes" ? true : v === "no" ? false : null);
const splitList = (v: string) => v.split(/[,，|、\s]+/).map((t) => t.trim()).filter(Boolean);

function toForm(s: SeriesDto): FormState {
  return {
    kind: s.kind ?? "standard",
    category_slug: s.category_slug, title: s.title, description: s.description, tags: s.tags.join("，"),
    score: String(s.score), total_episodes: String(s.total_episodes), is_finished: s.is_finished, is_original: s.is_original,
    card_badge: s.card_badge, author_name: s.author_name ?? "", external_id: s.external_id ?? "",
    rights_holder: s.rights_holder ?? "", contract_ref: s.contract_ref ?? "",
    licence_start: s.licence_start ?? "", licence_end: s.licence_end ?? "", territories: s.territories.join(", "),
    ai_generated: tri(s.ai_generated), has_real_person: tri(s.has_real_person), consent_ref: s.consent_ref ?? "",
    recommend_weight: String(s.recommend_weight), episode_price: String(s.episode_price), free_episodes: String(s.free_episodes),
  };
}

function toInput(f: FormState): SeriesInput {
  return {
    kind: f.kind,
    category_slug: f.category_slug, title: f.title, description: f.description, tags: splitList(f.tags),
    score: Number(f.score) || 0, total_episodes: Number(f.total_episodes) || 0, is_finished: f.is_finished, is_original: f.is_original,
    card_badge: f.card_badge, author_name: f.author_name || null, external_id: f.external_id || null,
    rights_holder: f.rights_holder || null, contract_ref: f.contract_ref || null,
    licence_start: f.licence_start || null, licence_end: f.licence_end || null,
    territories: splitList(f.territories.toUpperCase()),
    ai_generated: fromTri(f.ai_generated), has_real_person: fromTri(f.has_real_person), consent_ref: f.consent_ref || null,
    recommend_weight: Math.max(0, Math.min(100, Number(f.recommend_weight) || 0)),
    episode_price: Math.max(0, Math.round(Number(f.episode_price) || 0)), free_episodes: Math.max(0, Math.round(Number(f.free_episodes) || 0)),
  };
}

export function SeriesEditor({ id, onBack, onCreated, onStory }: {
  id: number | "new"; onBack: () => void; onCreated: (id: number) => void;
  /** Opens the story editor (剧情编辑) of an interactive series. */
  onStory: (id: number) => void;
}) {
  const qc = useQueryClient();
  const isNew = id === "new";
  const key = ["admin", "series", "detail", id];
  const [deleting, setDeleting] = useState(false);

  const series = useQuery({
    queryKey: key,
    enabled: !isNew,
    queryFn: async () => unwrap(await api.GET("/api/admin/series/{id}", { params: { path: { id: id as number } } })),
    // Poll while episodes are uploading or encoding.
    refetchInterval: (query) => (query.state.data?.episodes.some((e) => e.status === "processing") ? 3000 : false),
  });
  const categories = useQuery({
    queryKey: ["admin", "categories"],
    queryFn: async () => unwrap(await api.GET("/api/admin/categories")),
  });

  const [form, setForm] = useState<FormState>(EMPTY);
  const loadedFor = useRef<number | null>(null);
  useEffect(() => {
    if (series.data && loadedFor.current !== series.data.id) {
      loadedFor.current = series.data.id;
      setForm(toForm(series.data));
    }
  }, [series.data]);
  useEffect(() => {
    if (isNew && !form.category_slug && categories.data) {
      const first = categories.data.find((c) => c.status === "active");
      if (first) setForm((f) => ({ ...f, category_slug: first.slug }));
    }
  }, [isNew, categories.data, form.category_slug]);

  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => setForm((f) => ({ ...f, [k]: v }));
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ["admin", "series"] });
  };

  const save = useMutation({
    mutationFn: async () => isNew
      ? unwrap(await api.POST("/api/admin/series", { body: toInput(form) }))
      : unwrap(await api.PUT("/api/admin/series/{id}", { params: { path: { id: id as number } }, body: toInput(form) })),
    onSuccess: (saved) => {
      refresh();
      qc.setQueryData(["admin", "series", "detail", saved.id], saved);
      if (isNew) onCreated(saved.id);
    },
  });

  const publish = useMutation({
    mutationFn: async (on: boolean) => {
      const path = { params: { path: { id: id as number } } };
      if (on) unwrap(await api.POST("/api/admin/series/{id}/publish", path));
      else {
        const r = await api.POST("/api/admin/series/{id}/unpublish", path);
        if (!r.response.ok) unwrap(r);
      }
    },
    onSuccess: refresh,
  });

  const cover = useMutation({
    mutationFn: async (file: File) => {
      const res = await fetch(`/api/admin/series/${id}/cover`, { method: "POST", body: file, headers: { [CSRF_HEADER]: "1", "Content-Type": file.type } });
      const body = await res.json().catch(() => undefined);
      if (!res.ok) throw new ApiError(res.status, body?.error?.code ?? "error", body?.error?.message ?? "封面上传失败");
    },
    onSuccess: refresh,
  });

  function submit(e: FormEvent) {
    e.preventDefault();
    save.mutate();
  }

  if (!isNew && series.isPending) return <div className="flex justify-center py-16"><Spinner /></div>;
  if (!isNew && series.isError) return <ErrorText error={series.error} />;

  const s = series.data;
  const channelOptions = (categories.data ?? []).filter((c) => c.status === "active" || c.slug === s?.category_slug);
  const publishError = publish.error instanceof ApiError ? publish.error : null;

  return (
    <>
      <button onClick={onBack} className="flex items-center gap-1.5 text-[12.5px] text-mut hover:text-on mb-3">
        <ArrowLeft size={15} /> 返回列表
      </button>
      <PageHeader
        title={isNew ? "新建剧集" : s!.title}
        sub={s && <span className="flex items-center gap-2"><StatusBadge status={s.status} />{s.published_at && <span>首次发布于 {new Date(s.published_at).toLocaleDateString("zh-CN")}</span>}</span>}
        actions={s && (
          <div className="flex gap-2">
            <Button variant="ghost" className="hover:!text-danger" onClick={() => setDeleting(true)}><Trash2 size={14} className="inline -mt-0.5 mr-1" />删除</Button>
            {s.status === "published"
              ? <Button variant="danger" disabled={publish.isPending} onClick={() => window.confirm("下架后用户将无法观看，确定？") && publish.mutate(false)}>下架</Button>
              : <Button variant="primary" disabled={publish.isPending || s.publish_problems.length > 0} onClick={() => publish.mutate(true)}>发布</Button>}
          </div>
        )}
      />
      {s && <DeleteSeriesDialog ids={deleting ? [s.id] : null} onClose={() => setDeleting(false)} onDeleted={onBack} />}

      {s && s.status !== "published" && s.publish_problems.length > 0 && (
        <Card className="p-4 mb-4 border border-star/30">
          <div className="text-[13px] font-semibold text-star mb-1.5">发布前还需完成：</div>
          <ul className="list-disc pl-5 text-[12.5px] text-mut space-y-0.5">{s.publish_problems.map((p) => <li key={p}>{p}</li>)}</ul>
        </Card>
      )}
      {s && s.kind === "interactive" && (
        <Card className="p-4 mb-4 flex items-center gap-4 border border-primary/30">
          <GitBranch size={22} className="text-primary shrink-0" />
          <div className="flex-1 min-w-0">
            <div className="text-[13.5px] font-semibold">互动剧</div>
            <p className="text-[12px] text-mut mt-0.5">下面的每个分集是一个片段（S1、S2…）。在剧情编辑里设置每个片段播完后出现哪些选项、跳到哪里、哪些是结局。</p>
          </div>
          <Button variant="primary" onClick={() => onStory(s.id)}>剧情编辑</Button>
        </Card>
      )}
      {publishError && (
        <Card className="p-4 mb-4">
          <ErrorText error={publishError} />
          {publishError.details.length > 0 && <ul className="list-disc pl-5 mt-1 text-[12.5px] text-mut">{publishError.details.map((d) => <li key={d}>{d}</li>)}</ul>}
        </Card>
      )}

      <div className="grid grid-cols-[1fr_220px] gap-4 items-start">
        <form onSubmit={submit} className="space-y-4">
          <Card className="p-5 grid grid-cols-2 gap-4">
            <div className="col-span-2 text-[14px] font-semibold">基本信息</div>
            <Field label="剧名" className="col-span-2"><input required maxLength={200} value={form.title} onChange={(e) => set("title", e.target.value)} className={inputClass} /></Field>
            <FieldGroup label="类型" className="col-span-2"
              hint={form.kind === "interactive"
                ? "互动剧：每个上传的视频是一个片段，播完后由观众选择接下来看哪个片段。保存后点「剧情编辑」编排剧情。"
                : "普通剧集：分集按顺序播放。"}>
              <div className="flex gap-1 p-1 rounded-(--r-ctl) bg-s3 w-fit">
                {([["standard", "普通剧集"], ["interactive", "互动剧"]] as const).map(([v, l]) => (
                  <button type="button" key={v} onClick={() => set("kind", v)}
                    className={`px-3 h-7 rounded-(--r-ctl) text-[12px] ${form.kind === v ? "bg-sec text-primary font-semibold" : "text-mut"}`}>{l}</button>
                ))}
              </div>
            </FieldGroup>
            <Field label="频道" hint="未开放的频道（如 AI换脸）不能添加内容">
              <select required value={form.category_slug} onChange={(e) => set("category_slug", e.target.value)} className={inputClass}>
                {channelOptions.map((c) => <option key={c.slug} value={c.slug}>{c.name}</option>)}
              </select>
            </Field>
            <Field label="作者 / 出品方"><input value={form.author_name} onChange={(e) => set("author_name", e.target.value)} className={inputClass} /></Field>
            <Field label="简介" className="col-span-2">
              <textarea rows={3} value={form.description} onChange={(e) => set("description", e.target.value)} className={`${inputClass} h-auto py-2`} />
            </Field>
            <Field label="标签" hint="用逗号分隔，最多 10 个"><input value={form.tags} onChange={(e) => set("tags", e.target.value)} placeholder="都市，逆袭" className={inputClass} /></Field>
            <div className="grid grid-cols-2 gap-4">
              <Field label="评分（0-10）"><input type="number" min={0} max={10} step={0.1} value={form.score} onChange={(e) => set("score", e.target.value)} className={inputClass} /></Field>
              <Field label="总集数"><input type="number" min={0} value={form.total_episodes} onChange={(e) => set("total_episodes", e.target.value)} className={inputClass} /></Field>
            </div>
            <div className="flex gap-6 text-[13px]">
              <label className="flex items-center gap-2"><input type="checkbox" checked={form.is_finished} onChange={(e) => set("is_finished", e.target.checked)} />已完结</label>
              <label className="flex items-center gap-2"><input type="checkbox" checked={form.is_original} onChange={(e) => set("is_original", e.target.checked)} />原创</label>
            </div>
            <Field label="卡片角标" hint={`${CARD_BADGES.find((b) => b.value === form.card_badge)?.hint ?? ""}。「原创」由上面的勾选决定，也会让本剧出现在原创频道。`}>
              <div className="flex items-center gap-3">
                <select value={form.card_badge} onChange={(e) => set("card_badge", e.target.value as CardBadge)} className={`${inputClass} w-[160px]`}>
                  {CARD_BADGES.map((b) => <option key={b.value} value={b.value}>{b.label}</option>)}
                </select>
                <span className="text-[12px] text-dim">预览</span>
                <CardBadgePills original={form.is_original} badge={form.card_badge} />
              </div>
            </Field>
            <Field label="编辑推荐权重（0-100）" hint="大于 0 即进入推荐榜，数值越大越靠前">
              <input type="number" min={0} max={100} value={form.recommend_weight} onChange={(e) => set("recommend_weight", e.target.value)} className={inputClass} />
            </Field>
            <Field label="外部编号（内容源 ID）" hint="批量导入按此编号匹配"><input value={form.external_id} onChange={(e) => set("external_id", e.target.value)} className={inputClass} /></Field>
          </Card>

          <Card className="p-5 grid grid-cols-2 gap-4">
            <div className="col-span-2">
              <div className="text-[14px] font-semibold">付费设置</div>
              <p className="text-[11.5px] text-dim mt-0.5">
                {form.kind === "interactive"
                  ? Number(form.episode_price) > 0
                    ? `互动剧按片段收费：S1-S${Number(form.free_episodes) || 0} 免费，其余片段每个 ${Number(form.episode_price)} 金币（VIP 免费看）。可在剧情编辑里把某个片段设为「始终免费」，比如第一个选择之前的片段。`
                    : "全剧免费。设置单集价格后，免费数量之后的片段需用金币解锁或开通 VIP。"
                  : Number(form.episode_price) > 0
                    ? `前 ${Number(form.free_episodes) || 0} 集免费，之后每集 ${Number(form.episode_price)} 金币（VIP 免费看）。分集列表里勾选「始终免费」的单集不收费。`
                    : "全剧免费。设置单集价格后，免费集数之后的剧集需用金币解锁或开通 VIP。"}
              </p>
            </div>
            <Field label="单集价格（金币）" hint="0 = 全剧免费"><input type="number" min={0} max={10000} value={form.episode_price} onChange={(e) => set("episode_price", e.target.value)} className={inputClass} /></Field>
            <Field label="免费集数" hint="从第 1 集起免费的集数，建议 2-5 集"><input type="number" min={0} value={form.free_episodes} onChange={(e) => set("free_episodes", e.target.value)} className={inputClass} /></Field>
          </Card>

          <Card className="p-5 grid grid-cols-2 gap-4">
            <div className="col-span-2">
              <div className="text-[14px] font-semibold">版权与合规</div>
              <p className="text-[11.5px] text-dim mt-0.5">选填，不影响发布。设置了授权地区时，地区之外的用户看不到这部剧；选择「出现真实人物」时，须填写授权同意书编号才能发布。</p>
            </div>
            <Field label="版权方" hint="默认 huangyueai"><input value={form.rights_holder} onChange={(e) => set("rights_holder", e.target.value)} placeholder="huangyueai" className={inputClass} /></Field>
            <Field label="合同编号"><input value={form.contract_ref} onChange={(e) => set("contract_ref", e.target.value)} className={inputClass} /></Field>
            <Field label="授权开始" hint="留空表示立即生效"><input type="date" value={form.licence_start} onChange={(e) => set("licence_start", e.target.value)} className={inputClass} /></Field>
            <Field label="授权结束" hint="留空表示长期"><input type="date" value={form.licence_end} onChange={(e) => set("licence_end", e.target.value)} className={inputClass} /></Field>
            <Field label="授权地区" hint="ISO 国家代码，用逗号分隔（如 US, JP, MY）；* 或留空表示全球" className="col-span-2">
              <input value={form.territories} onChange={(e) => set("territories", e.target.value)} placeholder="*" className={inputClass} />
            </Field>
            <TriState label="是否为 AI 生成内容" value={form.ai_generated} onChange={(v) => set("ai_generated", v)} />
            <TriState label="是否出现真实可识别的人物" value={form.has_real_person} onChange={(v) => set("has_real_person", v)} />
            {form.has_real_person === "yes" && (
              <Field label="授权同意书编号" hint="出现真实人物时必须提供本人签署的同意书与身份证明记录" className="col-span-2">
                <input value={form.consent_ref} onChange={(e) => set("consent_ref", e.target.value)} className={inputClass} />
              </Field>
            )}
          </Card>

          <div className="flex items-center gap-3">
            <Button type="submit" variant="primary" disabled={save.isPending}>{save.isPending ? "保存中…" : isNew ? "创建" : "保存"}</Button>
            {save.isSuccess && !isNew && <span className="text-[12.5px] text-success">已保存</span>}
            <ErrorText error={save.error} />
          </div>
        </form>

        {s && (
          <Card className="p-4">
            <div className="text-[13px] font-semibold mb-2">封面</div>
            <div className="relative w-full aspect-[3/4] rounded-(--r-sm) bg-s3 overflow-hidden">
              {s.cover_url ? <img src={s.cover_url} alt="" className="w-full h-full object-cover" /> : <div className="absolute inset-0 flex items-center justify-center text-[11.5px] text-dim p-3 text-center">未上传时使用第一集的画面</div>}
            </div>
            <label className="mt-3 flex items-center justify-center gap-1.5 h-9 rounded-(--r-ctl) bg-s3 hover:bg-s4 text-[12.5px] cursor-pointer">
              <ImagePlus size={15} /> {cover.isPending ? "上传中…" : "上传封面"}
              <input type="file" accept="image/jpeg,image/png,image/webp" className="hidden"
                onChange={(e) => { const f = e.target.files?.[0]; if (f) cover.mutate(f); e.target.value = ""; }} />
            </label>
            <p className="mt-1.5 text-[11px] text-dim">JPEG / PNG / WebP，3:4，不超过 5 MB</p>
            <ErrorText error={cover.error} />
          </Card>
        )}
      </div>

      {s && <EpisodesPanel series={s} onChanged={refresh} />}
    </>
  );
}

function TriState({ label, value, onChange }: { label: string; value: FormState["ai_generated"]; onChange: (v: FormState["ai_generated"]) => void }) {
  return (
    <FieldGroup label={label}>
      <div className="flex gap-1 p-1 rounded-(--r-ctl) bg-s3 w-fit">
        {([["yes", "是"], ["no", "否"], ["", "未声明"]] as const).map(([v, l]) => (
          <button type="button" key={v} onClick={() => onChange(v)}
            className={`px-3 h-7 rounded-(--r-ctl) text-[12px] ${value === v ? "bg-sec text-primary font-semibold" : "text-mut"}`}>{l}</button>
        ))}
      </div>
    </FieldGroup>
  );
}
