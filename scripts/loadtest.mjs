#!/usr/bin/env node
// Load test: simulated viewers browse the feed, open a series, get a play link and fetch media, the way the app
// does. Prints latency percentiles per step and fails (exit 1) when the thresholds are not met.
//
//   node scripts/loadtest.mjs [--app http://localhost:5080] [--vus 50] [--duration 60] [--ramp 10]
//                             [--think 300] [--country US] [--p95 300] [--media-p95 800] [--max-errors 1]
//
//   --vus         concurrent simulated viewers          --duration  seconds at full load (after the ramp)
//   --think       max pause between steps, ms           --p95       API p95 limit, ms (feed, series, play, ...)
//   --media-p95   playlist/segment p95 limit, ms        --max-errors  % of requests allowed to fail (5xx/network)
//
// From one machine every request has the same IP, so the per-IP rate limits (play events, auth) will answer 429;
// those are reported but not counted as failures. Run it against staging, never production.
const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith("--") ? [...acc, [a.slice(2), all[i + 1]]] : acc), []),
);
const APP = String(args.app ?? "http://localhost:5080").replace(/\/$/, "");
const VUS = Number(args.vus ?? 50);
const DURATION_S = Number(args.duration ?? 60);
const RAMP_S = Number(args.ramp ?? 10);
const THINK_MS = Number(args.think ?? 300);
const COUNTRY = String(args.country ?? "US");
const API_P95 = Number(args.p95 ?? 300);
const MEDIA_P95 = Number(args["media-p95"] ?? 800);
const MAX_ERRORS = Number(args["max-errors"] ?? 1);

const stats = new Map(); // step -> { ms: number[], errors, limited, other4xx }
const stat = (step) => stats.get(step) ?? (stats.set(step, { ms: [], errors: 0, limited: 0, other4xx: 0 }), stats.get(step));
const headers = { "X-Debug-Country": COUNTRY, Accept: "application/json" };

async function call(step, url, init = {}) {
  const s = stat(step);
  const t0 = performance.now();
  try {
    const res = await fetch(url.startsWith("http") ? url : `${APP}${url}`, { ...init, headers: { ...headers, ...init.headers } });
    const body = init.method === "POST" ? await res.arrayBuffer() : res.headers.get("content-type")?.includes("json") ? await res.json() : await res.text();
    s.ms.push(performance.now() - t0);
    if (res.status >= 500) s.errors++;
    else if (res.status === 429) s.limited++;
    else if (res.status >= 400) s.other4xx++;
    return res.ok ? body : null;
  } catch {
    s.ms.push(performance.now() - t0);
    s.errors++;
    return null;
  }
}

const pause = () => new Promise((r) => setTimeout(r, Math.random() * THINK_MS));
const pick = (list) => list[Math.floor(Math.random() * list.length)];
const SEARCHES = ["总裁", "重生", "都市", "古风", "逆袭"];
const session = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, "0")).join("");

async function viewer(stopAt) {
  const sid = session();
  let iteration = 0;
  while (performance.now() < stopAt) {
    iteration++;
    const feed = await call("feed", `/api/v1/feed?page=${1 + (iteration % 3)}&size=10`);
    await pause();
    const items = feed?.items ?? [];
    if (!items.length) continue;
    const item = pick(items);
    const detail = await call("series", `/api/v1/series/${item.id}`);
    const ep = detail?.episodes?.[0]?.ep_num;
    if (!ep) continue;
    const play = await call("play", `/api/v1/series/${item.id}/episodes/${ep}/play`);
    if (play?.video_url) {
      const master = new URL(play.video_url, `${APP}/`).toString();
      const masterText = await call("media", master);
      const variantLine = typeof masterText === "string" ? masterText.split("\n").find((l) => l && !l.startsWith("#")) : null;
      if (variantLine) {
        const variant = new URL(variantLine, master).toString();
        const variantText = await call("media", variant);
        const segments = typeof variantText === "string" ? variantText.split("\n").filter((l) => l && !l.startsWith("#")).slice(0, 2) : [];
        for (const seg of segments) await call("media", new URL(seg, variant).toString());
      }
    }
    await pause();
    if (iteration % 4 === 0) await call("ranks", `/api/v1/ranks/${pick(["hot", "recommend", "potential"])}`);
    if (iteration % 5 === 0) await call("search", `/api/v1/search?q=${encodeURIComponent(pick(SEARCHES))}`);
    if (iteration % 3 === 0) {
      await call("comments", `/api/v1/series/${item.id}/episodes/${ep}/comments`);
      await call("events", "/api/v1/events", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ session_id: sid, events: [{ type: "start", series_id: item.id, ep }], samples: [] }),
      });
    }
  }
}

function pct(sorted, p) {
  if (!sorted.length) return 0;
  return sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];
}

console.log(`Load test: ${VUS} viewers, ${RAMP_S} s ramp + ${DURATION_S} s, think ≤ ${THINK_MS} ms, against ${APP}`);
const started = performance.now();
const stopAt = started + (RAMP_S + DURATION_S) * 1000;
await Promise.all(Array.from({ length: VUS }, (_, i) =>
  new Promise((r) => setTimeout(r, (i / VUS) * RAMP_S * 1000)).then(() => viewer(stopAt))));
const elapsed = (performance.now() - started) / 1000;

let total = 0;
let errors = 0;
let failed = false;
console.log("");
console.log("step        requests    rps     p50     p95     p99   5xx/net   429   other 4xx");
for (const [step, s] of [...stats].sort()) {
  const sorted = [...s.ms].sort((a, b) => a - b);
  const p95 = pct(sorted, 95);
  const limit = step === "media" ? MEDIA_P95 : step === "events" ? Infinity : API_P95;
  const over = p95 > limit;
  failed ||= over;
  total += s.ms.length;
  errors += s.errors;
  console.log(`${step.padEnd(10)} ${String(s.ms.length).padStart(9)} ${(s.ms.length / elapsed).toFixed(1).padStart(6)} ${pct(sorted, 50).toFixed(0).padStart(7)} ${p95.toFixed(0).padStart(7)}${over ? "!" : " "}${pct(sorted, 99).toFixed(0).padStart(7)} ${String(s.errors).padStart(9)} ${String(s.limited).padStart(5)} ${String(s.other4xx).padStart(11)}`);
}
const errorPct = total ? (errors / total) * 100 : 100;
failed ||= errorPct > MAX_ERRORS || total === 0;
console.log("");
console.log(`total ${total} requests in ${elapsed.toFixed(0)} s (${(total / elapsed).toFixed(0)} rps), failures ${errorPct.toFixed(2)}%`);
console.log(`thresholds: API p95 ≤ ${API_P95} ms, media p95 ≤ ${MEDIA_P95} ms, failures ≤ ${MAX_ERRORS}%  →  ${failed ? "FAIL" : "PASS"}`);
process.exit(failed ? 1 : 0);
