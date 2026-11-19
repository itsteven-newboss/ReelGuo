import type { VideoCard } from "../api";

/**
 * Which status badge a card shows. Set per series in admin (新片 / 热播 / none); "auto" (series from before that) shows
 * 热播 while in the top of the 热播榜, else 新片 for the first week.
 */
export function statusBadge(setting: VideoCard["card_badge"] | undefined, hot: boolean, fresh: boolean): "hot" | "new" | null {
  if (setting === "new") return "new";
  if (setting === "hot") return "hot";
  if (setting === "none") return null;
  return hot ? "hot" : fresh ? "new" : null;
}
