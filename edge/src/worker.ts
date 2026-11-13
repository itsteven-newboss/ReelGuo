/**
 * ReelGuo media edge (Cloudflare Worker in front of the R2 media bucket).
 * Mirrors the API's /media endpoints (backend/src/ReelGuo.Api/Media/MediaEdgeEndpoints.cs):
 *   GET /p/{key}                         public images
 *   GET /m/{exp}/{sig}/s/{id}/e/{ep}/v{n}/{file}   signed episode files
 * Both are refused with 451 for countries on the admin block list, and episode files of taken-down series with 410.
 * Both lists come from the API's /api/edge/config, cached 60 s, so a takedown stops playback within about a minute.
 */
import { importKey, isSafeKey, isValid, seriesIdOf } from "./sign";

// Minimal Cloudflare types, so this package needs no extra dependencies.
interface R2Range { offset?: number; length?: number; suffix?: number }
interface R2ObjectBody {
  body: ReadableStream;
  size: number;
  range?: R2Range;
  httpEtag: string;
  writeHttpMetadata(headers: Headers): void;
}
interface R2Bucket {
  get(key: string, options?: { range?: Headers; onlyIf?: Headers }): Promise<R2ObjectBody | null>;
}

export interface Env {
  MEDIA: R2Bucket;
  /** Same base64 key as the API's Media:SigningKey. */
  MEDIA_SIGNING_KEY: string;
  /** API origin, e.g. https://app.example.com */
  API_ORIGIN: string;
  /** Same value as the API's Edge:Secret. */
  EDGE_SECRET: string;
}

type CfRequest = Request & { cf?: { country?: string } };

let keyPromise: Promise<CryptoKey> | null = null;
type EdgeConfig = { codes: Set<string>; revoked: Set<number>; at: number };
let config: EdgeConfig | null = null;
const CONFIG_TTL_MS = 60_000;

async function edgeConfig(env: Env): Promise<EdgeConfig> {
  if (config && Date.now() - config.at < CONFIG_TTL_MS) return config;
  try {
    const res = await fetch(`${env.API_ORIGIN}/api/edge/config`, { headers: { "X-Edge-Secret": env.EDGE_SECRET } });
    if (!res.ok) throw new Error(`edge config ${res.status}`);
    const body = (await res.json()) as { blocked_countries: string[]; revoked_series?: number[] };
    config = { codes: new Set(body.blocked_countries), revoked: new Set(body.revoked_series ?? []), at: Date.now() };
  } catch (e) {
    // Keep enforcing the last known lists; with none yet, fail closed on media.
    if (!config) throw e;
  }
  return config;
}

function error(status: number, code: string, message: string): Response {
  return new Response(JSON.stringify({ error: { code, message } }), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store", "Access-Control-Allow-Origin": "*" },
  });
}

const CONTENT_TYPES: Record<string, string> = {
  m3u8: "application/vnd.apple.mpegurl",
  m4s: "video/iso.segment",
  mp4: "video/mp4",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  png: "image/png",
  webp: "image/webp",
};

async function serve(request: Request, env: Env, key: string): Promise<Response> {
  if (!isSafeKey(key)) return error(404, "not_found", "not found");
  const object = await env.MEDIA.get(key, { range: request.headers, onlyIf: request.headers });
  if (!object) return error(404, "not_found", "not found");

  const headers = new Headers();
  object.writeHttpMetadata(headers);
  const ext = key.split(".").pop()?.toLowerCase() ?? "";
  headers.set("Content-Type", CONTENT_TYPES[ext] ?? "application/octet-stream");
  headers.set("Cache-Control", ext === "m3u8" ? "public, max-age=60" : "public, max-age=31536000, immutable");
  headers.set("Access-Control-Allow-Origin", "*");
  headers.set("Accept-Ranges", "bytes");
  headers.set("ETag", object.httpEtag);
  // Responses depend on the visitor's country (451 vs 200): never share them across countries.
  headers.set("Vary", "CF-IPCountry");

  const range = object.range;
  if (range && request.headers.has("Range")) {
    const offset = range.offset ?? (range.suffix !== undefined ? object.size - range.suffix : 0);
    const length = range.length ?? object.size - offset;
    headers.set("Content-Range", `bytes ${offset}-${offset + length - 1}/${object.size}`);
    headers.set("Content-Length", String(length));
    return new Response(request.method === "HEAD" ? null : object.body, { status: 206, headers });
  }
  headers.set("Content-Length", String(object.size));
  return new Response(request.method === "HEAD" ? null : object.body, { status: 200, headers });
}

export default {
  async fetch(request: CfRequest, env: Env): Promise<Response> {
    if (request.method === "OPTIONS") {
      return new Response(null, { headers: { "Access-Control-Allow-Origin": "*", "Access-Control-Allow-Methods": "GET, HEAD", "Access-Control-Allow-Headers": "Range" } });
    }
    if (request.method !== "GET" && request.method !== "HEAD") return error(405, "method_not_allowed", "GET only");

    const country = request.cf?.country ?? "XX";
    let cfg: EdgeConfig;
    try {
      cfg = await edgeConfig(env);
    } catch {
      return error(503, "edge_config_unavailable", "暂时无法提供服务，请稍后重试");
    }
    if (cfg.codes.has(country)) return error(451, "region_blocked", "当前地区暂不提供服务");

    const path = decodeURIComponent(new URL(request.url).pathname).replace(/^\/+/, "");
    if (path.startsWith("p/")) return serve(request, env, path);

    const m = /^m\/(\d{1,12})\/([A-Za-z0-9_-]{20,64})\/(.+)$/.exec(path);
    if (!m) return error(404, "not_found", "not found");
    keyPromise ??= importKey(env.MEDIA_SIGNING_KEY);
    const [, exp, sig, rest] = m;
    if (!(await isValid(await keyPromise, Number(exp), sig!, rest!))) return error(403, "media_token_invalid", "播放链接已失效，请刷新");
    // Taken down after the link was issued: stop serving it.
    const seriesId = seriesIdOf(rest!);
    if (seriesId !== null && cfg.revoked.has(seriesId)) return error(410, "media_revoked", "该内容已下架");
    return serve(request, env, rest!);
  },
};
