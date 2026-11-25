import { ApiError, unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Flag, LayoutGrid, Play, Plus, RotateCcw, SlidersHorizontal, Trash2, X } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { api } from "../session";
import { Button, Card, Dialog, ErrorText, Field, FieldGroup, PageHeader, StatusBadge, inputClass } from "../ui";
import { formatDuration } from "../upload";
import { PreviewPlayer } from "./EpisodePreview";

type StoryDto = Schemas["AdminStoryDto"];
type SceneDto = Schemas["AdminSceneDto"];
type Outcome = Schemas["SceneOutcome"];
type StoryInput = Schemas["AdminStoryInput"];

// The same limits as the server (StoryRules).
const MAX_OPTIONS = 4;
const MAX_LABEL = 12;
const MAX_DESCRIPTION = 30;
const MAX_PROMPT = 40;
const MAX_ENDING = 20;
const MAX_VARIABLES = 5;
const MAX_VARIABLE_NAME = 8;
const MAX_EFFECTS = 3;
const MAX_CONDITIONS = 2;
const LEADS = [0, 3, 5, 10];
const COUNTDOWNS: (number | null)[] = [null, 5, 10, 15, 20, 30];
const OPS: { value: string; label: string }[] = [{ value: "gte", label: "≥" }, { value: "lte", label: "≤" }, { value: "eq", label: "=" }];

type Effect = { variable: string; add: number };
type Condition = { variable: string; op: string; value: number };
type Variable = { key: string; name: string; initial: number };
type Point = { x: number; y: number };
type OptionDraft = {
  key: string; id: number | null; label: string; description: string; target: number | null; isDefault: boolean;
  effects: Effect[]; conditions: Condition[];
};
type SceneDraft = {
  outcome: Outcome; prompt: string; lead: number; countdown: number | null; next: number | null; ending: string; options: OptionDraft[];
  isFree: boolean; customPrice: number | null;
};
/** `layout` null: the map is laid out automatically. */
type Draft = { start: number | null; scenes: Record<number, SceneDraft>; variables: Variable[]; layout: Record<number, Point> | null };

let keySeq = 0;
const newKey = () => `new${++keySeq}`;

function fromDto(story: StoryDto): Draft {
  const scenes: Record<number, SceneDraft> = {};
  for (const s of story.scenes) {
    scenes[s.episode_id] = {
      outcome: s.outcome, prompt: s.prompt ?? "", lead: s.lead_seconds, countdown: s.countdown ?? null, next: s.next_episode_id ?? null,
      ending: s.ending_title ?? "", isFree: s.is_free, customPrice: s.custom_price ?? null,
      options: s.options.map((o) => ({
        key: `id${o.id}`, id: o.id, label: o.label, description: o.description ?? "", target: o.target_episode_id, isDefault: o.is_default,
        effects: o.effects, conditions: o.conditions,
      })),
    };
  }
  const layout = story.layout.length ? Object.fromEntries(story.layout.map((p) => [p.episode_id, { x: p.x, y: p.y }])) : null;
  return { start: story.start_episode_id ?? null, scenes, variables: story.variables, layout };
}

function toInput(draft: Draft): StoryInput {
  return {
    start_episode_id: draft.start,
    variables: draft.variables.map((v) => ({ ...v, name: v.name.trim() })),
    layout: draft.layout ? Object.entries(draft.layout).map(([id, p]) => ({ episode_id: Number(id), x: Math.round(p.x), y: Math.round(p.y) })) : [],
    scenes: Object.entries(draft.scenes).map(([id, s]) => ({
      episode_id: Number(id),
      outcome: s.outcome,
      prompt: s.outcome === "choice" ? s.prompt.trim() || null : null,
      lead_seconds: s.outcome === "choice" ? s.lead : 0,
      countdown: s.outcome === "choice" ? s.countdown : null,
      next_episode_id: s.outcome === "continue" ? s.next : null,
      ending_title: s.outcome === "ending" ? s.ending.trim() || null : null,
      is_free: s.isFree,
      custom_price: s.isFree ? null : s.customPrice,
      options: s.outcome === "choice"
        ? s.options.filter((o) => o.target !== null).map((o) => ({
          id: o.id, label: o.label.trim(), description: o.description.trim() || null, target_episode_id: o.target!, is_default: o.isDefault,
          effects: o.effects, conditions: o.conditions,
        }))
        : [],
    })),
  };
}

/** Where a scene leads in the draft (its options' targets, or the scene it continues to). */
function exitsOf(d: SceneDraft | undefined): { to: number; label: string; auto: boolean }[] {
  if (!d) return [];
  if (d.outcome === "choice") return d.options.filter((o) => o.target !== null).map((o) => ({ to: o.target!, label: o.label || "（未命名）", auto: false }));
  if (d.outcome === "continue" && d.next !== null) return [{ to: d.next, label: "自动接续", auto: true }];
  return [];
}

const sceneLabel = (s: Pick<SceneDto, "ep_num" | "title">) => `S${s.ep_num}${s.title ? ` ${s.title}` : ""}`;
const opLabel = (op: string) => OPS.find((o) => o.value === op)?.label ?? op;
const holds = (c: Condition, score: number) => (c.op === "gte" ? score >= c.value : c.op === "lte" ? score <= c.value : score === c.value);
const signed = (n: number) => (n > 0 ? `+${n}` : String(n));

/** 剧情编辑: what happens when each scene of an interactive series ends, the story map, scores, problems, statistics and 试玩. */
export function StoryEditor({ id, onBack }: { id: number; onBack: () => void }) {
  const qc = useQueryClient();
  const key = ["admin", "story", id];
  const story = useQuery({
    queryKey: key,
    queryFn: async () => unwrap(await api.GET("/api/admin/series/{id}/story", { params: { path: { id } } })),
  });
  const [draft, setDraft] = useState<Draft | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [notice, setNotice] = useState("");
  const loadedAt = useRef(0);

  useEffect(() => {
    if (story.data && story.dataUpdatedAt !== loadedAt.current) {
      loadedAt.current = story.dataUpdatedAt;
      setDraft(fromDto(story.data));
      setSelected((cur) => cur ?? story.data.effective_start_episode_id ?? story.data.scenes[0]?.episode_id ?? null);
    }
  }, [story.data, story.dataUpdatedAt]);

  const save = useMutation({
    mutationFn: async (d: Draft) => unwrap(await api.PUT("/api/admin/series/{id}/story", { params: { path: { id } }, body: toInput(d) })),
    onSuccess: (saved) => {
      qc.setQueryData(key, saved);
      void qc.invalidateQueries({ queryKey: ["admin", "series"] });
    },
  });

  const dirty = useMemo(() => !!story.data && !!draft && JSON.stringify(toInput(draft)) !== JSON.stringify(toInput(fromDto(story.data))), [story.data, draft]);
  const missingTargets = useMemo(() => draft
    ? Object.entries(draft.scenes).flatMap(([sid, s]) => s.outcome === "choice" && s.options.some((o) => o.target === null)
      ? [story.data?.scenes.find((x) => x.episode_id === Number(sid))?.ep_num] : [])
    : [], [draft, story.data]);

  if (story.isPending || (story.data && !draft)) return <div className="flex justify-center py-16"><Spinner /></div>;
  if (story.isError) return <ErrorText error={story.error} />;
  const data = story.data!;
  const d = draft!;
  const scenes = data.scenes;
  const start = d.start ?? data.effective_start_episode_id ?? scenes[0]?.episode_id ?? null;
  const current = scenes.find((s) => s.episode_id === selected) ?? null;
  const errors = data.problems.filter((p) => p.level === "error");
  const warnings = data.problems.filter((p) => p.level === "warning");
  const endings = scenes.filter((s) => d.scenes[s.episode_id]?.outcome === "ending").length;

  const update = (sceneId: number, patch: Partial<SceneDraft>) =>
    setDraft((prev) => prev && { ...prev, scenes: { ...prev.scenes, [sceneId]: { ...prev.scenes[sceneId]!, ...patch } } });
  const back = () => { if (!dirty || window.confirm("剧情有未保存的修改，确定离开？")) onBack(); };

  /** A link drawn on the map: an option to `to` (or the continuation, for a scene that continues). */
  const connect = (from: number, to: number) => {
    const sd = d.scenes[from];
    const name = scenes.find((s) => s.episode_id === from)?.ep_num;
    if (!sd) return;
    setSelected(from);
    if (sd.outcome === "continue") { update(from, { next: to }); setNotice(`S${name} 改为接着播放这个片段`); return; }
    if (sd.outcome === "ending" && !window.confirm(`S${name} 是结局，改为出现选项？`)) return;
    const options = sd.outcome === "choice" ? sd.options : [];
    if (options.length >= MAX_OPTIONS) { setNotice(`S${name} 已有 ${MAX_OPTIONS} 个选项`); return; }
    update(from, { outcome: "choice", options: [...options, { ...blankOption(), target: to }] });
    setNotice(`已给 S${name} 添加选项，请在下方填写按钮文字`);
  };

  return (
    <>
      <button onClick={back} className="flex items-center gap-1.5 text-[12.5px] text-mut hover:text-on mb-3">
        <ArrowLeft size={15} /> 返回剧集
      </button>
      <PageHeader
        title={`剧情编辑 · ${data.title}`}
        sub={
          <span className="flex items-center gap-2">
            <StatusBadge status={data.status} />
            <span>{scenes.length} 个片段 · {endings} 个结局</span>
            {data.status === "published" && <span className="text-star">已上架：保存时剧情必须完整</span>}
          </span>
        }
        actions={
          <>
            {dirty && <span className="text-[12px] text-star">有未保存的修改</span>}
            <Button onClick={() => setPreviewing(true)} disabled={scenes.length === 0}><Play size={14} className="inline -mt-0.5 mr-1" />试玩</Button>
            <Button variant="primary" disabled={!dirty || save.isPending || missingTargets.length > 0} onClick={() => save.mutate(d)}>
              {save.isPending ? "保存中…" : "保存剧情"}
            </Button>
          </>
        }
      />

      {missingTargets.length > 0 && (
        <Card className="p-3 mb-3 text-[12.5px] text-danger">请为 {missingTargets.map((n) => `S${n}`).join("、")} 的每个选项选择「跳转到」的片段，才能保存。</Card>
      )}
      <ErrorText error={save.error} />
      {save.error instanceof ApiError && save.error.details.length > 0 && (
        <ul className="list-disc pl-5 mb-3 text-[12.5px] text-mut">{save.error.details.map((x) => <li key={x}>{x}</li>)}</ul>
      )}

      {scenes.length === 0 ? (
        <Card className="p-8 text-center text-[13px] text-mut">
          还没有片段。回到剧集页面上传视频（或用「文件夹上传」），每个视频就是一个片段（S1、S2…），再回来编排剧情。
        </Card>
      ) : (
        <>
          {(errors.length > 0 || warnings.length > 0) && (
            <Card className={`p-4 mb-4 border ${errors.length ? "border-danger/30" : "border-star/30"}`}>
              <div className={`text-[13px] font-semibold mb-1.5 ${errors.length ? "text-danger" : "text-star"}`}>
                {errors.length ? `发布前还需处理 ${errors.length} 个问题` : "提示"}{dirty && <span className="ml-2 text-[11.5px] font-normal text-dim">（按上次保存的剧情检查）</span>}
              </div>
              <ul className="space-y-0.5 text-[12.5px]">
                {[...errors, ...warnings].map((p) => (
                  <li key={`${p.level}${p.episode_id}${p.message}`} className="flex items-start gap-2">
                    <span className={p.level === "error" ? "text-danger" : "text-star"}>{p.level === "error" ? "✕" : "!"}</span>
                    {p.episode_id ? (
                      <button onClick={() => setSelected(p.episode_id!)} className="text-left text-mut hover:text-on underline-offset-2 hover:underline">{p.message}</button>
                    ) : <span className="text-mut">{p.message}</span>}
                  </li>
                ))}
              </ul>
            </Card>
          )}

          <Card className="p-4 mb-4">
            <div className="flex items-center gap-3 mb-2">
              <span className="text-[13px] font-semibold">剧情图</span>
              <span className="text-[11.5px] text-dim flex-1 min-w-0">拖动方框调整位置；从方框右侧的圆点拖到另一个方框，添加一个跳到那里的选项；点方框编辑该片段 · 虚线为自动接续 · 黄色为结局 · 红色虚框为还没设置的片段</span>
              {d.layout && (
                <Button variant="ghost" className="!h-7 !px-2 text-[12px] shrink-0" onClick={() => setDraft({ ...d, layout: null })}>
                  <LayoutGrid size={13} className="inline -mt-0.5 mr-1" />自动排列
                </Button>
              )}
            </div>
            {notice && <p className="mb-2 text-[12px] text-primary">{notice}</p>}
            <StoryMap scenes={scenes} draft={d} start={start} selected={selected} onSelect={(s) => { setSelected(s); setNotice(""); }}
              onLayout={(layout) => setDraft({ ...d, layout })} onConnect={connect} />
          </Card>

          <VariablesCard variables={d.variables} draft={d} onChange={(variables, scenesPatch) =>
            setDraft({ ...d, variables, scenes: scenesPatch ?? d.scenes })} />

          <div className="grid grid-cols-[260px_minmax(0,1fr)] gap-4 items-start">
            <Card className="p-2">
              {scenes.map((s) => {
                const sd = d.scenes[s.episode_id]!;
                return (
                  <button key={s.episode_id} onClick={() => setSelected(s.episode_id)}
                    className={`w-full grid grid-cols-[34px_minmax(0,1fr)_auto] items-center gap-2 px-2.5 py-2 rounded-(--r-sm) text-left text-[12.5px] ${selected === s.episode_id ? "bg-primary/12 text-on" : "text-mut hover:bg-(--p-hover)"}`}>
                    <b className="text-[11px] text-primary tabular-nums">S{s.ep_num}</b>
                    <span className="truncate">{s.title || "（未命名）"}{s.episode_id === start && <span className="ml-1.5 text-[10.5px] text-primary">起点</span>}</span>
                    <OutcomeTag outcome={sd.outcome} />
                  </button>
                );
              })}
            </Card>

            {current && (
              <SceneForm key={current.episode_id} scene={current} draft={d.scenes[current.episode_id]!} scenes={scenes} variables={d.variables}
                isStart={current.episode_id === start}
                onChange={(patch) => update(current.episode_id, patch)}
                onStart={() => setDraft((prev) => prev && { ...prev, start: current.episode_id })} />
            )}
          </div>
        </>
      )}

      <StoryPreviewDialog open={previewing} onClose={() => setPreviewing(false)} scenes={scenes} draft={d} start={start} />
    </>
  );
}

function OutcomeTag({ outcome }: { outcome: Outcome }) {
  const [label, style] = outcome === "choice" ? ["选项", "bg-s4 text-on"]
    : outcome === "continue" ? ["接续", "bg-s4 text-mut"]
    : outcome === "ending" ? ["结局", "bg-primary/20 text-primary"]
    : ["未设置", "bg-danger/15 text-danger"];
  return <span className={`px-1.5 py-0.5 rounded text-[10.5px] font-semibold whitespace-nowrap ${style}`}>{label}</span>;
}

/** 隐藏数值: scores such as 好感度 that options raise or lower, and that decide which options show. */
function VariablesCard({ variables, draft, onChange }: {
  variables: Variable[]; draft: Draft; onChange: (variables: Variable[], scenes?: Record<number, SceneDraft>) => void;
}) {
  const add = () => {
    const keyOf = () => `v${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
    onChange([...variables, { key: keyOf(), name: variables.length ? `数值${variables.length + 1}` : "好感度", initial: 0 }]);
  };
  const set = (k: string, patch: Partial<Variable>) => onChange(variables.map((v) => (v.key === k ? { ...v, ...patch } : v)));
  const remove = (v: Variable) => {
    const used = Object.values(draft.scenes).some((s) => s.options.some((o) => o.effects.some((e) => e.variable === v.key) || o.conditions.some((c) => c.variable === v.key)));
    if (used && !window.confirm(`「${v.name}」还在被选项使用，删除后会一并去掉这些数值变化和显示条件，确定？`)) return;
    const scenes = Object.fromEntries(Object.entries(draft.scenes).map(([sid, s]) => [sid, {
      ...s,
      options: s.options.map((o) => ({ ...o, effects: o.effects.filter((e) => e.variable !== v.key), conditions: o.conditions.filter((c) => c.variable !== v.key) })),
    }]));
    onChange(variables.filter((x) => x.key !== v.key), scenes);
  };
  return (
    <Card className="p-4 mb-4">
      <div className="flex items-center gap-3">
        <span className="text-[13px] font-semibold">隐藏数值</span>
        <span className="text-[11.5px] text-dim flex-1">观众看不到。选项可以让数值增减（比如「留下帮她」好感度 +1），也可以设置只在数值满足条件时才显示（比如好感度 ≥ 2）。</span>
        {variables.length < MAX_VARIABLES && <Button variant="ghost" className="!h-7 !px-2 text-[12px]" onClick={add}><Plus size={13} className="inline -mt-0.5 mr-1" />添加数值</Button>}
      </div>
      {variables.length > 0 && (
        <div className="mt-3 flex flex-wrap gap-3">
          {variables.map((v) => (
            <div key={v.key} className="flex items-center gap-2 rounded-(--r-sm) bg-s2 px-2.5 py-1.5">
              <input aria-label="数值名称" value={v.name} maxLength={MAX_VARIABLE_NAME} onChange={(e) => set(v.key, { name: e.target.value })} className={`${inputClass} !h-8 w-[110px]`} />
              <span className="text-[12px] text-dim">初始</span>
              <input aria-label={`${v.name} 初始值`} type="number" value={v.initial} onChange={(e) => set(v.key, { initial: Math.round(Number(e.target.value) || 0) })} className={`${inputClass} !h-8 w-[72px]`} />
              <button onClick={() => remove(v)} aria-label={`删除${v.name}`} className="p-1 text-dim hover:text-danger"><X size={14} /></button>
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}

/** The selected scene's settings: price, what happens when it ends, and its statistics. */
function SceneForm({ scene, draft, scenes, variables, isStart, onChange, onStart }: {
  scene: SceneDto; draft: SceneDraft; scenes: SceneDto[]; variables: Variable[]; isStart: boolean;
  onChange: (patch: Partial<SceneDraft>) => void; onStart: () => void;
}) {
  const [logicOpen, setLogicOpen] = useState<string | null>(null);
  const totalChosen = scene.options.reduce((n, o) => n + o.chosen, 0);
  const statsOf = (id: number | null) => scene.options.find((o) => o.id === id);
  const others = scenes.filter((s) => s.episode_id !== scene.episode_id);
  const setOption = (key: string, patch: Partial<OptionDraft>) =>
    onChange({ options: draft.options.map((o) => (o.key === key ? { ...o, ...patch } : patch.isDefault ? { ...o, isDefault: false } : o)) });
  const priceMode = draft.isFree ? "free" : draft.customPrice !== null ? "custom" : "series";

  return (
    <Card className="p-5 space-y-4">
      <div className="flex items-start gap-4">
        <div className="w-[72px] aspect-[9/16] rounded-(--r-sm) bg-s3 overflow-hidden shrink-0">
          {scene.poster_url && <img src={scene.poster_url} alt="" className="w-full h-full object-cover" />}
        </div>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-[16px] font-bold truncate">{sceneLabel(scene)}</span>
            <StatusBadge status={scene.status} />
            {isStart ? <span className="px-1.5 py-0.5 rounded bg-primary text-on-primary text-[10.5px] font-bold">起点</span>
              : <Button variant="ghost" className="!h-7 !px-2 text-[12px]" onClick={onStart}><Flag size={12} className="inline -mt-0.5 mr-1" />设为起点</Button>}
          </div>
          <div className="mt-1 text-[12px] text-dim flex flex-wrap gap-x-3">
            <span>{scene.duration > 0 ? formatDuration(scene.duration) : "时长未知"}</span>
            <span>{scene.viewers_here} 人停在这里</span>
            {scene.unlocks > 0 && <span>{scene.unlocks} 人付费解锁</span>}
          </div>
          <div className="mt-2 flex items-center gap-2 text-[12px] text-mut">
            <span>价格</span>
            <select aria-label="片段价格" value={priceMode} className={`${inputClass} !h-8 w-[200px]`} onChange={(e) => {
              const mode = e.target.value;
              onChange(mode === "free" ? { isFree: true, customPrice: null } : mode === "custom" ? { isFree: false, customPrice: draft.customPrice ?? 60 } : { isFree: false, customPrice: null });
            }}>
              <option value="series">按剧集的付费设置{priceMode === "series" ? `（${scene.price > 0 ? `${scene.price} 金币` : "免费"}）` : ""}</option>
              <option value="free">始终免费</option>
              <option value="custom">单独定价</option>
            </select>
            {priceMode === "custom" && (
              <>
                <input aria-label="单独定价（金币）" type="number" min={0} max={10000} value={draft.customPrice ?? 0}
                  onChange={(e) => onChange({ customPrice: Math.max(0, Math.min(10000, Math.round(Number(e.target.value) || 0))) })} className={`${inputClass} !h-8 w-[90px]`} />
                <span>金币</span>
              </>
            )}
          </div>
          {priceMode === "custom" && <p className="mt-1 text-[11px] text-dim">比如特别结局单独收费：选项上会显示价格，观众解锁后才能看。</p>}
        </div>
      </div>

      <FieldGroup label="片段播完后">
        <div className="flex gap-1 p-1 rounded-(--r-ctl) bg-s3 w-fit">
          {([["choice", "出现选项"], ["continue", "自动接续"], ["ending", "这是结局"]] as const).map(([v, l]) => (
            <button type="button" key={v} onClick={() => onChange({ outcome: v, options: v === "choice" && draft.options.length === 0 ? [blankOption(), blankOption()] : draft.options })}
              className={`px-3 h-7 rounded-(--r-ctl) text-[12.5px] ${draft.outcome === v ? "bg-sec text-primary font-semibold" : "text-mut"}`}>{l}</button>
          ))}
        </div>
      </FieldGroup>

      {draft.outcome === "unset" && <p className="text-[12.5px] text-dim">还没设置。观众能看到这个片段时，必须设置播完后怎么走才能发布。</p>}

      {draft.outcome === "choice" && (
        <>
          <Field label={`选择提示（显示在选项上方，最多 ${MAX_PROMPT} 字）`}>
            <input value={draft.prompt} maxLength={MAX_PROMPT} onChange={(e) => onChange({ prompt: e.target.value })} placeholder="门外是消失三年的前任，你要？" className={inputClass} />
          </Field>
          <div className="grid grid-cols-2 gap-4">
            <Field label="选项出现">
              <select value={draft.lead} onChange={(e) => onChange({ lead: Number(e.target.value) })} className={inputClass}>
                {LEADS.map((l) => <option key={l} value={l}>{l === 0 ? "视频播完时（停在最后一帧）" : `结束前 ${l} 秒（视频继续播放）`}</option>)}
              </select>
            </Field>
            <Field label="倒计时" hint={draft.countdown ? "时间到自动播放默认选项（默认选项不显示时，播放第一个显示的选项）" : "选项一直等观众选择"}>
              <select value={draft.countdown ?? ""} onChange={(e) => {
                const countdown = e.target.value ? Number(e.target.value) : null;
                const needsDefault = countdown && !draft.options.some((o) => o.isDefault) && draft.options.length > 0;
                onChange({ countdown, options: needsDefault ? draft.options.map((o, i) => ({ ...o, isDefault: i === 0 })) : draft.options });
              }} className={inputClass}>
                {COUNTDOWNS.map((c) => <option key={c ?? 0} value={c ?? ""}>{c ? `${c} 秒` : "不倒计时"}</option>)}
              </select>
            </Field>
          </div>

          <div>
            <div className="text-[12px] text-mut mb-1.5">选项（2-{MAX_OPTIONS} 个）{totalChosen > 0 && <span className="ml-2 text-dim">共选择 {totalChosen} 次</span>}</div>
            <table className="w-full text-[12.5px]">
              <thead>
                <tr className="text-left text-[11.5px] text-dim">
                  <th className="font-normal pb-1.5 w-[44px]">默认</th>
                  <th className="font-normal pb-1.5">按钮文字（≤{MAX_LABEL} 字）</th>
                  <th className="font-normal pb-1.5">说明（选填，≤{MAX_DESCRIPTION} 字）</th>
                  <th className="font-normal pb-1.5">跳转到</th>
                  <th className="font-normal pb-1.5 w-[86px]">数据</th>
                  <th className="w-[66px]" />
                </tr>
              </thead>
              <tbody>
                {draft.options.flatMap((o) => {
                  const stats = statsOf(o.id);
                  const logic = o.effects.length + o.conditions.length;
                  const rows = [
                    <tr key={o.key}>
                      <td className="py-1"><input type="radio" name={`default-${scene.episode_id}`} checked={o.isDefault} onChange={() => setOption(o.key, { isDefault: true })} aria-label="默认选项" /></td>
                      <td className="py-1 pr-2"><input value={o.label} maxLength={MAX_LABEL} onChange={(e) => setOption(o.key, { label: e.target.value })} placeholder="开门让她进来" className={`${inputClass} w-full`} /></td>
                      <td className="py-1 pr-2"><input value={o.description} maxLength={MAX_DESCRIPTION} onChange={(e) => setOption(o.key, { description: e.target.value })} placeholder="听听她想说什么" className={`${inputClass} w-full`} /></td>
                      <td className="py-1 pr-2">
                        <select value={o.target ?? ""} onChange={(e) => setOption(o.key, { target: e.target.value ? Number(e.target.value) : null })} className={`${inputClass} w-full ${o.target === null ? "ring-1 ring-danger" : ""}`}>
                          <option value="">选择片段…</option>
                          {scenes.map((s) => <option key={s.episode_id} value={s.episode_id}>{sceneLabel(s)}</option>)}
                        </select>
                      </td>
                      <td className="py-1 text-[11.5px] text-dim tabular-nums">
                        {stats && stats.chosen > 0 ? <>{Math.round((stats.chosen * 100) / Math.max(1, totalChosen))}% · {stats.chosen} 次{stats.timed_out > 0 && <div>超时 {stats.timed_out}</div>}</> : "—"}
                      </td>
                      <td className="py-1 whitespace-nowrap">
                        <button onClick={() => setLogicOpen(logicOpen === o.key ? null : o.key)} aria-label="数值与显示条件" title="数值与显示条件"
                          className={`p-1.5 ${logic ? "text-primary" : "text-dim hover:text-on"}`}><SlidersHorizontal size={14} />{logic > 0 && <span className="text-[10.5px] ml-0.5">{logic}</span>}</button>
                        <button onClick={() => onChange({ options: draft.options.filter((x) => x.key !== o.key) })} aria-label="删除选项" className="p-1.5 text-dim hover:text-danger"><Trash2 size={14} /></button>
                      </td>
                    </tr>,
                  ];
                  if (logicOpen === o.key) {
                    rows.push(
                      <tr key={`${o.key}-logic`}>
                        <td />
                        <td colSpan={5} className="pb-2">
                          <OptionLogic option={o} variables={variables} onChange={(patch) => setOption(o.key, patch)} />
                        </td>
                      </tr>,
                    );
                  } else if (logic > 0) {
                    rows.push(
                      <tr key={`${o.key}-summary`}>
                        <td />
                        <td colSpan={5} className="pb-1 text-[11.5px] text-dim">
                          {[
                            ...o.effects.map((e) => `${variables.find((v) => v.key === e.variable)?.name ?? "?"} ${signed(e.add)}`),
                            ...o.conditions.map((c) => `${variables.find((v) => v.key === c.variable)?.name ?? "?"} ${opLabel(c.op)} ${c.value} 时显示`),
                          ].join(" · ")}
                        </td>
                      </tr>,
                    );
                  }
                  return rows;
                })}
              </tbody>
            </table>
            {draft.options.length < MAX_OPTIONS && (
              <button onClick={() => onChange({ options: [...draft.options, blankOption()] })} className="mt-1.5 text-[12.5px] text-primary flex items-center gap-1"><Plus size={13} />添加选项</button>
            )}
          </div>
        </>
      )}

      {draft.outcome === "continue" && (
        <Field label="接着播放" hint="不出现选项，播完直接进入这个片段（用来让不同分支汇合）">
          <select value={draft.next ?? ""} onChange={(e) => onChange({ next: e.target.value ? Number(e.target.value) : null })} className={`${inputClass} max-w-[360px]`}>
            <option value="">选择片段…</option>
            {others.map((s) => <option key={s.episode_id} value={s.episode_id}>{sceneLabel(s)}</option>)}
          </select>
        </Field>
      )}

      {draft.outcome === "ending" && (
        <Field label={`结局名称（最多 ${MAX_ENDING} 字）`} hint={`观众到达后显示「结局 n / N ·「名称」」；没到达的结局显示为 ？？？。已有 ${scene.ending_reached} 人到达这个结局。`}>
          <input value={draft.ending} maxLength={MAX_ENDING} onChange={(e) => onChange({ ending: e.target.value })} placeholder="真相" className={`${inputClass} max-w-[360px]`} />
        </Field>
      )}
    </Card>
  );
}

/** One option's effects on the scores (好感度 +1) and the conditions for it to show (好感度 ≥ 2). */
function OptionLogic({ option, variables, onChange }: {
  option: OptionDraft; variables: Variable[]; onChange: (patch: Partial<OptionDraft>) => void;
}) {
  const [effect, setEffect] = useState<Effect>({ variable: variables[0]?.key ?? "", add: 1 });
  const [condition, setCondition] = useState<Condition>({ variable: variables[0]?.key ?? "", op: "gte", value: 1 });
  const nameOf = (k: string) => variables.find((v) => v.key === k)?.name ?? "?";
  if (variables.length === 0) return <p className="text-[12px] text-dim bg-s2 rounded-(--r-sm) px-3 py-2">先在上方「隐藏数值」里添加一个数值（比如 好感度），再设置这个选项的数值变化和显示条件。</p>;
  const small = `${inputClass} !h-8`;
  return (
    <div className="bg-s2 rounded-(--r-sm) px-3 py-2.5 grid grid-cols-2 gap-4 text-[12px]">
      <div className="space-y-1.5">
        <div className="text-mut">选择后数值变化（最多 {MAX_EFFECTS} 个）</div>
        {option.effects.map((e, i) => (
          <div key={`${e.variable}${i}`} className="flex items-center gap-2">
            <span className="px-2 py-1 rounded bg-s3">{nameOf(e.variable)} {signed(e.add)}</span>
            <button aria-label="去掉" onClick={() => onChange({ effects: option.effects.filter((_, j) => j !== i) })} className="text-dim hover:text-danger"><X size={13} /></button>
          </div>
        ))}
        {option.effects.length < MAX_EFFECTS && (
          <div className="flex items-center gap-1.5">
            <select aria-label="变化的数值" value={effect.variable} onChange={(e) => setEffect({ ...effect, variable: e.target.value })} className={`${small} w-[110px]`}>
              {variables.map((v) => <option key={v.key} value={v.key}>{v.name}</option>)}
            </select>
            <input aria-label="增减多少" type="number" value={effect.add} onChange={(e) => setEffect({ ...effect, add: Math.round(Number(e.target.value) || 0) })} className={`${small} w-[70px]`} />
            <Button className="!h-8 !px-2.5" disabled={!effect.variable || effect.add === 0}
              onClick={() => onChange({ effects: [...option.effects, { variable: effect.variable || variables[0]!.key, add: effect.add }] })}>添加</Button>
          </div>
        )}
      </div>
      <div className="space-y-1.5">
        <div className="text-mut">显示条件（全部满足才显示，最多 {MAX_CONDITIONS} 个）</div>
        {option.conditions.map((c, i) => (
          <div key={`${c.variable}${i}`} className="flex items-center gap-2">
            <span className="px-2 py-1 rounded bg-s3">{nameOf(c.variable)} {opLabel(c.op)} {c.value}</span>
            <button aria-label="去掉" onClick={() => onChange({ conditions: option.conditions.filter((_, j) => j !== i) })} className="text-dim hover:text-danger"><X size={13} /></button>
          </div>
        ))}
        {option.conditions.length < MAX_CONDITIONS && (
          <div className="flex items-center gap-1.5">
            <select aria-label="条件的数值" value={condition.variable} onChange={(e) => setCondition({ ...condition, variable: e.target.value })} className={`${small} w-[110px]`}>
              {variables.map((v) => <option key={v.key} value={v.key}>{v.name}</option>)}
            </select>
            <select aria-label="比较" value={condition.op} onChange={(e) => setCondition({ ...condition, op: e.target.value })} className={`${small} w-[56px]`}>
              {OPS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
            <input aria-label="条件的值" type="number" value={condition.value} onChange={(e) => setCondition({ ...condition, value: Math.round(Number(e.target.value) || 0) })} className={`${small} w-[70px]`} />
            <Button className="!h-8 !px-2.5" disabled={!condition.variable}
              onClick={() => onChange({ conditions: [...option.conditions, { ...condition, variable: condition.variable || variables[0]!.key }] })}>添加</Button>
          </div>
        )}
      </div>
    </div>
  );
}

const blankOption = (): OptionDraft => ({ key: newKey(), id: null, label: "", description: "", target: null, isDefault: false, effects: [], conditions: [] });

const NODE_W = 160;
const NODE_H = 52;
const COL_GAP = 80;
const ROW_GAP = 22;
const PAD = 28;

/**
 * The story drawn from the draft. Scenes the admin placed keep their place; the others are laid out in columns by
 * distance from the start (scenes nothing leads to in a last column). Drag a box to move it; drag from the dot on its
 * right edge onto another box to add an option leading there.
 */
function StoryMap({ scenes, draft, start, selected, onSelect, onLayout, onConnect }: {
  scenes: SceneDto[]; draft: Draft; start: number | null; selected: number | null; onSelect: (id: number) => void;
  onLayout: (layout: Record<number, Point>) => void; onConnect: (from: number, to: number) => void;
}) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [drag, setDrag] = useState<{ id: number; dx: number; dy: number; x: number; y: number; moved: boolean } | null>(null);
  const [link, setLink] = useState<{ from: number; x: number; y: number } | null>(null);

  const auto = useMemo(() => {
    const depth = new Map<number, number>();
    if (start !== null) {
      depth.set(start, 0);
      const queue = [start];
      while (queue.length) {
        const sid = queue.shift()!;
        for (const e of exitsOf(draft.scenes[sid])) {
          if (!depth.has(e.to) && scenes.some((s) => s.episode_id === e.to)) {
            depth.set(e.to, depth.get(sid)! + 1);
            queue.push(e.to);
          }
        }
      }
    }
    const maxDepth = Math.max(0, ...depth.values());
    const columns = new Map<number, SceneDto[]>();
    for (const s of scenes) {
      const col = depth.get(s.episode_id) ?? maxDepth + 1;
      columns.set(col, [...(columns.get(col) ?? []), s]);
    }
    const pos = new Map<number, Point>();
    for (const [col, list] of columns) list.forEach((s, row) => pos.set(s.episode_id, { x: 16 + col * (NODE_W + COL_GAP), y: PAD + row * (NODE_H + ROW_GAP) }));
    return { pos, depth };
  }, [scenes, draft.scenes, start]);

  const at = (sid: number): Point => (drag?.id === sid ? { x: drag.x, y: drag.y } : draft.layout?.[sid] ?? auto.pos.get(sid) ?? { x: 0, y: 0 });
  const all = scenes.map((s) => at(s.episode_id));
  const width = Math.max(600, ...all.map((p) => p.x + NODE_W + 40));
  const height = Math.max(140, ...all.map((p) => p.y + NODE_H + 30));

  const toSvg = (e: { clientX: number; clientY: number }): Point => {
    const svg = svgRef.current;
    const m = svg?.getScreenCTM();
    if (!svg || !m) return { x: 0, y: 0 };
    const pt = svg.createSVGPoint();
    pt.x = e.clientX;
    pt.y = e.clientY;
    const p = pt.matrixTransform(m.inverse());
    return { x: p.x, y: p.y };
  };
  const hit = (p: Point) => scenes.find((s) => { const q = at(s.episode_id); return p.x >= q.x && p.x <= q.x + NODE_W && p.y >= q.y && p.y <= q.y + NODE_H; });

  const startDrag = (e: ReactPointerEvent, sid: number) => {
    if (e.button !== 0) return;
    e.preventDefault();
    const p = toSvg(e);
    const q = at(sid);
    setDrag({ id: sid, dx: p.x - q.x, dy: p.y - q.y, x: q.x, y: q.y, moved: false });
    (e.currentTarget as Element).setPointerCapture?.(e.pointerId);
  };
  const startLink = (e: ReactPointerEvent, sid: number) => {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    setLink({ from: sid, ...toSvg(e) });
    (e.currentTarget as Element).setPointerCapture?.(e.pointerId);
  };
  const move = (e: ReactPointerEvent) => {
    if (drag) {
      const p = toSvg(e);
      const x = Math.max(0, Math.round((p.x - drag.dx) / 10) * 10);
      const y = Math.max(0, Math.round((p.y - drag.dy) / 10) * 10);
      if (x !== drag.x || y !== drag.y) setDrag({ ...drag, x, y, moved: true });
    } else if (link) {
      setLink({ ...link, ...toSvg(e) });
    }
  };
  const end = (e: ReactPointerEvent) => {
    if (drag) {
      if (drag.moved) {
        // The first move freezes the automatic layout, so the other boxes stay where they are.
        onLayout(Object.fromEntries(scenes.map((s) => [s.episode_id, s.episode_id === drag.id ? { x: drag.x, y: drag.y } : draft.layout?.[s.episode_id] ?? auto.pos.get(s.episode_id)!])));
      } else {
        onSelect(drag.id);
      }
      setDrag(null);
    } else if (link) {
      const target = hit(toSvg(e));
      if (target && target.episode_id !== link.from) onConnect(link.from, target.episode_id);
      setLink(null);
    }
  };

  const edges = scenes.flatMap((s) => {
    const exits = exitsOf(draft.scenes[s.episode_id]);
    return exits.map((x, i) => ({ from: s.episode_id, ...x, i, n: exits.length }));
  });

  return (
    <div className="overflow-x-auto">
      <svg ref={svgRef} width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label="剧情图" className="block select-none"
        onPointerMove={move} onPointerUp={end} onPointerCancel={() => { setDrag(null); setLink(null); }}>
        <defs>
          <marker id="story-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M0,0 L10,5 L0,10 z" fill="var(--p-mut)" />
          </marker>
        </defs>
        {edges.map((e) => {
          const a = at(e.from);
          const b = at(e.to);
          if (!scenes.some((s) => s.episode_id === e.to)) return null;
          const y1 = a.y + NODE_H / 2 + (e.n > 1 ? (e.i - (e.n - 1) / 2) * 12 : 0);
          const x1 = a.x + NODE_W;
          const forward = b.x > a.x + NODE_W / 2;
          const x2 = forward ? b.x - 2 : b.x + NODE_W / 2;
          const y2 = forward ? b.y + NODE_H / 2 : b.y + NODE_H + 2;
          const gap = Math.max(40, (x2 - x1) / 2);
          const path = forward
            ? `M${x1},${y1} C${x1 + gap},${y1} ${x2 - gap},${y2} ${x2},${y2}`
            : `M${x1},${y1} C${x1 + 50},${y1 + 60} ${x2},${y2 + 60} ${x2},${y2}`;
          const label = e.label.length > 7 ? `${e.label.slice(0, 7)}…` : e.label;
          const mx = forward ? (x1 + x2) / 2 : x1 + 18;
          const my = forward ? (y1 + y2) / 2 : y1 + 26;
          const w = label.length * 12 + 12;
          return (
            <g key={`${e.from}-${e.i}`} onClick={() => onSelect(e.from)} style={{ cursor: "pointer" }}>
              <path d={path} fill="none" stroke="var(--p-mut)" strokeWidth={1.3} strokeDasharray={e.auto ? "5 4" : undefined} markerEnd="url(#story-arrow)" />
              <rect x={mx - w / 2} y={my - 9} width={w} height={18} rx={9} fill="var(--p-s1)" stroke="var(--p-s4)" />
              <text x={mx} y={my + 4} textAnchor="middle" fontSize={11} fill="var(--p-on)">{label}</text>
            </g>
          );
        })}
        {scenes.map((s) => {
          const p = at(s.episode_id);
          const sd = draft.scenes[s.episode_id]!;
          const reached = auto.depth.has(s.episode_id);
          const isSel = s.episode_id === selected;
          const ending = sd.outcome === "ending";
          const unset = sd.outcome === "unset" && reached;
          const title = s.title || "（未命名）";
          const price = sd.isFree ? 0 : sd.customPrice;
          return (
            <g key={s.episode_id} style={{ opacity: reached ? 1 : 0.5 }}>
              {s.episode_id === start && <text x={p.x} y={p.y - 7} fontSize={11} fontWeight={700} fill="var(--p-primary)">起点</text>}
              <g onPointerDown={(e) => startDrag(e, s.episode_id)} style={{ cursor: drag?.id === s.episode_id ? "grabbing" : "grab" }}>
                <rect x={p.x} y={p.y} width={NODE_W} height={NODE_H} rx={8}
                  fill={ending ? "color-mix(in srgb, var(--p-primary) 14%, var(--p-s2))" : "var(--p-s2)"}
                  stroke={isSel ? "var(--p-primary)" : unset ? "var(--p-danger)" : ending ? "var(--p-primary)" : "var(--p-s4)"}
                  strokeWidth={isSel ? 2.2 : 1.2} strokeDasharray={unset ? "5 3" : undefined} />
                <text x={p.x + 10} y={p.y + 21} fontSize={12} fontWeight={700} fill="var(--p-primary)">S{s.ep_num}</text>
                <text x={p.x + 38} y={p.y + 21} fontSize={12} fill="var(--p-on)">{title.length > 8 ? `${title.slice(0, 8)}…` : title}</text>
                <text x={p.x + 10} y={p.y + 40} fontSize={11} fill="var(--p-mut)">
                  {ending ? `结局 · ${sd.ending || "未命名"}` : sd.outcome === "choice" ? `${sd.options.length} 个选项` : sd.outcome === "continue" ? "自动接续" : "未设置"}
                  {price ? ` · ${price} 金币` : ""}
                  {s.status !== "ready" ? " · 未就绪" : ""}
                </text>
              </g>
              <circle cx={p.x + NODE_W} cy={p.y + NODE_H / 2} r={7} fill="var(--p-s1)" stroke="var(--p-primary)" strokeWidth={1.6}
                onPointerDown={(e) => startLink(e, s.episode_id)} style={{ cursor: "crosshair" }} aria-label={`从 S${s.ep_num} 连线`} />
            </g>
          );
        })}
        {link && (() => {
          const a = at(link.from);
          return <line x1={a.x + NODE_W} y1={a.y + NODE_H / 2} x2={link.x} y2={link.y} stroke="var(--p-primary)" strokeWidth={1.6} strokeDasharray="4 3" pointerEvents="none" />;
        })()}
      </svg>
    </div>
  );
}

/** 试玩: plays the draft story (saved or not) with every scene unlocked, following the hidden scores; any scene can be the start. */
function StoryPreviewDialog({ open, onClose, scenes, draft, start }: {
  open: boolean; onClose: () => void; scenes: SceneDto[]; draft: Draft; start: number | null;
}) {
  const [current, setCurrent] = useState<number | null>(start);
  const [phase, setPhase] = useState<"playing" | "choice" | "ending">("playing");
  const [left, setLeft] = useState(0);
  const [effects, setEffects] = useState<Effect[]>([]);
  useEffect(() => { if (open) { setCurrent(start); setPhase("playing"); setEffects([]); } }, [open, start]);

  const scene = scenes.find((s) => s.episode_id === current);
  const sd = current !== null ? draft.scenes[current] : undefined;
  const scores = useMemo(() => {
    const out: Record<string, number> = Object.fromEntries(draft.variables.map((v) => [v.key, v.initial]));
    for (const e of effects) if (e.variable in out) out[e.variable] = out[e.variable]! + e.add;
    return out;
  }, [draft.variables, effects]);
  const shown = useMemo(() => {
    if (!sd) return [];
    const visible = sd.options.filter((o) => o.conditions.every((c) => !(c.variable in scores) || holds(c, scores[c.variable]!)));
    return visible.length ? visible : sd.options;
  }, [sd, scores]);
  const preview = useQuery({
    queryKey: ["admin", "episode-preview", current],
    queryFn: async () => unwrap(await api.GET("/api/admin/episodes/{id}/preview", { params: { path: { id: current! } } })),
    enabled: open && current !== null,
    staleTime: 0,
    gcTime: 0,
    retry: false,
  });

  const go = (to: number | null, option?: OptionDraft) => {
    if (to === null) return;
    if (option) setEffects((prev) => [...prev, ...option.effects]);
    setCurrent(to);
    setPhase("playing");
  };
  const restart = () => { setEffects([]); go(start); };
  const ended = () => {
    if (!sd) return;
    setLeft((sd.countdown ?? 0) * 1000);
    if (sd.outcome === "choice") setPhase("choice");
    else if (sd.outcome === "continue") go(sd.next);
    else setPhase("ending");
  };

  // The countdown, as viewers see it: the default option when it shows, else the first one shown.
  useEffect(() => {
    if (phase !== "choice" || !sd?.countdown) return undefined;
    const fallback = shown.find((o) => o.isDefault && o.target !== null) ?? shown.find((o) => o.target !== null);
    if (!fallback) return undefined;
    const started = Date.now();
    const total = sd.countdown * 1000;
    setLeft(total);
    const timer = window.setInterval(() => {
      const remaining = Math.max(0, total - (Date.now() - started));
      setLeft(remaining);
      if (remaining <= 0) { window.clearInterval(timer); go(fallback.target, fallback); }
    }, 200);
    return () => window.clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, sd, current]);

  return (
    <Dialog open={open} title={`试玩${scene ? ` · ${sceneLabel(scene)}` : ""}`} width={460} onClose={onClose}>
      <div className="relative bg-black rounded-(--r-md) overflow-hidden">
        {preview.data ? (
          <PreviewPlayer key={`${preview.data.url}#${preview.dataUpdatedAt}`} preview={preview.data} aspect={9 / 16}
            onSize={() => {}} onRetry={() => void preview.refetch()} onEnded={ended} />
        ) : (
          <div className="flex items-center justify-center text-[13px] text-mut" style={{ aspectRatio: "9 / 16", maxHeight: "70vh", margin: "0 auto" }}>
            {preview.isError ? (preview.error instanceof Error ? preview.error.message : "无法预览") : "加载中…"}
          </div>
        )}
        {phase === "choice" && sd && (
          <div className="absolute inset-x-0 bottom-0 p-4 flex flex-col gap-2" style={{ background: "linear-gradient(180deg, transparent, rgba(0,0,0,.88) 35%)" }}>
            {sd.prompt && <p className="text-[15px] font-bold text-white">{sd.prompt}</p>}
            {sd.countdown && <p className="text-[11.5px] text-white/60">{Math.ceil(left / 1000)} 秒后自动选择默认选项</p>}
            {shown.map((o) => (
              <button key={o.key} onClick={() => go(o.target, o)} disabled={o.target === null}
                className={`text-left rounded-xl px-3 py-2 bg-white/10 hover:bg-white/15 border ${o.isDefault && sd.countdown ? "border-primary" : "border-white/15"} disabled:opacity-50`}>
                <span className="block text-[14px] font-semibold text-white">{o.label || "（未命名）"}</span>
                {o.description && <span className="block text-[11.5px] text-white/60">{o.description}</span>}
              </button>
            ))}
            {shown.length < sd.options.length && <p className="text-[11px] text-white/50">有 {sd.options.length - shown.length} 个选项因数值条件没有显示</p>}
          </div>
        )}
        {phase === "ending" && sd && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-black/80 text-center px-6">
            <span className="text-[12px] tracking-[.14em] font-semibold text-primary">{sd.outcome === "ending" ? "结局" : "到这里就结束了（未设置后续）"}</span>
            {sd.outcome === "ending" && <span className="text-[22px] font-bold text-white">「{sd.ending || "未命名"}」</span>}
            <Button variant="primary" onClick={restart}><RotateCcw size={14} className="inline -mt-0.5 mr-1" />重新开始</Button>
          </div>
        )}
      </div>
      <div className="mt-3 flex items-center gap-2 text-[12px] text-dim">
        <span>从这里开始</span>
        <select value={current ?? ""} onChange={(e) => go(e.target.value ? Number(e.target.value) : null)} className={`${inputClass} !h-8 flex-1`}>
          {scenes.map((s) => <option key={s.episode_id} value={s.episode_id}>{sceneLabel(s)}</option>)}
        </select>
        {phase === "playing" && sd && sd.outcome !== "unset" && <Button className="!h-8 !px-3" onClick={ended}>跳到结尾</Button>}
      </div>
      {draft.variables.length > 0 && (
        <p className="mt-2 text-[12px] text-mut">隐藏数值：{draft.variables.map((v) => `${v.name} ${scores[v.key]}`).join(" · ")}</p>
      )}
      <p className="mt-2 text-[11.5px] text-dim">试玩用的是当前编辑中的剧情（包括未保存的修改），所有片段都已解锁，不计入数据。</p>
    </Dialog>
  );
}
