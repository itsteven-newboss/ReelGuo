#!/usr/bin/env node
// Seeds demo content through the real admin pipeline: generates labelled sample clips with ffmpeg,
// uploads them to incoming/, imports a manifest, waits for the worker to encode them, then publishes.
//
//   node scripts/seed-demo.mjs [--api http://localhost:5080] [--user admin] [--password reelguo-dev-admin]
//                              [--series 4] [--episodes 5] [--seconds 20]
//
// Requires the API and the worker to be running, and ffmpeg (REELGUO_FFMPEG_DIR, .tools/ffmpeg/bin or PATH).
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith("--") ? [...acc, [a.slice(2), all[i + 1]]] : acc), []),
);
const API = args.api ?? "http://localhost:5080";
const USER = args.user ?? "admin";
// The password can also come from REELGUO_ADMIN_PASSWORD (scripts/staging/seed-demo.ps1 passes it that way).
const PASSWORD = args.password ?? process.env.REELGUO_ADMIN_PASSWORD ?? "reelguo-dev-admin";
const SERIES = Number(args.series ?? 4);
const EPISODES = Number(args.episodes ?? 5);
const SECONDS = Number(args.seconds ?? 20);

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const exe = process.platform === "win32" ? ".exe" : "";
const ffmpegDir = [process.env.REELGUO_FFMPEG_DIR, join(repo, ".tools", "ffmpeg", "bin")].find((d) => d && existsSync(join(d, `ffmpeg${exe}`)));
const FFMPEG = ffmpegDir ? join(ffmpegDir, `ffmpeg${exe}`) : "ffmpeg";

// A CJK font for the on-screen titles; without one the titles fall back to Latin text.
const FONT = [
  process.env.REELGUO_FONT,
  "C:/Windows/Fonts/msyh.ttc",
  "C:/Windows/Fonts/simhei.ttf",
  "/System/Library/Fonts/PingFang.ttc",
  "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
  "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
].find((f) => f && existsSync(f));

const DEMO = [
  { title: "契约总裁的替身新娘（示例）", category: "ai-duanju", tags: "都市|逆袭", source: "testsrc2", hue: 0 },
  { title: "重生之我在豪门当千金（示例）", category: "ai-duanju", tags: "重生|豪门", source: "mandelbrot", hue: 60 },
  { title: "星辰之约·动态漫（示例）", category: "ai-manju", tags: "奇幻|热血", source: "testsrc2", hue: 200 },
  { title: "名场面魔改：大结局重写（示例）", category: "ai-mogai", tags: "脑洞|搞笑", source: "smptehdbars", hue: 300 },
  { title: "都市修仙录（示例）", category: "ai-duanju", tags: "仙侠|都市", source: "testsrc2", hue: 120 },
  { title: "末日求生指南·漫剧（示例）", category: "ai-manju", tags: "末日|冒险", source: "mandelbrot", hue: 30 },
];

let cookie = "";

async function api(method, path, body) {
  const res = await fetch(API + path, {
    method,
    headers: { "Content-Type": "application/json", "X-ReelGuo-Admin": "1", ...(cookie ? { Cookie: cookie } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const setCookie = res.headers.get("set-cookie");
  if (setCookie) cookie = setCookie.split(";")[0];
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${text}`);
  return data;
}

function run(cmd, cmdArgs) {
  return new Promise((ok, fail) => {
    const p = spawn(cmd, cmdArgs, { stdio: ["ignore", "ignore", "pipe"] });
    let err = "";
    p.stderr.on("data", (d) => { err = (err + d).slice(-2000); });
    p.on("error", fail);
    p.on("close", (code) => (code === 0 ? ok() : fail(new Error(`${cmd} exited ${code}: ${err}`))));
  });
}

function escapeText(s) {
  return s.replace(/\\/g, "\\\\").replace(/:/g, "\\:").replace(/'/g, "\u2019").replace(/%/g, "\\%");
}

async function makeClip(path, demo, ep) {
  const source = demo.source === "mandelbrot" ? `mandelbrot=size=1080x1920:rate=30` : `${demo.source}=size=1080x1920:rate=30`;
  const title = FONT ? demo.title : `Demo series ${demo.category}`;
  const line2 = FONT ? `第 ${ep} 集 · 示例内容` : `Episode ${ep} - sample`;
  const font = FONT ? `fontfile='${FONT.replace(/:/g, "\\:")}':` : "";
  const draw = (text, y, size, raw = false) =>
    `drawtext=${font}text='${raw ? text : escapeText(text)}':fontcolor=white:fontsize=${size}:x=(w-text_w)/2:y=${y}:box=1:boxcolor=black@0.45:boxborderw=24`;
  // A running clock on screen makes seeking and episode switches easy to check by eye.
  const filter = [`hue=h=${demo.hue}`, draw(title, "h*0.38", 64), draw(line2, "h*0.38+120", 52), draw("%{pts\\:hms}", "h*0.38+220", 44, true)].join(",");
  await run(FFMPEG, [
    "-hide_banner", "-y",
    "-f", "lavfi", "-i", source,
    "-f", "lavfi", "-i", `sine=frequency=${300 + ep * 40}:sample_rate=48000`,
    "-t", String(SECONDS), "-vf", filter,
    "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k",
    path,
  ]);
}

async function upload(fileName, path) {
  const ticket = await api("POST", "/api/admin/import/incoming", { file_name: fileName, content_type: "video/mp4" });
  const url = ticket.url.startsWith("/") ? API + ticket.url : ticket.url;
  const res = await fetch(url, {
    method: ticket.method,
    headers: { ...ticket.headers, ...(ticket.url.startsWith("/") ? { Cookie: cookie } : {}) },
    body: readFileSync(path),
  });
  if (!res.ok) throw new Error(`upload ${fileName} -> ${res.status}`);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function main() {
  console.log(`API ${API} · ffmpeg ${FFMPEG} · font ${FONT ?? "(none)"}`);
  await api("POST", "/api/admin/auth/login", { username: USER, password: PASSWORD });

  // --series 0: no new videos; add demo posts and plays on top of what is already published.
  if (SERIES === 0) {
    const list = await api("GET", "/api/admin/series?status=published&size=50");
    const ids = list.items.map((s) => s.id);
    await seedPosts(ids);
    await seedPlays(ids);
    console.log("done");
    return;
  }

  const work = mkdtempSync(join(tmpdir(), "reelguo-seed-"));
  const today = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
  const rows = ["external_id,series_title,category,episode,episode_title,file,description,tags,author,score,total_episodes,is_finished,rights_holder,contract_ref,licence_start,territories,ai_generated,has_real_person,episode_price,free_episodes"];
  try {
    for (let s = 0; s < SERIES; s++) {
      const demo = DEMO[s % DEMO.length];
      for (let ep = 1; ep <= EPISODES; ep++) {
        const file = `demo-${s + 1}-ep${String(ep).padStart(2, "0")}.mp4`;
        const path = join(work, file);
        process.stdout.write(`clip ${file}... `);
        await makeClip(path, demo, ep);
        await upload(file, path);
        console.log("uploaded");
        rows.push([
          `DEMO-${s + 1}`, `"${demo.title}"`, demo.category, ep, `第${ep}集`, file,
          `"示例内容，用于开发和测试。由 ffmpeg 生成，不含任何真实人物。"`, demo.tags, "ReelGuo 示例工作室",
          (7.5 + (s % 4) * 0.4).toFixed(1), EPISODES + 3, "否", "ReelGuo Demo", "DEMO-0001", today, "*", "是", "否",
          // The first demo series is paid (episodes 3+ cost 30 coins), to try the paywall and the sandbox store.
          s === 0 ? 30 : 0, s === 0 ? 2 : 3,
        ].join(","));
      }
    }
  } finally {
    rmSync(work, { recursive: true, force: true });
  }

  const csv = rows.join("\n");
  const dry = await api("POST", "/api/admin/import", { csv, dry_run: true });
  if (!dry.ok) throw new Error("manifest invalid: " + dry.errors.join("; "));
  const report = await api("POST", "/api/admin/import", { csv, dry_run: false });
  console.log(`imported ${report.series.length} series, queued ${report.queued_jobs} episodes; waiting for the worker...`);

  for (const s of report.series) {
    for (let attempt = 0; ; attempt++) {
      const detail = await api("GET", `/api/admin/series/${s.series_id}`);
      const failed = detail.episodes.filter((e) => e.status === "failed");
      if (failed.length) throw new Error(`episode failed: ${failed[0].error}`);
      if (detail.episodes.every((e) => e.status === "ready")) break;
      if (attempt > 600) throw new Error("timed out waiting for the worker (is it running?)");
      await sleep(2000);
    }
    await api("POST", `/api/admin/series/${s.series_id}/publish`);
    console.log(`published #${s.series_id} ${s.title}`);
  }
  await seedPosts(report.series.map((s) => s.series_id));
  await seedPlays(report.series.map((s) => s.series_id));
  console.log("done");
}

/** Two labelled demo articles, one linked to a series. */
async function seedPosts(seriesIds) {
  const work = mkdtempSync(join(tmpdir(), "reelguo-seed-"));
  const posts = [
    {
      title: "示例：本周短剧上新速览",
      summary: "本周新上线的示例剧集一览（示例内容）。",
      tags: ["上新", "周报"],
      series_id: seriesIds[0] ?? null,
      content_markdown: "## 本周上新\n\n以下为**示例内容**，用于开发和测试。\n\n- 示例剧集一：都市逆袭题材\n- 示例剧集二：豪门重生题材\n\n> 所有示例画面由 ffmpeg 生成，不含任何真实人物。\n\n点击上方的「观看正片」即可播放关联剧集。",
      color: "0x48307f",
    },
    {
      title: "示例：AI 漫剧是怎么做出来的",
      summary: "从剧本到成片的制作流程（示例内容）。",
      tags: ["幕后", "AI"],
      series_id: null,
      content_markdown: "## 制作流程\n\n1. 剧本与分镜\n2. 角色设定（**全部为成年原创角色**）\n3. 画面生成与剪辑\n4. 配音与字幕\n\n本文为示例内容。",
      color: "0x1f5a78",
    },
  ];
  try {
    for (const [i, p] of posts.entries()) {
      const created = await api("POST", "/api/admin/posts", p);
      const cover = join(work, `post-${i}.jpg`);
      await run(FFMPEG, ["-hide_banner", "-y", "-f", "lavfi", "-i", `color=c=${p.color}:s=720x960`, "-frames:v", "1",
        "-vf", FONT ? `drawtext=fontfile='${FONT.replace(/:/g, "\\:")}':text='${escapeText(p.title)}':fontcolor=white:fontsize=40:x=(w-text_w)/2:y=h*0.45` : "null",
        cover]);
      const res = await fetch(`${API}/api/admin/posts/${created.id}/cover`, {
        method: "POST", body: readFileSync(cover), headers: { "Content-Type": "image/jpeg", "X-ReelGuo-Admin": "1", Cookie: cookie },
      });
      if (!res.ok) throw new Error(`post cover -> ${res.status}`);
      await api("POST", `/api/admin/posts/${created.id}/publish`);
      console.log(`published post #${created.id} ${p.title}`);
    }
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

/** Anonymous demo play traffic so the rankings are not empty, then an immediate recompute. */
async function seedPlays(seriesIds) {
  const events = [];
  seriesIds.forEach((id, i) => {
    const viewers = Math.max(1, seriesIds.length - i) * 3;
    for (let v = 0; v < viewers; v++) {
      const session = `demoseed-${id}-${v}`.padEnd(12, "0");
      events.push({ session, list: [{ type: "start", series_id: id, ep: 1 }, ...(v % 2 ? [{ type: "complete", series_id: id, ep: 1 }] : [])] });
    }
  });
  for (const e of events) {
    await fetch(`${API}/api/v1/events`, {
      method: "POST", headers: { "Content-Type": "application/json", "X-Debug-Country": "US" },
      body: JSON.stringify({ session_id: e.session, events: e.list }),
    });
  }
  const run = await api("POST", "/api/admin/ranks/recompute");
  console.log(`demo plays: ${events.length} sessions; rankings hot ${run.hot}, recommend ${run.recommend}, potential ${run.potential}`);
}

main().catch((e) => {
  console.error(e.message);
  process.exit(1);
});
