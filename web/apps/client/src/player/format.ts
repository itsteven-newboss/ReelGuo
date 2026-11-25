import { locale, t } from "../i18n";

const zh = locale.startsWith("zh");
/** Other languages: the browser's short form in the site's language (1,2 rb · 1,2 N · 1,2 mil). */
const compact = zh ? null : new Intl.NumberFormat(locale, { notation: "compact", maximumFractionDigits: 1 });

/** Counts the way the reference shows them: 9999, 1.2万, 3亿 (other languages: their own short form). */
export function fmtCount(n: number | undefined | null): string {
  const v = Number(n) || 0;
  if (compact) return compact.format(v);
  if (v >= 100_000_000) return `${(v / 100_000_000).toFixed(1).replace(/\.0$/, "")}亿`;
  if (v >= 10_000) return `${(v / 10_000).toFixed(1).replace(/\.0$/, "")}万`;
  return String(v);
}

/** Heat on the ranking pages, as the reference shows it: 9999, 1.2W (other languages: their own short form). */
export function fmtCompact(n: number): string {
  if (compact) return compact.format(Math.round(n));
  return n >= 10_000 ? `${(n / 10_000).toFixed(1).replace(/\.0$/, "")}W` : String(Math.round(n));
}

/** 75 -> "1:15", 3725 -> "1:02:05". */
export function fmtTime(seconds: number): string {
  const s = Number.isFinite(seconds) && seconds > 0 ? Math.floor(seconds) : 0;
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = String(s % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${sec}` : `${m}:${sec}`;
}

/** Comment times like the reference: 刚刚, 5分钟前, 3小时前, 2天前, then the date (11-02, or 2025-11-02 in another year). */
export function fmtAgo(iso: string, now: number = Date.now()): string {
  const at = new Date(iso);
  const diff = Math.max(0, now - at.getTime()) / 1000;
  if (diff < 60) return t("time.justNow");
  if (diff < 3600) return t("time.minutesAgo", { count: Math.floor(diff / 60) });
  if (diff < 86_400) return t("time.hoursAgo", { count: Math.floor(diff / 3600) });
  if (diff < 7 * 86_400) return t("time.daysAgo", { count: Math.floor(diff / 86_400) });
  const md = `${String(at.getMonth() + 1).padStart(2, "0")}-${String(at.getDate()).padStart(2, "0")}`;
  return at.getFullYear() === new Date(now).getFullYear() ? md : `${at.getFullYear()}-${md}`;
}
