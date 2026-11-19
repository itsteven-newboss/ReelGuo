import { ApiError, unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ImagePlus, RotateCcw } from "lucide-react";
import { useRef } from "react";
import { api, CSRF_HEADER } from "../session";
import { Button, Card, ErrorText, PageHeader, formatTime } from "../ui";

type Uploaded = Schemas["AdminLandingImageDto"];

/** One picture on the download page. `w × h` is the recommended upload size (twice the size it is shown at). */
type Slot = { id: string; label: string; w: number; h: number; fallback: string };
type Group = { title: string; note: string; slots: Slot[] };

const GROUPS: Group[] = [
  {
    title: "首页主视觉",
    note: "整张图就是下载页第一屏（和参考站一样，站名、标语等文字请直接做在图里）。电脑版：左下角叠加扫码卡片（约 x 75–363、y 400–550，按 1440×608 计），右下角叠加下载按钮（x 743 起、y 485 起），这两处请留空。手机版：底部约 64 px 会被「手机客户端下载」按钮盖住。",
    slots: [
      { id: "home_d", label: "电脑版", w: 2880, h: 1216, fallback: "黄月短剧品牌主视觉" },
      { id: "home_p", label: "手机版", w: 880, h: 1240, fallback: "黄月短剧品牌主视觉" },
    ],
  },
  {
    title: "剧场类型",
    note: "背景铺满整个区块；四张卡片图按频道顺序对应第 1–4 个频道（标题和简介仍取频道设置）。",
    slots: [
      { id: "genres_bg_d", label: "背景 · 电脑版", w: 2880, h: 1728, fallback: "金色光效" },
      { id: "genres_bg_p", label: "背景 · 手机版", w: 880, h: 1352, fallback: "金色光效" },
      { id: "genre_1", label: "卡片 1", w: 490, h: 472, fallback: "该频道最热剧集的封面" },
      { id: "genre_2", label: "卡片 2", w: 490, h: 472, fallback: "该频道最热剧集的封面" },
      { id: "genre_3", label: "卡片 3", w: 490, h: 472, fallback: "该频道最热剧集的封面" },
      { id: "genre_4", label: "卡片 4", w: 490, h: 472, fallback: "该频道最热剧集的封面" },
    ],
  },
  {
    title: "沉浸式体验",
    note: "文字在左侧，手机和四张斜放的海报在右侧的展台上。",
    slots: [
      { id: "experience_bg_d", label: "背景 · 电脑版", w: 2880, h: 1728, fallback: "金色光效" },
      { id: "experience_bg_p", label: "背景 · 手机版", w: 880, h: 1196, fallback: "金色光效" },
      { id: "experience_screen", label: "手机屏幕", w: 600, h: 1200, fallback: "推荐第 1 部剧的封面" },
      { id: "experience_poster_1", label: "海报 1（最左）", w: 300, h: 900, fallback: "推荐第 2 部剧的封面" },
      { id: "experience_poster_2", label: "海报 2", w: 300, h: 900, fallback: "推荐第 3 部剧的封面" },
      { id: "experience_poster_3", label: "海报 3", w: 300, h: 900, fallback: "推荐第 4 部剧的封面" },
      { id: "experience_poster_4", label: "海报 4（最右）", w: 300, h: 900, fallback: "推荐第 5 部剧的封面" },
    ],
  },
  {
    title: "心愿榜",
    note: "榜上的七张卡片来自推荐榜（剧集编辑页的「编辑推荐权重」），这里只设置背景。",
    slots: [
      { id: "wishlist_bg_d", label: "背景 · 电脑版", w: 2880, h: 1912, fallback: "金色光效" },
      { id: "wishlist_bg_p", label: "背景 · 手机版", w: 880, h: 806, fallback: "金色光效" },
    ],
  },
  {
    title: "创作者计划",
    note: "整张图作为背景，标题、介绍和「了解创作者计划」按钮叠加在左侧，画面主体请放在右侧。",
    slots: [
      { id: "creator_d", label: "电脑版", w: 2880, h: 1536, fallback: "一张剧集封面" },
      { id: "creator_p", label: "手机版", w: 880, h: 712, fallback: "一张剧集封面" },
    ],
  },
  {
    title: "关于我们",
    note: "横幅背景，Logo、站名和标语叠加在中间。",
    slots: [
      { id: "about_d", label: "电脑版", w: 2880, h: 694, fallback: "一排剧集封面" },
      { id: "about_p", label: "手机版", w: 880, h: 212, fallback: "一排剧集封面" },
    ],
  },
];

const MAX_BYTES = 10 * 1024 * 1024;

/** 下载页图片: pictures for the app download page (/app/home). Empty slots use series covers. */
export function LandingImagesPage() {
  const list = useQuery({ queryKey: ["admin", "landing-images"], queryFn: async () => unwrap(await api.GET("/api/admin/landing-images")) });
  const bySlot = new Map((list.data ?? []).map((i) => [i.slot, i]));
  return (
    <>
      <PageHeader title="下载页图片"
        sub="下载页（/app/home）各位置的图片。未上传的位置自动使用剧集封面；上传后立即生效，「恢复默认」改回剧集封面。电脑版和手机版只传一张也可以，另一端会自动裁切使用。支持 JPEG、PNG、WebP，最大 10 MB。" />
      {list.isPending ? <div className="flex justify-center py-16"><Spinner /></div> : list.isError ? <ErrorText error={list.error} /> : (
        <div className="space-y-5">
          {GROUPS.map((g) => (
            <Card key={g.title} className="p-5">
              <h2 className="text-[15px] font-bold">{g.title}</h2>
              <p className="mt-1 text-[12px] text-mut leading-relaxed">{g.note}</p>
              <div className="mt-4 grid gap-4" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(250px, 1fr))" }}>
                {g.slots.map((s) => <SlotCard key={s.id} slot={s} current={bySlot.get(s.id)} />)}
              </div>
            </Card>
          ))}
        </div>
      )}
    </>
  );
}

function SlotCard({ slot, current }: { slot: Slot; current?: Uploaded }) {
  const qc = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const refresh = () => void qc.invalidateQueries({ queryKey: ["admin", "landing-images"] });
  const upload = useMutation({
    mutationFn: async (file: File) => {
      if (file.size > MAX_BYTES) throw new ApiError(400, "image_too_large", "图片不能超过 10 MB");
      const res = await fetch(`/api/admin/landing-images/${slot.id}`, { method: "POST", body: file, headers: { [CSRF_HEADER]: "1", "Content-Type": file.type } });
      const body = await res.json().catch(() => undefined);
      if (!res.ok) throw new ApiError(res.status, body?.error?.code ?? "error", body?.error?.message ?? "图片上传失败");
    },
    onSuccess: refresh,
  });
  const reset = useMutation({
    mutationFn: async () => {
      const r = await api.DELETE("/api/admin/landing-images/{slot}", { params: { path: { slot: slot.id } } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: refresh,
  });
  const busy = upload.isPending || reset.isPending;
  // Previews keep the slot's shape, at most 150 px tall.
  const ratio = slot.w / slot.h;
  return (
    <div className="rounded-(--r-md) bg-s2 p-3 flex flex-col">
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-[13px] font-semibold">{slot.label}</span>
        <span className="text-[11px] text-dim">建议 {slot.w}×{slot.h}</span>
      </div>
      <div className="mt-2 rounded-(--r-sm) overflow-hidden bg-s3 mx-auto w-full flex items-center justify-center" style={{ aspectRatio: String(ratio), maxHeight: 150, maxWidth: 150 * ratio }}>
        {current
          ? <img src={current.url} alt={slot.label} className="w-full h-full object-cover" />
          : <span className="text-[11.5px] text-dim text-center px-3 leading-relaxed">默认：{slot.fallback}</span>}
      </div>
      <p className="mt-2 text-[11px] text-dim min-h-[16px]">{current ? `${formatTime(current.updated_at)} · ${current.updated_by.replace(/^admin:/, "")}` : "未上传"}</p>
      <div className="mt-2 flex gap-2">
        <Button className="flex-1 inline-flex items-center justify-center gap-1.5" disabled={busy} onClick={() => input.current?.click()}>
          <ImagePlus size={14} /> {upload.isPending ? "上传中…" : current ? "更换" : "上传图片"}
        </Button>
        {current && (
          <Button variant="ghost" className="inline-flex items-center gap-1.5 px-3" disabled={busy} onClick={() => { if (window.confirm(`把「${slot.label}」恢复为默认图片？`)) reset.mutate(); }}>
            <RotateCcw size={14} /> 恢复默认
          </Button>
        )}
      </div>
      <input ref={input} type="file" accept="image/jpeg,image/png,image/webp" hidden
        onChange={(e) => { const f = e.target.files?.[0]; if (f) upload.mutate(f); e.target.value = ""; }} />
      <ErrorText error={upload.error ?? reset.error} />
    </div>
  );
}
