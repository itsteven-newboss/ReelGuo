import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useState, type FormEvent } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, Field, PageHeader, formatTime, inputClass } from "../ui";

type Release = Schemas["AdminAppReleaseDto"];

const PLATFORMS: [string, string][] = [
  ["android", "安卓 APK"],
  ["windows-x86_64", "Windows（x64）"],
  ["darwin-aarch64", "macOS（Apple 芯片）"],
  ["darwin-x86_64", "macOS（Intel）"],
  ["linux-x86_64", "Linux（x64）"],
];
const platformName = (p: string) => PLATFORMS.find(([k]) => k === p)?.[1] ?? p;

type FormState = { platform: string; version: string; url: string; signature: string; sha256: string; notes: string; mandatory: boolean; published: boolean };
const EMPTY: FormState = { platform: "android", version: "", url: "", signature: "", sha256: "", notes: "", mandatory: false, published: false };

const toForm = (r: Release): FormState => ({
  platform: r.platform, version: r.version, url: r.url, signature: r.signature ?? "", sha256: r.sha256 ?? "", notes: r.notes,
  mandatory: r.mandatory, published: r.published,
});
const toInput = (f: FormState) => ({
  platform: f.platform, version: f.version.trim(), url: f.url.trim(), signature: f.signature.trim() || null, sha256: f.sha256.trim() || null,
  notes: f.notes, mandatory: f.mandatory, published: f.published,
});

/** App 版本: builds of the native apps. Publishing one offers it to the update checks and the download page. */
export function AppReleasesPage() {
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ["admin", "app-releases"], queryFn: async () => unwrap(await api.GET("/api/admin/app-releases")) });
  const [editing, setEditing] = useState<number | "new" | null>(null);
  const [form, setForm] = useState<FormState>(EMPTY);
  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => setForm((f) => ({ ...f, [k]: v }));
  const refresh = () => void qc.invalidateQueries({ queryKey: ["admin", "app-releases"] });

  const save = useMutation({
    mutationFn: async () => editing === "new"
      ? unwrap(await api.POST("/api/admin/app-releases", { body: toInput(form) }))
      : unwrap(await api.PUT("/api/admin/app-releases/{id}", { params: { path: { id: editing as number } }, body: toInput(form) })),
    onSuccess: () => { setEditing(null); refresh(); },
  });
  const remove = useMutation({
    mutationFn: async (id: number) => {
      const r = await api.DELETE("/api/admin/app-releases/{id}", { params: { path: { id } } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: refresh,
  });

  const open = (r: Release | null) => {
    save.reset();
    setForm(r ? toForm(r) : EMPTY);
    setEditing(r ? r.id : "new");
  };
  const submit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };
  const desktop = form.platform !== "android";

  return (
    <>
      <PageHeader title="App 版本"
        sub={<>原生 App 的安装包。发布后：桌面版会在下次启动时自动下载并安装（需填写更新签名）；安卓 App 会提示用户下载新版本 APK；「下载 App」页面显示每个平台的最新版本。标为「强制更新」的版本要求旧版本先更新才能继续使用。</>}
        actions={<Button variant="primary" onClick={() => open(null)}><span className="inline-flex items-center gap-1.5"><Plus size={15} />添加版本</span></Button>} />

      {editing !== null && (
        <Card className="p-5 mb-4">
          <form onSubmit={submit} className="grid grid-cols-4 gap-4">
            <div className="col-span-4 text-[14px] font-semibold">{editing === "new" ? "添加版本" : `编辑版本 #${editing}`}</div>
            <Field label="平台">
              <select value={form.platform} onChange={(e) => set("platform", e.target.value)} className={inputClass}>
                {PLATFORMS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
              </select>
            </Field>
            <Field label="版本号" hint="如 0.6.0，须与安装包内的版本一致">
              <input required value={form.version} onChange={(e) => set("version", e.target.value)} placeholder="0.6.0" className={inputClass} />
            </Field>
            <Field label={desktop ? "安装包 / 更新包地址" : "APK 下载地址"} hint="https:// 开头的完整地址" className="col-span-2">
              <input required value={form.url} onChange={(e) => set("url", e.target.value)} placeholder="https://downloads.example.com/ReelGuo_0.6.0_x64-setup.exe" className={inputClass} />
            </Field>
            {desktop && (
              <Field label="更新签名" hint="构建时生成的 .sig 文件内容（用发布私钥签名）；没有签名的版本不会被自动更新安装" className="col-span-4">
                <textarea rows={2} value={form.signature} onChange={(e) => set("signature", e.target.value)} className={`${inputClass} h-auto py-2 font-mono text-[11.5px]`} />
              </Field>
            )}
            <Field label="SHA-256（可选）" hint="显示在下载页，方便用户核对" className="col-span-2">
              <input value={form.sha256} onChange={(e) => set("sha256", e.target.value)} className={`${inputClass} font-mono text-[11.5px]`} />
            </Field>
            <div className="col-span-2 flex items-end gap-6 text-[13px] h-9 self-end">
              <label className="flex items-center gap-2"><input type="checkbox" checked={form.published} onChange={(e) => set("published", e.target.checked)} />发布</label>
              <label className="flex items-center gap-2"><input type="checkbox" checked={form.mandatory} onChange={(e) => set("mandatory", e.target.checked)} />强制更新</label>
            </div>
            <Field label="更新说明" hint="展示给用户，最多 2000 字" className="col-span-4">
              <textarea rows={3} value={form.notes} onChange={(e) => set("notes", e.target.value)} className={`${inputClass} h-auto py-2`} />
            </Field>
            <div className="col-span-4 flex items-center gap-3">
              <Button type="submit" variant="primary" disabled={save.isPending}>{save.isPending ? "保存中…" : "保存"}</Button>
              <Button variant="ghost" onClick={() => setEditing(null)}>取消</Button>
              <ErrorText error={save.error} />
            </div>
          </form>
        </Card>
      )}

      <div className="mb-3"><ErrorText error={remove.error} /></div>
      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : list.isError ? <div className="p-5"><ErrorText error={list.error} /></div>
          : list.data.length === 0 ? <div className="py-12 text-center text-[13px] text-dim">还没有发布过 App 版本</div> : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">平台</th>
                <th className="px-4 py-2.5 font-medium">版本</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">下载地址</th>
                <th className="px-4 py-2.5 font-medium">发布时间</th>
                <th className="px-4 py-2.5 font-medium text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((r) => (
                <tr key={r.id} className="border-t border-white/5 align-top">
                  <td className="px-4 py-3 whitespace-nowrap">{platformName(r.platform)}</td>
                  <td className="px-4 py-3 tabular-nums">
                    {r.version}
                    {r.mandatory && <span className="ml-2 px-1.5 py-0.5 rounded bg-star/20 text-star text-[10.5px] font-semibold">强制</span>}
                  </td>
                  <td className="px-4 py-3">
                    <span className={`inline-block px-2 py-0.5 rounded-md text-[11px] font-semibold ${r.published ? "bg-success/20 text-success" : "bg-s4 text-mut"}`}>{r.published ? "已发布" : "未发布"}</span>
                    {r.platform !== "android" && !r.signature && <div className="text-[11px] text-danger mt-1">缺少更新签名</div>}
                  </td>
                  <td className="px-4 py-3 max-w-[340px]"><a href={r.url} target="_blank" rel="noreferrer" className="text-primary hover:underline break-all text-[12px]">{r.url}</a></td>
                  <td className="px-4 py-3 text-mut whitespace-nowrap">{formatTime(r.published_at)}<div className="text-[11px] text-dim">{r.updated_by}</div></td>
                  <td className="px-4 py-3 text-right whitespace-nowrap">
                    <button className="text-primary hover:underline mr-4" onClick={() => open(r)}>编辑</button>
                    <button className="text-danger hover:underline" disabled={remove.isPending}
                      onClick={() => window.confirm(`删除 ${platformName(r.platform)} ${r.version}？`) && remove.mutate(r.id)}>删除</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}
