/**
 * The visitor's device, for the download page. The server reads it the same way for /app/download
 * (backend/src/ReelGuo.Api/Apps/DeviceDetect.cs); keep the two in step.
 */
export type DeviceOs = "android" | "ios" | "windows" | "mac" | "linux" | "harmony" | "other";

export type Device = {
  os: DeviceOs;
  /** A chat or social app's built-in browser (WeChat, QQ, Weibo, …). Those block downloads. */
  inApp: boolean;
  /** WeChat in particular (its menu says 在浏览器打开). */
  weChat: boolean;
  /** Safari on an iPhone or iPad: the only browser there that can install the home-screen profile. */
  safari: boolean;
};

// WeChat, QQ (the chat app; QQ浏览器 is a real browser), Weibo, Alipay, DingTalk, Douyin / TikTok, Kuaishou, Facebook,
// Instagram, LINE.
const IN_APP = /MicroMessenger|\bQQ\/|Weibo|AlipayClient|DingTalk|aweme|BytedanceWebview|musical_ly|Kwai|FBAN|FBAV|Instagram|\bLine\//i;
// iOS browsers other than Safari (all of them say "Safari/" as well).
const NOT_SAFARI = /CriOS|FxiOS|EdgiOS|OPiOS|OPT\/|YaBrowser|DuckDuckGo|GSA\/|Mercury|UCBrowser|MQQBrowser|baiduboxapp|Quark|SogouMobileBrowser|2345Browser/i;

/** `touchPoints`: an iPad asking for the desktop site says "Macintosh"; only its touch screen gives it away. */
export function detectDevice(ua: string, touchPoints = 0): Device {
  const os: DeviceOs =
    /OpenHarmony|ArkWeb/i.test(ua) ? "harmony"
    : /\bAndroid\b/i.test(ua) ? "android"
    : /\b(iPhone|iPad|iPod)\b/i.test(ua) || (/Macintosh/i.test(ua) && touchPoints > 1) ? "ios"
    : /Windows NT/i.test(ua) ? "windows"
    : /Macintosh|Mac OS X/i.test(ua) ? "mac"
    : /\bLinux\b|\bX11\b/i.test(ua) && !/\bCrOS\b/.test(ua) ? "linux"
    : "other";
  const inApp = IN_APP.test(ua);
  return { os, inApp, weChat: /MicroMessenger/i.test(ua), safari: os === "ios" && !inApp && /Safari\//.test(ua) && !NOT_SAFARI.test(ua) };
}

export const thisDevice = (): Device =>
  typeof navigator === "undefined" ? { os: "other", inApp: false, weChat: false, safari: false } : detectDevice(navigator.userAgent, navigator.maxTouchPoints ?? 0);

type UaData = { getHighEntropyValues?: (hints: string[]) => Promise<{ architecture?: string }> };

/**
 * A Mac's chip, when the browser tells: Chrome and Edge say so when asked; Firefox names the graphics chip. Safari
 * names neither (null), so the visitor picks.
 */
export async function macChip(): Promise<"arm" | "x86" | null> {
  try {
    const data = (navigator as Navigator & { userAgentData?: UaData }).userAgentData;
    const arch = (await data?.getHighEntropyValues?.(["architecture"]))?.architecture;
    if (arch === "arm") return "arm";
    if (arch === "x86") return "x86";
  } catch { /* not told */ }
  try {
    const gl = document.createElement("canvas").getContext("webgl");
    const info = gl?.getExtension("WEBGL_debug_renderer_info");
    const gpu = gl && info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : "";
    if (/Apple M\d/i.test(gpu)) return "arm";
    if (/Intel|AMD|Radeon|NVIDIA/i.test(gpu)) return "x86";
  } catch { /* no WebGL */ }
  return null;
}

/**
 * Why /app/download sent the visitor to the page instead of a file (`?install=…`): `ios` (show the install steps),
 * `mac` (pick the chip), `browser` (open the page outside WeChat or QQ first), `auto` (the server couldn't tell), or a
 * platform's link name when it has nothing published yet.
 */
export type InstallHint = "ios" | "mac" | "browser" | "auto" | "android" | "windows" | "linux" | "mac-arm" | "mac-intel";
const HINTS: InstallHint[] = ["ios", "mac", "browser", "auto", "android", "windows", "linux", "mac-arm", "mac-intel"];

export function installHint(search: string): InstallHint | null {
  const value = new URLSearchParams(search).get("install");
  return HINTS.includes(value as InstallHint) ? (value as InstallHint) : null;
}
