import { CheckCircle2, FolderInput, FolderOutput, Square, Upload, Wand2, X } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Card, ErrorText } from "../ui";
import { formatBytes } from "../upload";
import {
  checkFolders, convertEpisode, copyExtras, existingSize, optimizerSupport, outputPath, planOptimize, writeFileAt,
  type OptimizeJob, type OptimizePlan,
} from "./optimize";

type JobState = { state: "working" | "done" | "exists" | "failed"; progress: number; outBytes?: number; speed?: number; error?: string };
const jobKey = (j: Pick<OptimizeJob, "title" | "number">) => `${j.title}\n${j.number}`;

type WakeLock = { release: () => Promise<void> };

async function pickDirectory(mode: "read" | "readwrite", id: string): Promise<FileSystemDirectoryHandle | null> {
  try {
    return await (window as unknown as { showDirectoryPicker: (o: object) => Promise<FileSystemDirectoryHandle> }).showDirectoryPicker({ mode, id });
  } catch (e) {
    if (e instanceof DOMException && e.name === "AbortError") return null;
    throw e;
  }
}

/**
 * 优化视频: turns a delivery folder into one the folder upload takes as it is (see optimize.ts), on this PC, in the
 * browser. Pick the delivery, pick an empty output folder, start; finished episodes are skipped when started again.
 */
export function OptimizePanel({ onUseFolder, onClose }: { onUseFolder: (dir: FileSystemDirectoryHandle) => void; onClose: () => void }) {
  const [support, setSupport] = useState<string | null | undefined>(undefined);
  const [source, setSource] = useState<FileSystemDirectoryHandle | null>(null);
  const [target, setTarget] = useState<FileSystemDirectoryHandle | null>(null);
  const [plan, setPlan] = useState<OptimizePlan | null>(null);
  const [scan, setScan] = useState<{ done: number; total: number } | null>(null);
  const [states, setStates] = useState<Record<string, JobState>>({});
  const [running, setRunning] = useState(false);
  const [finished, setFinished] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const stopper = useRef<AbortController | null>(null);

  useEffect(() => { void optimizerSupport().then(setSupport); }, []);

  // Closing the tab mid-run would lose the episode being converted.
  useEffect(() => {
    if (!running) return undefined;
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [running]);

  const set = (key: string, value: JobState) => setStates((s) => ({ ...s, [key]: value }));

  async function chooseSource() {
    setError(null);
    try {
      const dir = await pickDirectory("read", "rg-optimize-source");
      if (!dir) return;
      if (target) {
        const problem = await checkFolders(dir, target);
        if (problem) { setError(new Error(problem)); return; }
      }
      setSource(dir);
      setPlan(null);
      setStates({});
      setFinished(false);
      setScan({ done: 0, total: 0 });
      setPlan(await planOptimize(dir, (done, total) => setScan({ done, total })));
    } catch (e) {
      setError(e);
    } finally {
      setScan(null);
    }
  }

  async function chooseTarget() {
    setError(null);
    try {
      const dir = await pickDirectory("readwrite", "rg-optimize-target");
      if (!dir) return;
      if (source) {
        const problem = await checkFolders(source, dir);
        if (problem) { setError(new Error(problem)); return; }
      }
      setTarget(dir);
      setFinished(false);
    } catch (e) {
      setError(e);
    }
  }

  async function start() {
    if (!plan || !target) return;
    const stop = new AbortController();
    stopper.current = stop;
    setRunning(true);
    setFinished(false);
    setError(null);
    let lock: WakeLock | null = null;
    try {
      lock = (await (navigator as unknown as { wakeLock?: { request: (t: "screen") => Promise<WakeLock> } }).wakeLock?.request("screen").catch(() => null)) ?? null;
      await copyExtras(plan, target);
      for (const job of plan.jobs) {
        if (stop.signal.aborted) break;
        const key = jobKey(job);
        const path = outputPath(job);
        const have = await existingSize(target, path);
        if (have) { set(key, { state: "exists", progress: 1, outBytes: have }); continue; }
        set(key, { state: "working", progress: 0 });
        try {
          if (job.action === "copy") {
            await writeFileAt(target, path, job.file);
            set(key, { state: "done", progress: 1, outBytes: job.bytes });
          } else {
            const started = performance.now();
            const mp4 = await convertEpisode(job.file, (progress, seconds) => {
              const elapsed = (performance.now() - started) / 1000;
              set(key, { state: "working", progress, speed: elapsed > 1 ? seconds / elapsed : undefined });
            }, stop.signal);
            await writeFileAt(target, path, mp4);
            set(key, { state: "done", progress: 1, outBytes: mp4.byteLength });
          }
        } catch (e) {
          if (stop.signal.aborted) { setStates((s) => { const next = { ...s }; delete next[key]; return next; }); break; }
          set(key, { state: "failed", progress: 0, error: e instanceof Error ? e.message : "失败" });
        }
      }
      if (!stop.signal.aborted) setFinished(true);
    } catch (e) {
      setError(e);
    } finally {
      await lock?.release().catch(() => undefined);
      setRunning(false);
    }
  }

  const totals = useMemo(() => {
    const jobs = plan?.jobs ?? [];
    const convert = jobs.filter((j) => j.action === "convert");
    let done = 0;
    let failed = 0;
    let inBytes = 0;
    let outBytes = 0;
    for (const j of jobs) {
      const s = states[jobKey(j)];
      if (s?.state === "done" || s?.state === "exists") { done++; inBytes += j.bytes; outBytes += s.outBytes ?? 0; }
      if (s?.state === "failed") failed++;
    }
    return { jobs: jobs.length, convert: convert.length, copy: jobs.length - convert.length, bytes: jobs.reduce((n, j) => n + j.bytes, 0), done, failed, inBytes, outBytes };
  }, [plan, states]);

  const titles = useMemo(() => {
    const map = new Map<string, OptimizeJob[]>();
    for (const j of plan?.jobs ?? []) map.set(j.title, [...(map.get(j.title) ?? []), j]);
    return [...map];
  }, [plan]);

  return (
    <Card className="p-5 mb-4">
      <div className="flex items-start gap-3">
        <Wand2 size={20} className="text-primary mt-0.5 shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="text-[14px] font-semibold">优化视频</div>
          <p className="text-[12px] text-mut mt-1 leading-relaxed">
            把交付文件夹转换成适合上传和网络播放的格式：H.264 + AAC、索引在文件开头、约 1.5–2 Mbit/s（1080×1920，6 分钟一集约 80 MB）。
            已符合要求的集直接复制。输出整理为 Videos/剧名/剧名_集数.mp4、Images、VideoDetails，可直接用于下方的文件夹上传。
            在本机浏览器中完成，需要电脑版 Edge 或 Chrome，处理期间请保持此页面打开。
          </p>
        </div>
        <button onClick={onClose} disabled={running} aria-label="收起" className="w-8 h-8 rounded-full flex items-center justify-center text-mut hover:bg-s3 disabled:opacity-40"><X size={16} /></button>
      </div>

      {support === undefined && <p className="mt-3 text-[12.5px] text-mut">正在检查此浏览器能否编码视频…</p>}
      {support && <p className="mt-3 text-[12.5px] text-danger">{support}</p>}

      {support === null && (
        <>
          <div className="mt-4 grid grid-cols-2 gap-3">
            <div className="rounded-(--r-md) bg-s2 p-3">
              <div className="text-[12px] text-mut">1. 原始文件夹（交付的视频）</div>
              <div className="mt-1 text-[13px] truncate">{source ? <b>{source.name}</b> : "未选择"}</div>
              {scan && <div className="mt-1 text-[12px]">正在读取… {scan.done}/{scan.total || "?"}</div>}
              <Button className="mt-2" disabled={running || !!scan} onClick={() => void chooseSource()}><FolderInput size={14} className="inline -mt-0.5 mr-1" />选择原始文件夹</Button>
            </div>
            <div className="rounded-(--r-md) bg-s2 p-3">
              <div className="text-[12px] text-mut">2. 输出文件夹（建议新建一个空文件夹）</div>
              <div className="mt-1 text-[13px] truncate">{target ? <b>{target.name}</b> : "未选择"}</div>
              <Button className="mt-2" disabled={running} onClick={() => void chooseTarget()}><FolderOutput size={14} className="inline -mt-0.5 mr-1" />选择输出文件夹</Button>
            </div>
          </div>

          {plan && (
            <div className="mt-4 flex items-center gap-3 flex-wrap">
              <div className="flex-1 text-[13px]">
                <b>{plan.titles}</b> 部剧 · <b>{totals.jobs}</b> 集：{totals.convert} 集需优化，{totals.copy} 集直接复制 · 原始 {formatBytes(totals.bytes)}
                {plan.skipped.length > 0 && <span className="text-star"> · {plan.skipped.length} 个文件暂不处理</span>}
                {(running || totals.done + totals.failed > 0) && (
                  <span className="text-mut"> · 已完成 {totals.done}/{totals.jobs}{totals.failed ? `，失败 ${totals.failed}` : ""}
                    {totals.done > 0 && totals.outBytes > 0 && `（${formatBytes(totals.inBytes)} → ${formatBytes(totals.outBytes)}）`}</span>
                )}
              </div>
              {running ? (
                <Button variant="danger" onClick={() => stopper.current?.abort()}><Square size={13} className="inline -mt-0.5 mr-1" />停止</Button>
              ) : (
                <Button variant="primary" disabled={!target || totals.jobs === 0} onClick={() => void start()}>
                  <Wand2 size={14} className="inline -mt-0.5 mr-1" />{totals.done + totals.failed > 0 ? "继续优化" : "开始优化"}
                </Button>
              )}
              {finished && target && (
                <Button variant="primary" onClick={() => onUseFolder(target)}><Upload size={14} className="inline -mt-0.5 mr-1" />上传优化后的文件夹</Button>
              )}
            </div>
          )}
          <ErrorText error={error} />
          {finished && (
            <p className="mt-2 text-[12.5px] text-success flex items-center gap-1.5">
              <CheckCircle2 size={14} />优化完成{totals.failed ? `，${totals.failed} 集失败（可再点「继续优化」重试）` : ""}。点击「上传优化后的文件夹」即可识别所有剧集并上传。
            </p>
          )}

          {plan && titles.length > 0 && (
            <div className="mt-4 max-h-[420px] overflow-y-auto rounded-(--r-md) border border-white/[.06]">
              <table className="w-full text-[12.5px]">
                <thead className="sticky top-0 bg-s2 text-mut text-left">
                  <tr><th className="px-3 py-2 font-normal">剧名</th><th className="px-3 py-2 font-normal w-14">集</th><th className="px-3 py-2 font-normal">处理</th><th className="px-3 py-2 font-normal w-[260px]">状态</th></tr>
                </thead>
                <tbody>
                  {titles.flatMap(([title, jobs]) => jobs.map((j, i) => {
                    const s = states[jobKey(j)];
                    return (
                      <tr key={jobKey(j)} className="border-t border-white/[.04]">
                        <td className="px-3 py-1.5">{i === 0 ? <b>{title}</b> : ""}</td>
                        <td className="px-3 py-1.5">{j.number}</td>
                        <td className="px-3 py-1.5 text-mut" title={j.name}>{j.action === "convert" ? `优化：${j.reason}` : j.reason} · {formatBytes(j.bytes)}</td>
                        <td className="px-3 py-1.5"><JobStatus s={s} /></td>
                      </tr>
                    );
                  }))}
                </tbody>
              </table>
            </div>
          )}
          {plan && plan.skipped.length > 0 && (
            <details className="mt-3 text-[12px] text-mut">
              <summary className="cursor-pointer">暂不处理的文件（{plan.skipped.length}）</summary>
              <ul className="mt-2 space-y-0.5">{plan.skipped.map((s) => <li key={`${s.title}/${s.name}`}>{s.title} / {s.name}：{s.reason}</li>)}</ul>
            </details>
          )}
        </>
      )}
    </Card>
  );
}

function JobStatus({ s }: { s: JobState | undefined }) {
  if (!s) return <span className="text-dim">等待</span>;
  if (s.state === "working") {
    return (
      <span className="flex items-center gap-2">
        <span className="flex-1 h-1.5 rounded-full bg-s4 overflow-hidden"><span className="block h-full bg-primary" style={{ width: `${Math.round(s.progress * 100)}%` }} /></span>
        <span className="w-[92px] text-right">{Math.round(s.progress * 100)}%{s.speed ? ` · ${s.speed.toFixed(1)} 倍速` : ""}</span>
      </span>
    );
  }
  if (s.state === "failed") return <span className="text-danger" title={s.error}>失败：{s.error}</span>;
  return <span className="text-success">{s.state === "exists" ? "已存在，跳过" : "完成"}{s.outBytes ? ` · ${formatBytes(s.outBytes)}` : ""}</span>;
}
