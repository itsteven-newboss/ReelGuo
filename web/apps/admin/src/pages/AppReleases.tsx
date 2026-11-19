import { ApiError, unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Copy, FileKey2, Plus, Upload } from "lucide-react";
import { useRef, useState, type FormEvent } from "react";
import { api, CSRF_HEADER } from "../session";
import { Button, Card, ErrorText, Field, PageHeader, formatTime, inputClass } from "../ui";

type Release = Schemas["AdminAppReleaseDto"];
type UploadedFile = Schemas["AppFileDto"];

/** `files`: what the file picker offers (the server checks the type again). `link`: its fixed download link, /app/download/{link}. */
const PLATFORMS: { id: string; label: string; files?: string; link: string }[] = [
  { id: "android", label: "安卓 APK", files: ".apk", link: "android" },
  { id: "ios", label: "iPhone（外部安装链接）", link: "ios" },
  { id: "windows-x86_64", label: "Windows（x64）", files: ".exe,.msi", link: "windows" },
  { id: "darwin-aarch64", label: "macOS（Apple 芯片）", files: ".gz", link: "mac-arm" },
  { id: "darwin-x86_64", label: "macOS（Intel）", files: ".gz", link: "mac-intel" },
  { id: "linux-x86_64", label: "Linux（x64）", files: ".AppImage", link: "linux" },
];
const platformName = (p: string) => PLATFORMS.find((x) => x.id === p)?.label ?? p;
/** Desktop builds update themselves and need the updater signature. */
const isDesktop = (p: string) => p !== "android" && p !== "ios";
/** Installers uploaded here live under /media/p/app/ (or the media domain's p/app/). */
const uploadedName = (url: string) => (/\/p\/app\//.test(url) ? url.slice(url.lastIndexOf("/") + 1) : null);

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

const formatSize = (bytes: number) => (bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`);

/** Sends an installer to the server, reporting progress (0–1). Large files are why this is not fetch(). */
function uploadInstaller(platform: string, file: File, onProgress: (share: number) => void): Promise<UploadedFile> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `/api/admin/app-releases/files?platform=${encodeURIComponent(platform)}&name=${encodeURIComponent(file.name)}`);
    xhr.setRequestHeader(CSRF_HEADER, "1");
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(e.loaded / e.total); };
    xhr.onload = () => {
      let body: { url?: string; error?: { code?: string; message?: string } } | undefined;
      try { body = JSON.parse(xhr.responseText); } catch { body = undefined; }
      if (xhr.status >= 200 && xhr.status < 300 && body?.url) resolve(body as UploadedFile);
      else if (xhr.status === 413) reject(new ApiError(413, "too_large", "文件超过了上传大小限制。经 Cloudflare 打开后台时单个文件最大 100 MB：可在服务器上直接打开后台上传，或把文件放到其他下载站后填写地址"));
      else reject(new ApiError(xhr.status, body?.error?.code ?? "error", body?.error?.message ?? `上传失败（${xhr.status}）`));
    };
    xhr.onerror = () => reject(new ApiError(0, "network", "上传中断，请检查网络后重试"));
    xhr.send(file);
  });
}

/** App 版本: builds of the native apps. Publishing one offers it to the update checks, the download page and the download links. */
export function AppReleasesPage() {
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ["admin", "app-releases"], queryFn: async () => unwrap(await api.GET("/api/admin/app-releases")) });
  const [editing, setEditing] = useState<number | "new" | null>(null);
  const [form, setForm] = useState<FormState>(EMPTY);
  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => setForm((f) => ({ ...f, [k]: v }));
  const refresh = () => void qc.invalidateQueries({ queryKey: ["admin", "app-releases"] });

  const [progress, setProgress] = useState<number | null>(null);
  const [uploaded, setUploaded] = useState<UploadedFile | null>(null);
  const [uploadError, setUploadError] = useState<unknown>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const sigInput = useRef<HTMLInputElement>(null);

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
    setUploaded(null);
    setUploadError(null);
    setForm(r ? toForm(r) : EMPTY);
    setEditing(r ? r.id : "new");
  };
  const submit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };
  /** Uploads the picked installer and fills in its address, SHA-256 and (from the file name) the version. */
  const pickFile = async (file: File) => {
    setUploadError(null);
    setUploaded(null);
    setProgress(0);
    try {
      const r = await uploadInstaller(form.platform, file, setProgress);
      setUploaded(r);
      setForm((f) => ({ ...f, url: r.url, sha256: r.sha256, version: f.version.trim() || (file.name.match(/\d+\.\d+\.\d+/)?.[0] ?? "") }));
    } catch (e) {
      setUploadError(e);
    } finally {
      setProgress(null);
    }
  };
  const pickSignature = async (file: File) => set("signature", (await file.text()).trim());

  const platform = PLATFORMS.find((p) => p.id === form.platform);
  const desktop = isDesktop(form.platform);
  const iphone = form.platform === "ios";
  const uploading = progress !== null;

  return (
    <>
      <PageHeader title="App 版本"
        sub={<>原生 App 的安装包。可直接上传安装包，也可填写其他下载站的地址。发布后：桌面版会在下次启动时自动下载并安装（需填写更新签名）；安卓 App 会提示用户下载新版本；「下载 App」页面和下面的下载链接会给出每个平台的最新版本。标为「强制更新」的版本要求旧版本先更新才能继续使用。</>}
        actions={<Button variant="primary" onClick={() => open(null)}><span className="inline-flex items-center gap-1.5"><Plus size={15} />添加版本</span></Button>} />

      <DownloadLinks />

      {editing !== null && (
        <Card className="p-5 mb-4">
          <form onSubmit={submit} className="grid grid-cols-4 gap-4">
            <div className="col-span-4 text-[14px] font-semibold">{editing === "new" ? "添加版本" : `编辑版本 #${editing}`}</div>
            <Field label="平台">
              <select value={form.platform} onChange={(e) => { set("platform", e.target.value); setUploaded(null); setUploadError(null); }} className={inputClass} disabled={uploading}>
                {PLATFORMS.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
              </select>
            </Field>
            <Field label="版本号" hint="如 0.6.0，须与安装包内的版本一致（上传时从文件名读取）">
              <input required value={form.version} onChange={(e) => set("version", e.target.value)} placeholder="0.6.0" className={inputClass} />
            </Field>
            <Field label={iphone ? "安装链接" : desktop ? "安装包 / 更新包地址" : "APK 下载地址"} className="col-span-2"
              hint={iphone
                ? "签名分发平台提供的安装链接（https:// 或 itms-services://）。不添加 iPhone 版本时，苹果用户下载的是主屏幕描述文件，无需任何安装包"
                : "点「上传安装包」自动填写；也可填写其他下载站上的 https:// 完整地址"}>
              <input required value={form.url} onChange={(e) => { set("url", e.target.value); setUploaded(null); }} disabled={uploading}
                placeholder={iphone ? "itms-services://?action=download-manifest&url=https://…" : "上传安装包，或填写 https://…"} className={inputClass} />
            </Field>
            {platform?.files && (
              <div className="col-span-4 -mt-1 flex flex-wrap items-center gap-3 text-[12.5px]">
                <Button type="button" disabled={uploading} onClick={() => fileInput.current?.click()} className="inline-flex items-center gap-1.5">
                  <Upload size={14} />{uploading ? `上传中 ${Math.round((progress ?? 0) * 100)}%` : "上传安装包"}
                </Button>
                {desktop && (
                  <Button type="button" variant="ghost" disabled={uploading} onClick={() => sigInput.current?.click()} className="inline-flex items-center gap-1.5">
                    <FileKey2 size={14} />读取 .sig 签名文件
                  </Button>
                )}
                <span className="text-dim">
                  {uploading ? "请保持页面打开，上传完成前不要关闭"
                    : uploaded ? <span className="text-success">已上传 {uploaded.name}（{formatSize(uploaded.size)}），地址和 SHA-256 已填好</span>
                    : form.platform.startsWith("darwin") ? "macOS 请上传构建产物中的 .app.tar.gz（自动更新使用同一个文件）"
                    : `支持 ${platform.files.split(",").join(" / ")}，最大 500 MB`}
                </span>
                {uploading && (
                  <div className="basis-full h-1.5 rounded-full bg-s3 overflow-hidden">
                    <div className="h-full bg-primary transition-[width]" style={{ width: `${Math.round((progress ?? 0) * 100)}%` }} />
                  </div>
                )}
                <input ref={fileInput} type="file" accept={platform.files} hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) void pickFile(f); e.target.value = ""; }} />
                <input ref={sigInput} type="file" accept=".sig" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) void pickSignature(f); e.target.value = ""; }} />
                <div className="basis-full"><ErrorText error={uploadError} /></div>
              </div>
            )}
            {desktop && (
              <Field label="更新签名" hint="构建时生成的 .sig 文件内容（用发布私钥签名）；没有签名的版本不会被自动更新安装" className="col-span-4">
                <textarea rows={2} value={form.signature} onChange={(e) => set("signature", e.target.value)} className={`${inputClass} h-auto py-2 font-mono text-[11.5px]`} />
              </Field>
            )}
            {!iphone && (
              <Field label="SHA-256（可选）" hint="上传时自动计算；显示在下载页，方便用户核对" className="col-span-2">
                <input value={form.sha256} onChange={(e) => set("sha256", e.target.value)} className={`${inputClass} font-mono text-[11.5px]`} />
              </Field>
            )}
            <div className={`${iphone ? "col-span-4" : "col-span-2"} flex items-end gap-6 text-[13px] h-9 self-end`}>
              <label className="flex items-center gap-2"><input type="checkbox" checked={form.published} onChange={(e) => set("published", e.target.checked)} />发布</label>
              {!iphone && <label className="flex items-center gap-2"><input type="checkbox" checked={form.mandatory} onChange={(e) => set("mandatory", e.target.checked)} />强制更新</label>}
            </div>
            <Field label="更新说明" hint="展示给用户，最多 2000 字" className="col-span-4">
              <textarea rows={3} value={form.notes} onChange={(e) => set("notes", e.target.value)} className={`${inputClass} h-auto py-2`} />
            </Field>
            <div className="col-span-4 flex items-center gap-3">
              <Button type="submit" variant="primary" disabled={save.isPending || uploading}>{save.isPending ? "保存中…" : "保存"}</Button>
              <Button variant="ghost" onClick={() => setEditing(null)} disabled={uploading}>取消</Button>
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
              {list.data.map((r) => {
                const file = uploadedName(r.url);
                return (
                  <tr key={r.id} className="border-t border-white/5 align-top">
                    <td className="px-4 py-3 whitespace-nowrap">{platformName(r.platform)}</td>
                    <td className="px-4 py-3 tabular-nums">
                      {r.version}
                      {r.mandatory && <span className="ml-2 px-1.5 py-0.5 rounded bg-star/20 text-star text-[10.5px] font-semibold">强制</span>}
                    </td>
                    <td className="px-4 py-3">
                      <span className={`inline-block px-2 py-0.5 rounded-md text-[11px] font-semibold ${r.published ? "bg-success/20 text-success" : "bg-s4 text-mut"}`}>{r.published ? "已发布" : "未发布"}</span>
                      {isDesktop(r.platform) && !r.signature && <div className="text-[11px] text-danger mt-1">缺少更新签名</div>}
                    </td>
                    <td className="px-4 py-3 max-w-[340px]">
                      {file && <span className="mr-1.5 px-1.5 py-0.5 rounded bg-primary/15 text-primary text-[10.5px] font-semibold">已上传</span>}
                      <a href={r.url} target="_blank" rel="noreferrer" className="text-primary hover:underline break-all text-[12px]">{file ?? r.url}</a>
                    </td>
                    <td className="px-4 py-3 text-mut whitespace-nowrap">{formatTime(r.published_at)}<div className="text-[11px] text-dim">{r.updated_by}</div></td>
                    <td className="px-4 py-3 text-right whitespace-nowrap">
                      <button className="text-primary hover:underline mr-4" onClick={() => open(r)}>编辑</button>
                      <button className="text-danger hover:underline" disabled={remove.isPending}
                        onClick={() => window.confirm(`删除 ${platformName(r.platform)} ${r.version}？${file ? "上传的安装包会一并删除。" : ""}`) && remove.mutate(r.id)}>删除</button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </Card>
    </>
  );
}

/** The links to share: one that picks the visitor's device, and one fixed link per platform. */
function DownloadLinks() {
  const site = useQuery({ queryKey: ["site"], queryFn: async () => unwrap(await api.GET("/api/v1/site")), staleTime: 5 * 60_000 });
  const [copied, setCopied] = useState<string | null>(null);
  const base = (site.data?.mirrors[0] ?? "").replace(/\/+$/, "");
  const rows: [string, string][] = [
    ["/app/download", "自动识别设备 · 二维码、推广和分享都用这个"],
    ...PLATFORMS.map((p): [string, string] => [`/app/download/${p.link}`, p.id === "ios" ? "iPhone · 主屏幕描述文件（发布了安装链接时改用该链接）" : p.label]),
  ];
  const copy = (text: string) => void navigator.clipboard?.writeText(text).then(() => { setCopied(text); window.setTimeout(() => setCopied(null), 1500); });
  return (
    <Card className="p-5 mb-4">
      <h2 className="text-[15px] font-bold">下载链接</h2>
      <p className="mt-1 text-[12px] text-mut leading-relaxed">
        链接始终指向最新发布的版本，发新版后不用更换。「自动识别设备」：安卓手机直接下载 APK，Windows 电脑直接下载安装程序；苹果手机进入安装说明并下载主屏幕描述文件；
        Mac 选择芯片版本；在微信、QQ 里打开时，提示先用浏览器打开（这些应用内无法下载）。还没有发布的平台会进入下载页并提示「即将上线」。
      </p>
      <div className="mt-3 divide-y divide-white/5">
        {rows.map(([path, label]) => {
          const full = base + path;
          return (
            <div key={path} className="flex items-center gap-3 py-2 text-[12.5px]">
              <code className="font-mono text-[12px] text-on break-all min-w-0 flex-1">{full}</code>
              <span className="text-dim shrink-0 max-w-[45%] text-right">{label}</span>
              <button type="button" onClick={() => copy(full)} className="shrink-0 inline-flex items-center gap-1 text-primary hover:underline">
                <Copy size={13} />{copied === full ? "已复制" : "复制"}
              </button>
            </div>
          );
        })}
      </div>
      {!base && !site.isPending && <p className="mt-2 text-[11.5px] text-dim">没有配置网站地址（App__Mirrors__0），使用时请在前面加上网站域名。</p>}
    </Card>
  );
}
