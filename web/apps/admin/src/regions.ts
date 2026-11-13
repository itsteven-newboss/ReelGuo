/** Chinese display names for block-list codes; the special codes are not real regions, so they get explicit labels. */
const SPECIAL: Record<string, string> = {
  XX: "未知地区（无法识别）",
  T1: "Tor 匿名网络",
  XK: "科索沃",
};

let displayNames: Intl.DisplayNames | null = null;
try {
  displayNames = new Intl.DisplayNames(["zh-CN"], { type: "region" });
} catch {
  displayNames = null;
}

export function regionName(code: string): string {
  if (SPECIAL[code]) return SPECIAL[code];
  try {
    return displayNames?.of(code) ?? code;
  } catch {
    return code;
  }
}

export function formatTime(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString("zh-CN", { hour12: false });
}
