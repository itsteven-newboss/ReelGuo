import { encode } from "uqr";
import { t } from "../i18n";

/** What the 「我解锁了结局」 share card shows. */
export type EndingShare = {
  series: string;
  cover?: string | null;
  ending: string;
  number: number;
  total: number;
  /** The series page, also behind the QR code. */
  url: string;
  brand: string;
};

const W = 1080;
const H = 1440;
const FONT = '"PingFang SC","HarmonyOS Sans SC","Microsoft YaHei","Noto Sans SC",sans-serif';

/** Loads an image for drawing; null when it can't be loaded (or isn't allowed to be drawn, without CORS). */
function loadImage(src: string): Promise<HTMLImageElement | null> {
  return new Promise((resolve) => {
    const img = new Image();
    img.crossOrigin = "anonymous";
    img.onload = () => resolve(img);
    img.onerror = () => resolve(null);
    img.src = src;
  });
}

function fit(ctx: CanvasRenderingContext2D, text: string, max: number): string {
  if (ctx.measureText(text).width <= max) return text;
  let t = text;
  while (t.length > 1 && ctx.measureText(`${t}…`).width > max) t = t.slice(0, -1);
  return `${t}…`;
}

function cover(ctx: CanvasRenderingContext2D, img: HTMLImageElement, x: number, y: number, w: number, h: number) {
  const scale = Math.max(w / img.width, h / img.height);
  const sw = w / scale;
  const sh = h / scale;
  ctx.drawImage(img, (img.width - sw) / 2, (img.height - sh) / 2, sw, sh, x, y, w, h);
}

/** Draws the card: the cover, 「我解锁了结局」, the ending's name, the series and a QR code to it. */
export async function endingCard(s: EndingShare): Promise<Blob> {
  const canvas = document.createElement("canvas");
  canvas.width = W;
  canvas.height = H;
  const ctx = canvas.getContext("2d")!;
  const img = s.cover ? await loadImage(s.cover) : null;

  ctx.fillStyle = "#0d0c0b";
  ctx.fillRect(0, 0, W, H);
  if (img) {
    ctx.save();
    ctx.filter = "blur(36px) brightness(.45)";
    cover(ctx, img, -60, -60, W + 120, H + 120);
    ctx.restore();
  }
  const shade = ctx.createLinearGradient(0, 0, 0, H);
  shade.addColorStop(0, "rgba(13,12,11,.35)");
  shade.addColorStop(0.55, "rgba(13,12,11,.75)");
  shade.addColorStop(1, "rgba(13,12,11,.96)");
  ctx.fillStyle = shade;
  ctx.fillRect(0, 0, W, H);

  ctx.textAlign = "center";
  ctx.fillStyle = "#ffd21c";
  ctx.font = `600 34px ${FONT}`;
  ctx.fillText(t("share.header", { brand: s.brand }), W / 2, 104);

  // The cover, 3:4, rounded.
  const cw = 460;
  const ch = 614;
  const cx = (W - cw) / 2;
  const cy = 150;
  ctx.save();
  ctx.beginPath();
  ctx.roundRect(cx, cy, cw, ch, 28);
  ctx.clip();
  if (img) cover(ctx, img, cx, cy, cw, ch);
  else {
    const g = ctx.createLinearGradient(cx, cy, cx + cw, cy + ch);
    g.addColorStop(0, "#3a3220");
    g.addColorStop(1, "#151412");
    ctx.fillStyle = g;
    ctx.fillRect(cx, cy, cw, ch);
  }
  ctx.restore();

  ctx.fillStyle = "rgba(255,255,255,.88)";
  ctx.font = `500 46px ${FONT}`;
  ctx.fillText(t("share.unlocked"), W / 2, 870);
  ctx.fillStyle = "#ffd21c";
  ctx.font = `700 104px ${FONT}`;
  ctx.fillText(fit(ctx, `「${s.ending}」`, W - 120), W / 2, 1000);
  ctx.fillStyle = "rgba(255,255,255,.6)";
  ctx.font = `400 36px ${FONT}`;
  ctx.fillText(fit(ctx, t("share.line", { series: s.series, n: s.number, total: s.total }), W - 120), W / 2, 1068);

  // The QR code to the series, bottom right; the invitation beside it.
  const qr = encode(s.url, { border: 1 });
  const box = 196;
  const cell = Math.floor(box / qr.size);
  const side = cell * qr.size;
  const qx = W - 80 - side;
  const qy = H - 80 - side;
  ctx.fillStyle = "#fff";
  ctx.beginPath();
  ctx.roundRect(qx - 12, qy - 12, side + 24, side + 24, 16);
  ctx.fill();
  ctx.fillStyle = "#111";
  qr.data.forEach((row, y) => row.forEach((on, x) => { if (on) ctx.fillRect(qx + x * cell, qy + y * cell, cell, cell); }));

  ctx.textAlign = "left";
  ctx.fillStyle = "#fff";
  ctx.font = `600 40px ${FONT}`;
  ctx.fillText(fit(ctx, t("share.question"), qx - 120), 80, qy + 70);
  ctx.fillStyle = "rgba(255,255,255,.55)";
  ctx.font = `400 30px ${FONT}`;
  ctx.fillText(fit(ctx, t("share.scan"), qx - 120), 80, qy + 124);
  ctx.fillText(fit(ctx, s.url.replace(/^https?:\/\//, ""), qx - 120), 80, qy + 170);

  return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error(t("share.failed")))), "image/png"));
}

/**
 * Shares the card: the system share sheet where it takes images (phones), otherwise the image downloads and the
 * link is copied. Returns what happened, for the toast.
 */
export async function shareEnding(s: EndingShare): Promise<"shared" | "saved"> {
  const blob = await endingCard(s);
  const text = t("share.text", { series: s.series, ending: s.ending });
  const file = new File([blob], `${t("share.fileName", { ending: s.ending })}.png`, { type: "image/png" });
  const nav = navigator as Navigator & { canShare?: (data: ShareData) => boolean };
  if (nav.canShare?.({ files: [file] })) {
    try {
      await nav.share({ files: [file], title: s.series, text, url: s.url });
      return "shared";
    } catch (e) {
      if (e instanceof DOMException && e.name === "AbortError") return "shared";
    }
  }
  const link = document.createElement("a");
  link.href = URL.createObjectURL(blob);
  link.download = file.name;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(link.href), 10_000);
  try {
    await navigator.clipboard?.writeText(`${text} ${s.url}`);
  } catch {
    /* the image is enough */
  }
  return "saved";
}
