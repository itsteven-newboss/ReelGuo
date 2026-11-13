#!/usr/bin/env node
// Takedown drill: proves a series stops playing everywhere after a takedown, including through play links that
// viewers already have, and measures how long it takes. Run it before launch and after any change to media delivery.
//
//   node scripts/takedown-drill.mjs [--app http://localhost:5080] [--admin http://localhost:5080]
//                                   [--user admin] [--password reelguo-dev-admin] [--series <id>] [--country US]
//                                   [--timeout 180] [--restore]
//
//   --app       origin serving /api/v1 (the app's own origin in production)
//   --admin     origin serving /api/admin (the admin host in production)
//   --series    series to take down (default: the first one in the feed). Use a drill/test series in production.
//   --country   X-Debug-Country to send (Development only; ignored elsewhere)
//   --restore   publish the series again afterwards
//
// Exit code 0 = every check went dark within the timeout. Paste the printed report into the takedown log.
const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith("--") ? [...acc, [a.slice(2), all[i + 1]?.startsWith("--") ? true : all[i + 1] ?? true]] : acc), []),
);
const APP = String(args.app ?? "http://localhost:5080").replace(/\/$/, "");
const ADMIN = String(args.admin ?? APP).replace(/\/$/, "");
const USER = String(args.user ?? "admin");
const PASSWORD = String(args.password ?? process.env.REELGUO_ADMIN_PASSWORD ?? "reelguo-dev-admin");
const COUNTRY = String(args.country ?? "US");
const TIMEOUT_S = Number(args.timeout ?? 180);

const viewerHeaders = { "X-Debug-Country": COUNTRY };
let adminCookie = "";

async function admin(method, path, body) {
  const res = await fetch(`${ADMIN}${path}`, {
    method,
    headers: { "X-ReelGuo-Admin": "1", Cookie: adminCookie, ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const cookie = res.headers.getSetCookie?.().find((c) => c.startsWith("rg_admin="));
  if (cookie) adminCookie = cookie.split(";")[0];
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}

async function viewer(path) {
  const res = await fetch(path.startsWith("http") ? path : `${APP}${path}`, { headers: viewerHeaders });
  return res;
}

const absolute = (url) => new URL(url, `${APP}/`).toString();
const now = () => performance.now();
const fmt = (ms) => `${(ms / 1000).toFixed(1)} s`;

// 1. Sign in and pick the series.
await admin("POST", "/api/admin/auth/login", { username: USER, password: PASSWORD });
let seriesId = args.series ? Number(args.series) : null;
if (!seriesId) {
  const feed = await (await viewer("/api/v1/feed?size=1")).json();
  seriesId = feed.items?.[0]?.id;
  if (!seriesId) throw new Error("No published series in the feed; pass --series.");
}
const detail = await (await viewer(`/api/v1/series/${seriesId}`)).json();
if (!detail.episodes?.length) throw new Error(`Series ${seriesId} has no playable episodes.`);
const ep = detail.episodes[0].ep_num;

// 2. Get a play link the way a viewer would, and resolve one media segment from it.
const play = await (await viewer(`/api/v1/series/${seriesId}/episodes/${ep}/play`)).json();
const master = absolute(play.video_url);
const masterText = await (await viewer(master)).text();
const variantPath = masterText.split("\n").find((l) => l && !l.startsWith("#"));
const variant = new URL(variantPath, master).toString();
const variantText = await (await viewer(variant)).text();
const segmentPath = variantText.split("\n").find((l) => l && !l.startsWith("#"));
const segment = new URL(segmentPath, variant).toString();

const checks = [
  { name: "series page   GET /api/v1/series/{id}", url: `${APP}/api/v1/series/${seriesId}`, dark: (s) => s === 404 },
  { name: "new play link GET /play", url: `${APP}/api/v1/series/${seriesId}/episodes/${ep}/play`, dark: (s) => s === 404 },
  { name: "issued link   master.m3u8", url: master, dark: (s) => s === 410 || s === 403 },
  { name: "issued link   media segment", url: segment, dark: (s) => s === 410 || s === 403 },
];
for (const c of checks) {
  const s = (await viewer(c.url)).status;
  if (c.dark(s) || s >= 400) throw new Error(`Before the takedown, "${c.name}" already answers ${s}; pick a playable series.`);
}

// 3. Take it down and watch every check go dark.
console.log(`Taking down series ${seriesId} 「${detail.title}」 (episode ${ep}) ...`);
const t0 = now();
await admin("POST", `/api/admin/series/${seriesId}/unpublish`);
const results = new Map();
while (results.size < checks.length && now() - t0 < TIMEOUT_S * 1000) {
  for (const c of checks) {
    if (results.has(c.name)) continue;
    const s = (await viewer(c.url)).status;
    if (c.dark(s)) results.set(c.name, { ms: now() - t0, status: s });
  }
  if (results.size < checks.length) await new Promise((r) => setTimeout(r, 2000));
}

// 4. Report.
const passed = results.size === checks.length;
console.log("");
console.log(`Takedown drill · ${new Date().toISOString()} · app ${APP} · series ${seriesId}`);
for (const c of checks) {
  const r = results.get(c.name);
  console.log(`  ${r ? "✓" : "✗"} ${c.name.padEnd(40)} ${r ? `${r.status} after ${fmt(r.ms)}` : `still served after ${TIMEOUT_S} s`}`);
}
console.log(passed ? "PASS: nothing from this series is served any more." : "FAIL: some media is still being served.");

if (args.restore) {
  await admin("POST", `/api/admin/series/${seriesId}/publish`);
  console.log(`Restored: series ${seriesId} is published again.`);
}
process.exit(passed ? 0 : 1);
