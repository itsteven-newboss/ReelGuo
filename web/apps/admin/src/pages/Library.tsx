import { ApiError, unwrap, type Schemas } from "@reelguo/api-client";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, FolderOpen, Pause, Play, RefreshCw, Square, Upload } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from "react";
import {
  buildIndex, canPickFolders, canRead, listingFor, pickFolder, rememberFolder, rememberedFolder, videoKey, walkFileList, walkHandle,
  type FolderIndex, type TitlePlan,
} from "../library/scan";
import { api, CSRF_HEADER } from "../session";
import { formatBytes, uploadFile } from "../upload";
import { Button, Card, ErrorText, Field, inputClass, PageHeader } from "../ui";

type Plan = Schemas["LibraryPlan"];
type EpisodePlan = Schemas["LibraryEpisodePlan"];

/** States the runner can upload; everything else is shown but left alone. */
const SENDABLE = new Set(["new", "stored", "failed"]);
const RESCAN_MS = 3 * 60_000;
const PARALLEL = 2;
const ATTEMPTS = 3;
const MAX_COVER_BYTES = 5 * 1024 * 1024;

const TITLE_STATUS: Record<string, [string, string]> = {
  new: ["新剧", "bg-pc text-on-pc"],
  updates: ["有更新", "bg-star/20 text-star"],
  synced: ["已同步", "bg-success/20 text-success"],
  attention: ["需处理", "bg-danger/20 text-danger"],
};

const EPISODE_STATE: Record<string, [string, string]> = {
  new: ["待上传", "bg-s4 text-on"],
  stored: ["已在 COS", "bg-pc text-on-pc"],
  failed: ["失败，将重试", "bg-danger/20 text-danger"],
  uploaded: ["已上传", "bg-success/20 text-success"],
  processing: ["处理中", "bg-pc text-on-pc"],
  changed: ["文件已变化", "bg-star/20 text-star"],
  incomplete: ["未下载完", "bg-s3 text-dim"],
  unplayable: ["格式不支持", "bg-danger/20 text-danger"],
  held: ["暂不上传", "bg-s3 text-dim"],
  duplicate: ["集数重复", "bg-danger/20 text-danger"],
  unnumbered: ["无集数", "bg-danger/20 text-danger"],
  uploading: ["上传中", "bg-primary text-on-primary"],
  done: ["已完成", "bg-success/20 text-success"],
  error: ["失败", "bg-danger text-on-danger"],
};

type Scanned = { idx: FolderIndex; plan: Plan };
type Live = Record<string, { state: "uploading" | "done" | "error"; progress: number; error?: string }>;
const liveKey = (title: string, n: number) => `${title}\n${n}`;

function readPref(key: string, fallback: string) {
  try {
    return window.localStorage.getItem(key) ?? fallback;
  } catch {
    return fallback;
  }
}
function writePref(key: string, value: string) {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    /* storage blocked: the choice is simply not remembered */
  }
}

const errorText = (e: unknown) => (e instanceof ApiError || e instanceof Error ? e.message : "操作失败");
const sleep = (ms: number, signal: AbortSignal) => new Promise<void>((resolve) => {
  const t = window.setTimeout(resolve, ms);
  signal.addEventListener("abort", () => { window.clearTimeout(t); resolve(); }, { once: true });
});

/**
 * Folder upload: drop the delivery folder (VideoDetails/, Images/, Videos/<title>/<title>_<n>.mp4) once and every
 * finished episode goes straight from this browser to storage. With Tencent COS that is
 * videos/{storage folder}/{title folder}/{file}, the layout the Viora platform uses; files already there are linked
 * instead of sent again. The server decides what is missing; this page only lists the folder and sends files.
 */
export function LibraryPage({ onOpenSeries }: { onOpenSeries: (id: number) => void }) {
  const config = useQuery({
    queryKey: ["admin", "library", "config"],
    queryFn: async () => unwrap(await api.GET("/api/admin/library")),
  });
  const categories = useQuery({
    queryKey: ["admin", "categories"],
    queryFn: async () => unwrap(await api.GET("/api/admin/categories")),
  });

  const [folder, setFolder] = useState(() => readPref("rg.library.folder", ""));
  const [category, setCategory] = useState(() => readPref("rg.library.category", ""));
  const [rights, setRights] = useState(() => {
    try {
      return JSON.parse(readPref("rg.library.rights", "{}")) as Partial<Schemas["LibraryRights"]>;
    } catch {
      return {};
    }
  });
  useEffect(() => { if (!folder && config.data) setFolder(config.data.default_folder); }, [config.data, folder]);
  const openCategories = useMemo(() => (categories.data ?? []).filter((c) => c.status === "active"), [categories.data]);
  useEffect(() => { if (!category && openCategories[0]) setCategory(openCategories[0].slug); }, [openCategories, category]);

  const [handle, setHandle] = useState<FileSystemDirectoryHandle | null>(null);
  const [remembered, setRemembered] = useState<FileSystemDirectoryHandle | null>(null);
  const [index, setIndex] = useState<FolderIndex | null>(null);
  const [scan, setScan] = useState<{ done: number; total: number } | null>(null);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [live, setLive] = useState<Live>({});
  const [running, setRunning] = useState(false);
  const [paused, setPaused] = useState(false);
  const [keepUploading, setKeepUploading] = useState(true);
  const [log, setLog] = useState<string[]>([]);
  const fileInput = useRef<HTMLInputElement>(null);
  const stopper = useRef<AbortController | null>(null);
  const pausedRef = useRef(false);
  pausedRef.current = paused;

  useEffect(() => { void rememberedFolder().then(setRemembered); }, []);

  const note = (line: string) => setLog((l) => [`${new Date().toLocaleTimeString("zh-CN", { hour12: false })} ${line}`, ...l].slice(0, 200));

  const askPlan = useCallback(async (idx: FolderIndex) => {
    const result = unwrap(await api.POST("/api/admin/library/plan", { body: { storage_folder: folder || null, listing: idx.listing } }));
    setPlan(result);
    return result;
  }, [folder]);

  const scanFolder = useCallback(async (source: FileSystemDirectoryHandle | File[]): Promise<Scanned | null> => {
    setError(null);
    setScan({ done: 0, total: 0 });
    try {
      const entries = Array.isArray(source) ? walkFileList(source) : walkHandle(source);
      const idx = await buildIndex(entries, (done, total) => setScan({ done, total }));
      setIndex(idx);
      return { idx, plan: await askPlan(idx) };
    } catch (e) {
      setError(e);
      return null;
    } finally {
      setScan(null);
    }
  }, [askPlan]);

  async function choose() {
    if (!canPickFolders()) {
      fileInput.current?.click();
      return;
    }
    const picked = await pickFolder();
    if (picked === "cancelled") return;
    if (picked === "failed") {
      fileInput.current?.click();
      return;
    }
    setHandle(picked);
    setRemembered(picked);
    await rememberFolder(picked);
    await scanFolder(picked);
  }

  async function reopen(h: FileSystemDirectoryHandle) {
    if (!(await canRead(h, true))) return;
    setHandle(h);
    await scanFolder(h);
  }

  async function onDrop(e: DragEvent) {
    e.preventDefault();
    const item = e.dataTransfer.items?.[0] as (DataTransferItem & { getAsFileSystemHandle?: () => Promise<FileSystemHandle | null> }) | undefined;
    const h = item?.getAsFileSystemHandle ? await item.getAsFileSystemHandle() : null;
    if (h?.kind === "directory") {
      const dir = h as FileSystemDirectoryHandle;
      setHandle(dir);
      setRemembered(dir);
      await rememberFolder(dir);
      await scanFolder(dir);
    } else {
      setError(new Error("请拖入整个文件夹，或点击「选择文件夹」"));
    }
  }

  // ---------- the runner ----------

  const setOne = (key: string, value: Live[string]) => setLive((l) => ({ ...l, [key]: value }));

  async function ensureSeries(t: TitlePlan, idx: FolderIndex): Promise<number> {
    if (t.series_id) return t.series_id;
    const created = unwrap(await api.POST("/api/admin/library/titles", {
      body: {
        category_slug: category,
        listing: listingFor(idx, t),
        rights: {
          rights_holder: rights.rights_holder || null,
          contract_ref: rights.contract_ref || null,
          licence_start: rights.licence_start || null,
          territories: rights.territories?.length ? rights.territories : null,
          ai_generated: rights.ai_generated ?? null,
          has_real_person: rights.has_real_person ?? null,
        },
      },
    }));
    note(`${created.created ? "已创建" : "已关联"}剧集「${created.title}」`);
    return created.series_id;
  }

  async function sendCover(seriesId: number, t: TitlePlan, idx: FolderIndex) {
    const file = t.image_name ? idx.images.get(t.image_name) : undefined;
    if (!file || t.has_cover) return;
    if (file.size > MAX_COVER_BYTES) {
      note(`「${t.title}」的封面超过 5 MB，未上传`);
      return;
    }
    const res = await fetch(`/api/admin/series/${seriesId}/cover`, { method: "POST", body: file, headers: { [CSRF_HEADER]: "1", "Content-Type": file.type || "image/jpeg" } });
    if (!res.ok) note(`「${t.title}」的封面上传失败（HTTP ${res.status}）`);
  }

  async function sendEpisode(seriesId: number, storageFolder: string, t: TitlePlan, ep: EpisodePlan, file: File, signal: AbortSignal) {
    const key = liveKey(t.key, ep.number);
    for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
      if (signal.aborted) return;
      let episodeId: number | null = null;
      try {
        setOne(key, { state: "uploading", progress: 0 });
        const ticket = unwrap(await api.POST("/api/admin/library/episodes", {
          body: {
            series_id: seriesId, number: ep.number, storage_folder: storageFolder, title_folder: t.folder_name!,
            file_name: file.name, bytes: file.size, content_type: file.type || "video/mp4", replace: false,
          },
        }));
        episodeId = ticket.episode_id;
        if (!ticket.stored && ticket.upload) await uploadFile(ticket.upload, file, (p) => setOne(key, { state: "uploading", progress: p }), signal);
        unwrap(await api.POST("/api/admin/library/episodes/{id}/commit", { params: { path: { id: ticket.episode_id } }, body: { bytes: file.size } }));
        setOne(key, { state: "done", progress: 1 });
        return;
      } catch (e) {
        if (e instanceof ApiError && e.code === "episode_uploaded") {
          setOne(key, { state: "done", progress: 1 });
          return;
        }
        if (signal.aborted) {
          if (episodeId) await api.POST("/api/admin/library/episodes/{id}/abort", { params: { path: { id: episodeId } } }).catch(() => {});
          setOne(key, { state: "error", progress: 0, error: "已停止" });
          return;
        }
        if (attempt === ATTEMPTS) {
          if (episodeId) await api.POST("/api/admin/library/episodes/{id}/abort", { params: { path: { id: episodeId } } }).catch(() => {});
          setOne(key, { state: "error", progress: 0, error: errorText(e) });
          note(`「${t.title}」第 ${ep.number} 集失败：${errorText(e)}`);
          return;
        }
        await sleep(2000 * attempt, signal);
      }
    }
  }

  /**
   * Uploads what the plan says is missing: whole titles first, episodes in order, two files at a time. A rescan passes
   * its fresh results in, since state set a moment ago is not visible here until the next render.
   */
  async function run(only?: TitlePlan[], quiet = false, given?: Scanned) {
    const idx = given?.idx ?? index;
    const current = given?.plan ?? plan;
    if (!idx || !current || stopper.current) return;
    if (!category) {
      setError(new Error("请先选择频道"));
      return;
    }
    const titles = (only ?? current.titles)
      .filter((t) => t.status === "new" || t.status === "updates")
      .sort((a, b) => (a.downloading > 0 ? 1 : 0) - (b.downloading > 0 ? 1 : 0));
    if (titles.length === 0) {
      if (!quiet) note("没有需要上传的内容");
      return;
    }
    const ctrl = new AbortController();
    stopper.current = ctrl;
    setRunning(true);
    setError(null);
    let lock: { release: () => Promise<void> } | null = null;
    try {
      lock = await (navigator as unknown as { wakeLock?: { request: (t: "screen") => Promise<{ release: () => Promise<void> }> } }).wakeLock?.request("screen") ?? null;
    } catch {
      lock = null; // not offered or refused: the PC may sleep during a long upload
    }
    try {
      for (const t of titles) {
        if (ctrl.signal.aborted) break;
        let seriesId: number;
        try {
          seriesId = await ensureSeries(t, idx);
          await sendCover(seriesId, t, idx);
        } catch (e) {
          note(`「${t.title}」无法创建剧集：${errorText(e)}`);
          continue;
        }
        const queue = t.episodes.filter((e) => SENDABLE.has(e.state) && e.number > 0).sort((a, b) => a.number - b.number);
        const next = async () => {
          for (let ep = queue.shift(); ep; ep = queue.shift()) {
            while (pausedRef.current && !ctrl.signal.aborted) await sleep(500, ctrl.signal);
            if (ctrl.signal.aborted) return;
            const file = idx.videos.get(videoKey(t.folder_name ?? "", ep.file_name));
            if (!file) continue;
            await sendEpisode(seriesId, current.storage_folder, t, ep, file, ctrl.signal);
          }
        };
        await Promise.all(Array.from({ length: PARALLEL }, next));
      }
    } finally {
      await lock?.release().catch(() => {});
      setRunning(false);
      stopper.current = null;
      try {
        await askPlan(idx);
      } catch (e) {
        setError(e);
      }
    }
  }

  // Keep uploading: re-list the folder every few minutes and send what finished downloading since (needs a folder
  // handle, which Chrome and Edge give; a folder picked through the plain input is a one-off snapshot).
  const runRef = useRef(run);
  runRef.current = run;
  useEffect(() => {
    if (!keepUploading || !handle) return undefined;
    const timer = window.setInterval(() => {
      if (stopper.current || pausedRef.current) return;
      void (async () => {
        const scanned = await scanFolder(handle);
        if (scanned) await runRef.current(undefined, true, scanned);
      })();
    }, RESCAN_MS);
    return () => window.clearInterval(timer);
  }, [keepUploading, handle, scanFolder]);

  // ---------- view ----------

  const totals = useMemo(() => {
    const t = { titles: 0, send: 0, bytes: 0, stored: 0, downloading: 0, attention: 0, onSite: 0 };
    for (const title of plan?.titles ?? []) {
      t.titles++;
      t.downloading += title.downloading;
      if (title.status === "attention") t.attention++;
      for (const e of title.episodes) {
        if (e.state === "uploaded" || e.state === "processing") t.onSite++;
        if (!SENDABLE.has(e.state) || title.status === "attention") continue;
        t.send++;
        if (e.state === "stored") t.stored++;
        else t.bytes += e.bytes;
      }
    }
    return t;
  }, [plan]);

  const progress = useMemo(() => {
    const items = Object.values(live);
    return { done: items.filter((x) => x.state === "done").length, failed: items.filter((x) => x.state === "error").length, active: items.filter((x) => x.state === "uploading").length };
  }, [live]);

  const cfg = config.data;
  const where = cfg?.cos
    ? `videos/${(plan?.storage_folder ?? folder) || cfg.default_folder}/<剧名文件夹>/<文件名>`
    : "服务器存储（raw/uploads），上传后转码为 HLS";

  return (
    <>
      <PageHeader
        title="文件夹上传"
        sub={<>
          选择整个交付文件夹（包含 <code>VideoDetails/</code>、<code>Images/</code>、<code>Videos/剧名/剧名_集数.mp4</code>），
          系统会比对后台，列出每部剧缺少的内容；视频由浏览器直接上传到存储，不经过服务器。新剧以草稿创建，发布前请在内容管理中补全版权信息。
        </>}
      />

      <Card className="p-5 mb-4">
        <div className="grid grid-cols-[1fr_1fr_1fr] gap-4">
          <Field label="视频存储" hint={cfg?.cos ? `腾讯云 COS · ${cfg.bucket} · ${cfg.region}` : "未启用 COS（Video:Provider = Hls）"}>
            <div className="h-9 px-3 rounded-(--r-sm) bg-s2 text-[12px] text-mut flex items-center truncate" title={where}>{where}</div>
          </Field>
          <Field label="存储文件夹（videos/ 下）" hint="可选择存储桶中已有的文件夹，或输入新名称">
            <input list="rg-library-folders" value={folder} disabled={!cfg?.cos || running}
              onChange={(e) => { setFolder(e.target.value); writePref("rg.library.folder", e.target.value); }}
              className={inputClass} placeholder={cfg?.default_folder} />
            <datalist id="rg-library-folders">{(cfg?.folders ?? []).map((f) => <option key={f} value={f} />)}</datalist>
          </Field>
          <Field label="新剧放入频道">
            <select value={category} disabled={running} onChange={(e) => { setCategory(e.target.value); writePref("rg.library.category", e.target.value); }} className={inputClass}>
              {openCategories.map((c) => <option key={c.slug} value={c.slug}>{c.name}</option>)}
            </select>
          </Field>
        </div>
        {cfg?.warning && <p className="mt-3 text-[12px] text-danger">{cfg.warning}</p>}
        {plan?.warning && <p className="mt-3 text-[12px] text-star">{plan.warning}</p>}
        <details className="mt-3 text-[12px] text-mut">
          <summary className="cursor-pointer">新剧的版权信息（可选，只填入剧集中还空着的项）</summary>
          <div className="mt-3 grid grid-cols-3 gap-4">
            {([["rights_holder", "版权方"], ["contract_ref", "合同编号"]] as const).map(([k, label]) => (
              <Field key={k} label={label}>
                <input value={rights[k] ?? ""} className={inputClass}
                  onChange={(e) => { const r = { ...rights, [k]: e.target.value }; setRights(r); writePref("rg.library.rights", JSON.stringify(r)); }} />
              </Field>
            ))}
            <Field label="授权开始日期">
              <input type="date" value={rights.licence_start ?? ""} className={inputClass}
                onChange={(e) => { const r = { ...rights, licence_start: e.target.value || null }; setRights(r); writePref("rg.library.rights", JSON.stringify(r)); }} />
            </Field>
            <Field label="授权地区" hint="ISO 代码用逗号分隔，* 表示全球">
              <input value={(rights.territories ?? []).join(",")} className={inputClass}
                onChange={(e) => { const r = { ...rights, territories: e.target.value.split(/[,，|\s]+/).filter(Boolean) }; setRights(r); writePref("rg.library.rights", JSON.stringify(r)); }} />
            </Field>
            {([["ai_generated", "AI 生成内容"], ["has_real_person", "出现真实人物"]] as const).map(([k, label]) => (
              <Field key={k} label={label}>
                <select value={rights[k] == null ? "" : rights[k] ? "yes" : "no"} className={inputClass}
                  onChange={(e) => { const r = { ...rights, [k]: e.target.value === "" ? null : e.target.value === "yes" }; setRights(r); writePref("rg.library.rights", JSON.stringify(r)); }}>
                  <option value="">未填写</option><option value="yes">是</option><option value="no">否</option>
                </select>
              </Field>
            ))}
          </div>
        </details>
      </Card>

      <Card className="p-5 mb-4">
        <div onDragOver={(e) => e.preventDefault()} onDrop={(e) => void onDrop(e)}
          className="rounded-(--r-md) border border-dashed border-white/15 p-5 flex items-center gap-4">
          <FolderOpen size={28} className="text-mut shrink-0" />
          <div className="flex-1 min-w-0 text-[12.5px] text-mut">
            {index
              ? <>已读取 <b className="text-on">{handle?.name ?? "所选文件夹"}</b> 中的 {index.files} 个文件{handle ? "" : "（此浏览器不能记住文件夹，无法自动检查新文件）"}</>
              : "把交付文件夹拖到这里，或点击右侧按钮选择。Chrome / Edge 会记住文件夹，之后可自动上传新下载完的文件。"}
            {scan && <div className="mt-1 text-on">正在读取文件… {scan.done}/{scan.total || "?"}</div>}
          </div>
          {remembered && !handle && <Button disabled={running || !!scan} onClick={() => void reopen(remembered)}>打开上次的文件夹「{remembered.name}」</Button>}
          {index && <Button disabled={running || !!scan} onClick={() => void (handle ? scanFolder(handle) : choose())}><RefreshCw size={14} className="inline -mt-0.5 mr-1" />重新检查</Button>}
          <Button variant="primary" disabled={running || !!scan} onClick={() => void choose()}><FolderOpen size={14} className="inline -mt-0.5 mr-1" />选择文件夹</Button>
          <input ref={(el) => { fileInput.current = el; el?.setAttribute("webkitdirectory", ""); }} type="file" multiple className="hidden"
            onChange={(e) => { const files = Array.from(e.target.files ?? []); e.target.value = ""; if (files.length) { setHandle(null); void scanFolder(files); } }} />
        </div>
        <ErrorText error={error} />

        {plan && (
          <div className="mt-4 flex items-center gap-3 flex-wrap">
            <div className="flex-1 text-[13px]">
              <b>{totals.titles}</b> 部剧 · <b>{totals.send}</b> 集待上传{totals.send > 0 && <>（{totals.bytes > 0 ? formatBytes(totals.bytes) : "无需传输"}{totals.stored ? `，其中 ${totals.stored} 集已在 COS，直接关联` : ""}）</>}
              · {totals.onSite} 集已在网站 · {totals.downloading} 集下载中
              {totals.attention > 0 && <span className="text-danger"> · {totals.attention} 部需处理</span>}
              {(running || progress.done + progress.failed > 0) && (
                <span className="text-mut"> · 本次完成 {progress.done}{progress.failed ? `，失败 ${progress.failed}` : ""}{progress.active ? `，上传中 ${progress.active}` : ""}</span>
              )}
            </div>
            <label className="flex items-center gap-1.5 text-[12px] text-mut" title="每 3 分钟重新检查文件夹，自动上传新下载完的集（需 Chrome / Edge 记住的文件夹）">
              <input type="checkbox" checked={keepUploading && !!handle} disabled={!handle} onChange={(e) => setKeepUploading(e.target.checked)} />
              自动上传新文件
            </label>
            {running ? (
              <>
                <Button onClick={() => setPaused(!paused)}>{paused ? <><Play size={14} className="inline -mt-0.5 mr-1" />继续</> : <><Pause size={14} className="inline -mt-0.5 mr-1" />暂停</>}</Button>
                <Button variant="danger" onClick={() => { setPaused(false); stopper.current?.abort(); }}><Square size={13} className="inline -mt-0.5 mr-1" />停止</Button>
              </>
            ) : (
              <Button variant="primary" disabled={totals.send === 0 && !plan.titles.some((t) => t.status === "new" || t.status === "updates")} onClick={() => void run()}>
                <Upload size={14} className="inline -mt-0.5 mr-1" />全部上传
              </Button>
            )}
          </div>
        )}
      </Card>

      {plan && plan.titles.length > 0 && (
        <Card className="overflow-hidden">
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-5 py-2.5 font-medium">剧名</th>
                <th className="px-3 py-2.5 font-medium w-[90px]">状态</th>
                <th className="px-3 py-2.5 font-medium w-[150px]">分集</th>
                <th className="px-3 py-2.5 font-medium w-[70px]">封面</th>
                <th className="px-5 py-2.5 font-medium w-[170px] text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {plan.titles.map((t) => {
                const [label, style] = TITLE_STATUS[t.status] ?? [t.status, "bg-s4 text-mut"];
                const send = t.episodes.filter((e) => SENDABLE.has(e.state)).length;
                const onSite = t.episodes.filter((e) => e.state === "uploaded" || e.state === "processing").length;
                const expanded = open[t.key] ?? false;
                return (
                  <TitleRow key={t.key} t={t} label={label} style={style} send={send} onSite={onSite} expanded={expanded} live={live} running={running}
                    onToggle={() => setOpen((o) => ({ ...o, [t.key]: !expanded }))}
                    onUpload={() => void run([t])} onOpenSeries={onOpenSeries} />
                );
              })}
            </tbody>
          </table>
        </Card>
      )}

      {log.length > 0 && (
        <Card className="p-4 mt-4">
          <div className="text-[12px] text-dim mb-1">记录</div>
          <div className="max-h-[160px] overflow-auto text-[12px] text-mut space-y-0.5">{log.map((l, i) => <div key={i}>{l}</div>)}</div>
        </Card>
      )}
    </>
  );
}

function TitleRow({ t, label, style, send, onSite, expanded, live, running, onToggle, onUpload, onOpenSeries }: {
  t: TitlePlan; label: string; style: string; send: number; onSite: number; expanded: boolean; live: Live; running: boolean;
  onToggle: () => void; onUpload: () => void; onOpenSeries: (id: number) => void;
}) {
  return (
    <>
      <tr className="border-t border-white/5 align-top">
        <td className="px-5 py-3">
          <button onClick={onToggle} className="flex items-start gap-1.5 text-left hover:text-primary">
            {expanded ? <ChevronDown size={15} className="mt-0.5 shrink-0" /> : <ChevronRight size={15} className="mt-0.5 shrink-0" />}
            <span>
              <span className="font-semibold">{t.title}</span>
              {t.folder_name && t.folder_name !== t.title && <span className="block text-[11px] text-dim">{t.folder_name}</span>}
            </span>
          </button>
          {t.problems.map((p) => <div key={p} className="ml-5 mt-1 text-[11.5px] text-danger">{p}</div>)}
        </td>
        <td className="px-3 py-3"><span className={`inline-block px-2 py-0.5 rounded-md text-[11px] font-semibold whitespace-nowrap ${style}`}>{label}</span></td>
        <td className="px-3 py-3 text-[12px] text-mut tabular-nums">
          网站 {onSite}{t.total_episodes ? ` / ${t.total_episodes}` : ""} · 待传 {send}
          {t.downloading > 0 && <div className="text-dim">下载中 {t.downloading}</div>}
        </td>
        <td className="px-3 py-3 text-[12px] text-mut">{t.has_cover ? "已有" : t.image_name ? "待上传" : "—"}</td>
        <td className="px-5 py-3 text-right whitespace-nowrap">
          {t.series_id && <button onClick={() => onOpenSeries(t.series_id!)} className="text-[12px] text-primary hover:underline mr-3">打开剧集</button>}
          <Button disabled={running || !(t.status === "new" || t.status === "updates")} onClick={onUpload}>上传</Button>
        </td>
      </tr>
      {expanded && (
        <tr className="bg-s2/40">
          <td colSpan={5} className="px-5 py-3">
            {t.episodes.length === 0 && <div className="text-[12px] text-dim">文件夹中没有已下载完的视频</div>}
            <div className="flex flex-wrap gap-1.5">
              {t.episodes.map((e) => {
                const l = live[liveKey(t.key, e.number)];
                const state = l?.state ?? e.state;
                const [text, cls] = EPISODE_STATE[state] ?? [state, "bg-s4 text-mut"];
                const title = [e.file_name, formatBytes(e.bytes), text, l?.error ?? e.note].filter(Boolean).join(" · ");
                return (
                  <span key={`${e.number}-${e.file_name}`} title={title}
                    className={`relative overflow-hidden min-w-[42px] h-7 px-2 rounded-md text-[11.5px] font-semibold tabular-nums flex items-center justify-center ${cls}`}>
                    {l?.state === "uploading" && <span className="absolute left-0 bottom-0 h-[3px] bg-on-primary/70" style={{ width: `${Math.round(l.progress * 100)}%` }} />}
                    {e.number || "?"}
                  </span>
                );
              })}
            </div>
            <div className="mt-2 text-[11px] text-dim">把鼠标停在集数上可查看文件名和原因。颜色：灰 待上传 · 蓝 已在 COS / 处理中 · 绿 已上传 · 黄 文件已变化 · 红 需处理。</div>
          </td>
        </tr>
      )}
    </>
  );
}
