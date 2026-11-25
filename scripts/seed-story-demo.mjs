#!/usr/bin/env node
// Seeds a demo interactive series (互动剧) through the real admin pipeline: generates seven labelled scenes with
// ffmpeg, uploads and imports them, waits for the worker, makes the series interactive, saves its story and publishes.
// The story is the design plan's example 《深夜来客》: S1 → S2 / S3, S2 → S4 / ending 1, S3 continues to S4,
// S4 → ending 2 / ending 3 (the last one paid, to try the unlock flow). A hidden score, 好感度, goes up when you open the
// door (+1) and help her (+1); 「替她隐瞒」 only shows at 好感度 ≥ 2, so the guarded path ends in 「真相」.
//
//   node scripts/seed-story-demo.mjs [--api http://localhost:5080] [--user admin] [--password reelguo-dev-admin] [--seconds 12]
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
const PASSWORD = args.password ?? process.env.REELGUO_ADMIN_PASSWORD ?? "reelguo-dev-admin";
const SECONDS = Number(args.seconds ?? 12);

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const exe = process.platform === "win32" ? ".exe" : "";
const ffmpegDir = [process.env.REELGUO_FFMPEG_DIR, join(repo, ".tools", "ffmpeg", "bin")].find((d) => d && existsSync(join(d, `ffmpeg${exe}`)));
const FFMPEG = ffmpegDir ? join(ffmpegDir, `ffmpeg${exe}`) : "ffmpeg";
const FONT = [
  process.env.REELGUO_FONT,
  "C:/Windows/Fonts/msyh.ttc",
  "C:/Windows/Fonts/simhei.ttf",
  "/System/Library/Fonts/PingFang.ttc",
  "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
  "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
].find((f) => f && existsSync(f));

const TITLE = "深夜来客（互动剧示例）";
const SCENES = [
  { title: "开场：深夜敲门", line: "凌晨一点，门铃响了。", hue: 220 },
  { title: "屋内叙旧", line: "她浑身湿透，说只待一晚。", hue: 20 },
  { title: "门缝里的纸条", line: "她没再敲门，只塞进来一张纸条。", hue: 170 },
  { title: "警察上门", line: "第二天一早，警察站在门口。", hue: 280 },
  { title: "结局·各自安好", line: "你关上门，这一夜什么都没发生。", hue: 100 },
  { title: "结局·真相", line: "她被带走前，回头看了你一眼。", hue: 45 },
  { title: "结局·共犯", line: "从今天起，你们有了同一个秘密。", hue: 340 },
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

const escapeText = (s) => s.replace(/\\/g, "\\\\").replace(/:/g, "\\:").replace(/'/g, "\u2019").replace(/%/g, "\\%");

async function makeClip(path, scene, n) {
  const font = FONT ? `fontfile='${FONT.replace(/:/g, "\\:")}':` : "";
  const draw = (text, y, size, raw = false) =>
    `drawtext=${font}text='${raw ? text : escapeText(text)}':fontcolor=white:fontsize=${size}:x=(w-text_w)/2:y=${y}:box=1:boxcolor=black@0.45:boxborderw=24`;
  const lines = FONT ? [`S${n} ${scene.title}`, scene.line] : [`Scene ${n}`, "interactive demo"];
  const filter = [`hue=h=${scene.hue}`, draw(lines[0], "h*0.36", 64), draw(lines[1], "h*0.36+120", 46), draw("%{pts\\:hms}", "h*0.36+220", 44, true)].join(",");
  await run(FFMPEG, [
    "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=1080x1920:rate=30",
    "-f", "lavfi", "-i", `sine=frequency=${260 + n * 50}:sample_rate=48000`,
    "-t", String(SECONDS), "-vf", filter,
    "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", path,
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
const csvCell = (v) => `"${String(v).replace(/"/g, '""')}"`;

async function main() {
  console.log(`API ${API} · ffmpeg ${FFMPEG} · font ${FONT ?? "(none)"}`);
  await api("POST", "/api/admin/auth/login", { username: USER, password: PASSWORD });

  const work = mkdtempSync(join(tmpdir(), "reelguo-story-"));
  const today = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
  const rows = ["external_id,series_title,category,episode,episode_title,file,description,tags,author,score,total_episodes,is_finished,rights_holder,contract_ref,licence_start,territories,ai_generated,has_real_person,episode_price,free_episodes"];
  try {
    for (const [i, scene] of SCENES.entries()) {
      const n = i + 1;
      const file = `story-demo-s${n}.mp4`;
      process.stdout.write(`clip ${file}... `);
      await makeClip(join(work, file), scene, n);
      await upload(file, join(work, file));
      console.log("uploaded");
      rows.push([
        "DEMO-STORY", csvCell(TITLE), "ai-duanju", n, csvCell(scene.title), file,
        csvCell("互动剧示例：每段播完由你决定接下来发生什么，共 3 个结局。由 ffmpeg 生成，不含任何真实人物。"), "悬疑|互动", "黄月短剧示例工作室",
        "8.2", SCENES.length, "是", "黄月短剧（示例）", "DEMO-0002", today, "*", "是", "否",
        // S1-S6 free; S7 (结局·共犯) costs 30 coins, to try unlocking a scene and 换个选择.
        30, 6,
      ].join(","));
    }
  } finally {
    rmSync(work, { recursive: true, force: true });
  }

  const csv = rows.join("\n");
  const dry = await api("POST", "/api/admin/import", { csv, dry_run: true });
  if (!dry.ok) throw new Error("manifest invalid: " + dry.errors.join("; "));
  const report = await api("POST", "/api/admin/import", { csv, dry_run: false });
  const id = report.series[0].series_id;
  console.log(`imported #${id}; waiting for the worker...`);
  let detail;
  for (let attempt = 0; ; attempt++) {
    detail = await api("GET", `/api/admin/series/${id}`);
    const failed = detail.episodes.filter((e) => e.status === "failed");
    if (failed.length) throw new Error(`scene failed: ${failed[0].error}`);
    if (detail.episodes.length === SCENES.length && detail.episodes.every((e) => e.status === "ready")) break;
    if (attempt > 600) throw new Error("timed out waiting for the worker (is it running?)");
    await sleep(2000);
  }

  // Make it a 互动剧 (the import creates ordinary series).
  const s = detail;
  await api("PUT", `/api/admin/series/${id}`, {
    kind: "interactive", category_slug: s.category_slug, title: s.title, description: s.description, tags: s.tags, score: s.score,
    is_finished: s.is_finished, is_original: s.is_original, total_episodes: s.total_episodes, author_name: s.author_name,
    external_id: s.external_id, rights_holder: s.rights_holder, contract_ref: s.contract_ref, licence_start: s.licence_start,
    licence_end: s.licence_end, territories: s.territories, ai_generated: s.ai_generated, has_real_person: s.has_real_person,
    consent_ref: s.consent_ref, recommend_weight: s.recommend_weight, episode_price: s.episode_price, free_episodes: s.free_episodes,
    card_badge: s.card_badge,
  });

  const ep = Object.fromEntries(detail.episodes.map((e) => [e.episode_number, e.id]));
  const option = (label, to, isDefault = false, description = null, logic = {}) =>
    ({ id: null, label, description, target_episode_id: ep[to], is_default: isDefault, effects: [], conditions: [], ...logic });
  const plus = (add) => ({ effects: [{ variable: "affection", add }] });
  const choice = (n, prompt, options, extra = {}) => ({ episode_id: ep[n], outcome: "choice", prompt, lead_seconds: 0, countdown: null, options, ...extra });
  await api("PUT", `/api/admin/series/${id}/story`, {
    start_episode_id: ep[1],
    variables: [{ key: "affection", name: "好感度", initial: 0 }],
    scenes: [
      choice(1, "门外是消失三年的前任，你要？",
        [option("开门让她进来", 2, true, "听听她想说什么", plus(1)), option("隔着门说话", 3, false, "先保持距离")], { countdown: 10 }),
      choice(2, "她说自己惹上了麻烦，你要？",
        [option("留下帮她", 4, false, "不管是什么麻烦", plus(1)), option("送她离开", 5, false, "不想再被卷进去")], { lead_seconds: 3 }),
      { episode_id: ep[3], outcome: "continue", next_episode_id: ep[4] },
      choice(4, "警察问你有没有见过她，你要？", [
        option("说出实情", 6),
        option("替她隐瞒", 7, false, "通往付费结局", { conditions: [{ variable: "affection", op: "gte", value: 2 }] }),
      ]),
      { episode_id: ep[5], outcome: "ending", ending_title: "各自安好" },
      { episode_id: ep[6], outcome: "ending", ending_title: "真相" },
      { episode_id: ep[7], outcome: "ending", ending_title: "共犯" },
    ],
  });
  await api("POST", `/api/admin/series/${id}/publish`);
  console.log(`published #${id} ${TITLE} (/video/${id})`);
}

main().catch((e) => {
  console.error(e.message ?? e);
  process.exit(1);
});
