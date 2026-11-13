/**
 * The native apps (Phase 6) are a thin shell around this same web app, loaded from the server. The shell appends
 * "ReelGuoApp/<version> (<desktop|android>)" to the user agent, which is how the page knows where it runs.
 */
export type NativeApp = { version: string; platform: "desktop" | "android" };

export function parseNativeApp(userAgent: string): NativeApp | null {
  const m = /\bReelGuoApp\/(\d+(?:\.\d+){0,2}) \((desktop|android)\)/.exec(userAgent);
  return m ? { version: m[1]!, platform: m[2] as NativeApp["platform"] } : null;
}

export const nativeApp: NativeApp | null = typeof navigator === "undefined" ? null : parseNativeApp(navigator.userAgent);
