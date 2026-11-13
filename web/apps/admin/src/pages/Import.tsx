import { unwrap, type Schemas } from "@reelguo/api-client";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, Upload } from "lucide-react";
import { useState } from "react";
import { api } from "../session";
import { formatBytes, uploadFile } from "../upload";
import { Button, Card, ErrorText, PageHeader } from "../ui";

type Report = Schemas["ImportReport"];

const ACTIONS: Record<string, string> = { create: "新建", update: "更新", skip_exists: "已存在，跳过" };

const COLUMN_HELP: [string, string][] = [
  ["external_id", "内容源的剧集编号（必填，同一部剧的每一行相同）"],
  ["series_title", "剧名（必填）"],
  ["category", "频道代码：ai-duanju / ai-manju / ai-mogai（必填）"],
  ["episode", "集数，从 1 开始（必填）"],
  ["file", "incoming/ 中的文件名（必填）"],
  ["episode_title / description / tags / author", "可选；tags 用 | 分隔"],
  ["score / total_episodes / is_finished / is_original / is_free", "可选；布尔值填 是/否"],
  ["rights_holder / contract_ref / licence_start / licence_end", "版权信息；日期格式 yyyy-MM-dd"],
  ["territories", "授权地区，ISO 代码用 | 分隔，或 * 表示全球"],
  ["ai_generated / has_real_person / consent_ref", "合规声明；出现真实人物必须填写同意书编号"],
];

export function ImportPage({ onOpenSeries }: { onOpenSeries: (id: number) => void }) {
  const qc = useQueryClient();
  const incoming = useQuery({
    queryKey: ["admin", "incoming"],
    queryFn: async () => unwrap(await api.GET("/api/admin/import/incoming")),
  });

  const [uploads, setUploads] = useState<{ name: string; size: number; progress: number; error?: string }[]>([]);
  async function uploadAll(files: File[]) {
    setUploads(files.map((f) => ({ name: f.name, size: f.size, progress: 0 })));
    for (const [i, file] of files.entries()) {
      const update = (p: object) => setUploads((list) => list.map((x, j) => (j === i ? { ...x, ...p } : x)));
      try {
        const ticket = unwrap(await api.POST("/api/admin/import/incoming", { body: { file_name: file.name, content_type: file.type || null } }));
        await uploadFile(ticket, file, (progress) => update({ progress }));
      } catch (e) {
        update({ error: e instanceof Error ? e.message : "上传失败" });
      }
    }
    void qc.invalidateQueries({ queryKey: ["admin", "incoming"] });
  }

  const [csv, setCsv] = useState("");
  const [report, setReport] = useState<Report | null>(null);
  const run = useMutation({
    mutationFn: async (dryRun: boolean) => unwrap(await api.POST("/api/admin/import", { body: { csv, dry_run: dryRun } })),
    onSuccess: (r) => {
      setReport(r);
      if (!r.dry_run) void qc.invalidateQueries({ queryKey: ["admin", "series"] });
    },
  });

  async function downloadTemplate() {
    const text = await (await fetch("/api/admin/import/template")).text();
    const blob = new Blob(["﻿" + text], { type: "text/csv;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = "reelguo-import-template.csv";
    a.click();
    URL.revokeObjectURL(a.href);
  }

  return (
    <>
      <PageHeader
        title="批量导入"
        sub="第一步把视频文件放进 incoming/，第二步提交清单（CSV，每集一行）。可以重复导入：按 external_id 匹配剧集，已存在的分集会跳过。"
      />

      <Card className="p-5 mb-4">
        <div className="flex items-center gap-3 mb-3">
          <div className="flex-1 text-[14px] font-semibold">1. 上传视频到 incoming/</div>
          <label className="flex items-center gap-1.5 h-9 px-4 rounded-(--r-ctl) bg-s3 hover:bg-s4 text-[13px] cursor-pointer">
            <Upload size={15} /> 选择文件
            <input type="file" multiple accept="video/*,.mkv,.mov" className="hidden"
              onChange={(e) => { const f = Array.from(e.target.files ?? []); if (f.length) void uploadAll(f); e.target.value = ""; }} />
          </label>
        </div>
        <p className="text-[11.5px] text-dim mb-3">大批量文件也可以直接用对象存储工具（如 rclone）放入 raw 存储的 incoming/ 目录。</p>
        {uploads.map((u) => (
          <div key={u.name} className="text-[12px] mb-2">
            <div className="flex justify-between text-mut"><span>{u.name}</span><span>{formatBytes(u.size)} · {Math.round(u.progress * 100)}%</span></div>
            <div className="h-1.5 mt-1 rounded-full bg-s3 overflow-hidden"><div className="h-full bg-primary" style={{ width: `${u.progress * 100}%` }} /></div>
            {u.error && <p className="text-danger">{u.error}</p>}
          </div>
        ))}
        <div className="text-[12px] text-mut">
          incoming/ 中共有 {incoming.data?.length ?? 0} 个文件
          {incoming.data && incoming.data.length > 0 && (
            <div className="mt-1.5 max-h-[120px] overflow-auto font-mono text-[11.5px] text-dim">{incoming.data.map((f) => <div key={f.key}>{f.name}</div>)}</div>
          )}
        </div>
      </Card>

      <Card className="p-5">
        <div className="flex items-center gap-3 mb-3">
          <div className="flex-1 text-[14px] font-semibold">2. 提交清单</div>
          <Button onClick={() => void downloadTemplate()}><Download size={14} className="inline -mt-0.5 mr-1" />下载模板</Button>
          <label className="h-9 px-4 rounded-(--r-ctl) bg-s3 hover:bg-s4 text-[13px] cursor-pointer flex items-center">
            打开 CSV
            <input type="file" accept=".csv,text/csv" className="hidden"
              onChange={async (e) => { const f = e.target.files?.[0]; if (f) setCsv(await f.text()); e.target.value = ""; }} />
          </label>
        </div>
        <textarea value={csv} onChange={(e) => setCsv(e.target.value)} rows={8} spellCheck={false}
          placeholder="external_id,series_title,category,episode,file,..." className="w-full rounded-(--r-sm) bg-s3 p-3 font-mono text-[12px] outline-none focus:ring-2 ring-primary" />
        <details className="mt-2 text-[12px] text-mut">
          <summary className="cursor-pointer">列说明</summary>
          <table className="mt-2"><tbody>{COLUMN_HELP.map(([c, d]) => <tr key={c}><td className="pr-4 py-0.5 font-mono text-[11.5px] text-on align-top">{c}</td><td>{d}</td></tr>)}</tbody></table>
        </details>
        <div className="flex items-center gap-2 mt-3">
          <Button disabled={!csv.trim() || run.isPending} onClick={() => run.mutate(true)}>校验</Button>
          <Button variant="primary" disabled={!report?.ok || !report.dry_run || run.isPending} onClick={() => run.mutate(false)}>开始导入</Button>
          <span className="text-[11.5px] text-dim">先校验，通过后才能导入</span>
        </div>
        <ErrorText error={run.error} />

        {report && (
          <div className="mt-4 text-[12.5px]">
            {report.errors.length > 0 ? (
              <div className="text-danger space-y-0.5">{report.errors.map((e) => <div key={e}>{e}</div>)}</div>
            ) : (
              <div className="text-success">{report.dry_run ? "校验通过" : `导入完成，已排队处理 ${report.queued_jobs} 集`}</div>
            )}
            <div className="mt-3 grid grid-cols-2 gap-4">
              <div>
                <div className="text-dim mb-1">剧集</div>
                {report.series.map((s) => (
                  <div key={s.external_id} className="flex gap-2">
                    <span className="font-mono text-[11.5px]">{s.external_id}</span>
                    <span>{s.title}</span>
                    <span className="text-mut">{ACTIONS[s.action] ?? s.action}</span>
                    {s.series_id && <button onClick={() => onOpenSeries(s.series_id!)} className="text-primary hover:underline">打开</button>}
                  </div>
                ))}
              </div>
              <div>
                <div className="text-dim mb-1">分集</div>
                <div className="max-h-[200px] overflow-auto">
                  {report.episodes.map((e) => (
                    <div key={`${e.external_id}-${e.episode}`} className="flex gap-2"><span>第 {e.row} 行</span><span>{e.external_id} 第 {e.episode} 集</span><span className="text-mut">{ACTIONS[e.action] ?? e.action}</span></div>
                  ))}
                </div>
              </div>
            </div>
          </div>
        )}
      </Card>
    </>
  );
}
