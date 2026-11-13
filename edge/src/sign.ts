/**
 * Signed media URLs, shared rules with the API (backend/src/ReelGuo.Data/Media/MediaUrls.cs):
 *   /m/{exp}/{sig}/s/{series}/e/{ep}/v{version}/{file}
 *   sig = base64url(HMAC-SHA256(key, "{exp}:{prefix}")), no padding, prefix = "s/{series}/e/{ep}/v{version}/"
 */

const EPISODE_PREFIX = /^s\/\d{1,18}\/e\/\d{1,6}\/v\d{1,6}\//;

export function signedPrefixOf(path: string): string | null {
  return EPISODE_PREFIX.exec(path)?.[0] ?? null;
}

/** Series id of an episode media path ("s/{id}/e/..."), or null (same as the API's MediaPaths.SeriesIdOf). */
export function seriesIdOf(path: string): number | null {
  const m = /^s\/(\d{1,15})\/e\/\d{1,6}\/v\d{1,6}\//.exec(path);
  return m ? Number(m[1]) : null;
}

export async function importKey(base64Key: string): Promise<CryptoKey> {
  const raw = Uint8Array.from(atob(base64Key), (c) => c.charCodeAt(0));
  if (raw.length < 32) throw new Error("MEDIA_SIGNING_KEY must be at least 32 bytes");
  return crypto.subtle.importKey("raw", raw, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
}

export async function sign(key: CryptoKey, exp: number, prefix: string): Promise<string> {
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${exp}:${prefix}`)));
  let binary = "";
  for (const b of mac) binary += String.fromCharCode(b);
  return btoa(binary).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

function timingSafeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/** Validates /m/{exp}/{sig}/{path}. `nowSeconds` is injectable for tests. */
export async function isValid(key: CryptoKey, exp: number, signature: string, path: string, nowSeconds = Math.floor(Date.now() / 1000)): Promise<boolean> {
  if (!Number.isSafeInteger(exp) || exp < nowSeconds) return false;
  const prefix = signedPrefixOf(path);
  if (!prefix) return false;
  return timingSafeEqual(await sign(key, exp, prefix), signature);
}

/** Same safety rule as the API's StorageKeys.IsValid: relative paths of safe characters, no "." or ".." segments. */
export function isSafeKey(key: string): boolean {
  return key.length > 0 && key.length <= 512 && /^[A-Za-z0-9._/-]+$/.test(key)
    && !key.split("/").some((p) => p === "" || p === "." || p === "..");
}
