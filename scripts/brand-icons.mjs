// Brand images for each site (web/sites/<key>/site.json), from one source picture per site. To change a site's icon,
// replace its source ("icon" in site.json), run `pnpm brand:icons` and commit what it writes:
//
//   web/sites/<key>/mark/mark-{64,128,256}.webp + mark.ts   the mark on the site's pages (BrandMark, srcset)
//   web/sites/<key>/public/favicon.ico                       16, 32 and 48 px (the site and its admin console)
//   web/sites/<key>/public/icons/icon-{192,512}.png          install icons (transparent)
//   web/sites/<key>/public/icons/maskable-512.png            Android install icon (inside the safe zone)
//   web/sites/<key>/public/icons/apple-touch-icon.png        iOS home screen (180 px, no transparency)
//   backend/src/ReelGuo.Api/Apps/WebClip/<key>.png           the same, in the iPhone install profile
//   native/boot/icon.webp, native/app-icon.png + app-icon.json   desktop / Android app (then `tauri icon`), only for
//                                                            the site with "nativeApps": true
//
// Two kinds of source ("iconStyle" in site.json):
//   mark  a transparent PNG of the mark alone, any shape (黄月短剧's moon). Trimmed to the picture itself; install
//         icons put it on black.
//   tile  a square app icon that fills its rounded square edge to edge (the country sites' icons). A white
//         background around the rounded corners (as in a JPEG) is made transparent. Install icons that can't be
//         transparent fill the corners from the picture's own edges.
// Scaled with Lanczos. The page images are lossless WebP: pixel for pixel what was drawn, and still smaller than PNG.
//
//   node scripts/brand-icons.mjs [--site <key>] [--quality 95] [--no-native]
//     --site KEY    only this site (default: every site in web/sites)
//     --quality N   lossy WebP at quality N (1-100) instead of lossless: smaller files, slightly softer
//     --no-native   skip the app icons (and `tauri icon`)
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sitesDir = join(repo, "web", "sites");
const args = process.argv.slice(2);
const opt = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : fallback; };
const quality = opt("quality") ? Number(opt("quality")) : null;
const native = !args.includes("--no-native");
if (quality !== null && !(quality >= 1 && quality <= 100)) throw new Error("--quality must be 1-100");
const only = opt("site");
const keys = readdirSync(sitesDir, { withFileTypes: true })
  .filter((d) => d.isDirectory() && existsSync(join(sitesDir, d.name, "site.json")))
  .map((d) => d.name)
  .filter((k) => !only || k === only);
if (only && keys.length === 0) throw new Error(`No site "${only}" in web/sites`);

// Small images: no cache and one thread keep memory use low, at no real cost in time.
sharp.cache(false);
sharp.concurrency(1);

const webp = quality === null ? { lossless: true, effort: 6 } : { quality, alphaQuality: 100, smartSubsample: true, effort: 6 };
const clear = { r: 0, g: 0, b: 0, alpha: 0 };
const black = { r: 0, g: 0, b: 0, alpha: 1 };

// ICO holding PNG images (every browser and Windows since Vista read these).
function ico(images) {
  const header = Buffer.alloc(6 + 16 * images.length);
  header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  images.forEach(({ size, png }, i) => {
    const e = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, e); header.writeUInt8(size >= 256 ? 0 : size, e + 1);
    header.writeUInt16LE(1, e + 4); header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(png.length, e + 8); header.writeUInt32LE(offset, e + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...images.map((x) => x.png)]);
}

/**
 * A tile icon with the white around its rounded corners made transparent: everything near-white that is connected to
 * a corner goes, then the edge is pulled in by 2 px (JPEG leaves a light fringe) and softened.
 */
async function cutCorners(path) {
  const { data, info } = await sharp(path).removeAlpha().raw().toBuffer({ resolveWithObject: true });
  const { width: w, height: h } = info;
  const outside = new Uint8Array(w * h);
  const stack = [0, w - 1, (h - 1) * w, h * w - 1];
  const whiteish = (p) => Math.min(data[p * 3], data[p * 3 + 1], data[p * 3 + 2]) > 200;
  while (stack.length) {
    const p = stack.pop();
    if (outside[p] || !whiteish(p)) continue;
    outside[p] = 1;
    const x = p % w, y = (p - x) / w;
    if (x > 0) stack.push(p - 1);
    if (x < w - 1) stack.push(p + 1);
    if (y > 0) stack.push(p - w);
    if (y < h - 1) stack.push(p + w);
  }
  const r = 2;
  const alpha = Buffer.alloc(w * h, 255);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      let out = false;
      for (let dy = -r; dy <= r && !out; dy++) {
        const yy = y + dy;
        if (yy < 0 || yy >= h) continue;
        for (let dx = -r; dx <= r; dx++) {
          const xx = x + dx;
          if (xx >= 0 && xx < w && outside[yy * w + xx]) { out = true; break; }
        }
      }
      if (out) alpha[y * w + x] = 0;
    }
  }
  const soft = await sharp(alpha, { raw: { width: w, height: h, channels: 1 } }).blur(0.8).extractChannel(0).raw().toBuffer();
  return sharp(data, { raw: { width: w, height: h, channels: 3 } }).joinChannel(soft, { raw: { width: w, height: h, channels: 1 } }).png().toBuffer();
}

async function buildSite(key) {
  const site = JSON.parse(readFileSync(join(sitesDir, key, "site.json"), "utf8"));
  const style = site.iconStyle ?? "mark";
  if (!["mark", "tile"].includes(style)) throw new Error(`${key}: iconStyle must be "mark" or "tile"`);
  const source = resolve(repo, site.icon);
  if (!existsSync(source)) throw new Error(`${key}: icon ${site.icon} not found`);
  const written = [];
  const save = (rel, data) => {
    const path = join(repo, rel);
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, data);
    written.push([rel, data.length]);
  };
  const out = `web/sites/${key}`;

  // The picture without its transparent margins.
  const input = style === "tile" ? await cutCorners(source) : source;
  const trimmed = await sharp(input).ensureAlpha().trim({ threshold: 1 }).png().toBuffer({ resolveWithObject: true });
  const { width: srcW, height: srcH } = trimmed.info;
  const aspect = srcW / srcH;
  const art = trimmed.data;
  const scaled = (o) => sharp(art).resize({ ...o, kernel: "lanczos3" });

  // Round artwork (a badge whose bounding-box corners are transparent) can fill far more of a circular crop than a
  // rectangle can: it only has to fit the circle, not have its corners inside it.
  const corners = await sharp(art).extractChannel("alpha").raw().toBuffer();
  const alphaAt = (fx, fy) => corners[Math.min(srcH - 1, Math.round(fy * (srcH - 1))) * srcW + Math.min(srcW - 1, Math.round(fx * (srcW - 1)))];
  const round = style === "mark" && [[0.08, 0.08], [0.92, 0.08], [0.08, 0.92], [0.92, 0.92]].every(([x, y]) => alphaAt(x, y) < 16);

  // The mark placed on a square: `fill` is the share of the side its longer edge takes.
  async function tile(size, fill, background = clear) {
    const box = Math.round(size * fill);
    const fit = aspect >= 1 ? { width: box } : { height: box };
    const { data, info } = await scaled(fit).png().toBuffer({ resolveWithObject: true });
    return sharp({ create: { width: size, height: size, channels: 4, background } })
      .composite([{ input: data, left: Math.round((size - info.width) / 2), top: Math.round((size - info.height) / 2) }])
      .png({ compressionLevel: 9, adaptiveFiltering: true }).toBuffer();
  }

  // A tile icon filling the whole square, its rounded corners filled from a blurred, enlarged copy of itself (for
  // icons that can't be transparent: the system rounds them itself).
  async function fullBleed(size) {
    const grow = Math.round(size * 1.3);
    const back = await scaled({ width: grow, height: grow, fit: "fill" }).blur(size / 18).flatten({ background: "#000" })
      .extract({ left: Math.round((grow - size) / 2), top: Math.round((grow - size) / 2), width: size, height: size }).png().toBuffer();
    const front = await scaled({ width: size, height: size, fit: "fill" }).png().toBuffer();
    return sharp(back).composite([{ input: front }]).flatten({ background: "#000" }).png({ compressionLevel: 9, adaptiveFiltering: true }).toBuffer();
  }

  // The colour around the tile's edge, for the maskable icon's background.
  async function edgeColour() {
    const { data, info } = await scaled({ width: 64, height: 64, fit: "fill" }).raw().toBuffer({ resolveWithObject: true });
    let r = 0, g = 0, b = 0, n = 0;
    for (let y = 0; y < info.height; y++) for (let x = 0; x < info.width; x++) {
      if (x > 6 && x < info.width - 7 && y > 6 && y < info.height - 7) continue;
      const i = (y * info.width + x) * info.channels;
      if (data[i + 3] < 250) continue;
      r += data[i]; g += data[i + 1]; b += data[i + 2]; n++;
    }
    return { r: Math.round(r / n), g: Math.round(g / n), b: Math.round(b / n), alpha: 1 };
  }

  // 1. The mark on pages: three heights; the browser picks by displayed size and pixel density.
  const files = [];
  for (const h of [64, 128, 256]) {
    const { data, info } = await scaled({ height: h }).webp(webp).toBuffer({ resolveWithObject: true });
    save(`${out}/mark/mark-${h}.webp`, data);
    files.push({ h, w: info.width });
  }
  save(`${out}/mark/mark.ts`, Buffer.from(
    `// Generated by scripts/brand-icons.mjs from ${site.icon}; don't edit, run \`pnpm brand:icons\`.\n` +
    `export const markAspect = ${(files[2].w / files[2].h).toFixed(4)}; // width / height\n` +
    `export const markFiles = [\n${files.map((f) => `  { url: new URL("./mark-${f.h}.webp", import.meta.url).href, width: ${f.w}, height: ${f.h} },`).join("\n")}\n];\n`));

  // 2. Browser and install icons.
  save(`${out}/public/favicon.ico`, ico(await Promise.all([16, 32, 48].map(async (size) => ({ size, png: await tile(size, 1) })))));
  const anyFill = style === "tile" ? 1 : 0.94;
  save(`${out}/public/icons/icon-192.png`, await tile(192, anyFill));
  save(`${out}/public/icons/icon-512.png`, await tile(512, anyFill));
  // Maskable: Android may cut the icon to a circle of 80% of the side, so what matters stays inside it.
  const safe = round ? 0.8 : 0.8 / Math.sqrt(1 + (aspect >= 1 ? 1 / aspect ** 2 : aspect ** 2));
  save(`${out}/public/icons/maskable-512.png`, style === "tile"
    ? await tile(512, 0.76, await edgeColour())
    : await tile(512, Math.min(0.9, safe * 0.96), black));
  const appleTouch = style === "tile" ? await fullBleed(180) : await tile(180, round ? 0.88 : 0.8, black);
  save(`${out}/public/icons/apple-touch-icon.png`, appleTouch);
  // The API serves the same icon in the iPhone install profile (/app/download/ios).
  save(`backend/src/ReelGuo.Api/Apps/WebClip/${key}.png`, appleTouch);

  // 3. The desktop / Android app: start page and the source for `tauri icon` (which writes native/src-tauri/icons).
  const appIcons = native && site.nativeApps === true;
  if (appIcons) {
    save("native/boot/icon.webp", await scaled({ height: 192 }).webp(webp).toBuffer());
    save("native/app-icon.png", await tile(1024, 0.86));
    save("native/app-icon.json", Buffer.from(JSON.stringify({ default: "app-icon.png", bg_color: "#000000", android_fg_scale: 85 }, null, 2) + "\n"));
  }

  console.log(`${key}: ${site.icon} (${srcW} x ${srcH} after trimming, ${style}${round ? ", round" : ""}, ${quality === null ? "lossless" : `quality ${quality}`} WebP)`);
  if (appIcons && Math.max(srcW, srcH) < 880) {
    console.log(`  note: the app icon is drawn at 880 px but the picture is ${Math.max(srcW, srcH)} px, so the largest app icons are enlarged (a 1024 px source is sharper)`);
  }
  for (const [rel, bytes] of written) console.log(`  ${rel.padEnd(56)} ${(bytes / 1024).toFixed(1).padStart(7)} KB`);
  return appIcons;
}

let tauri = false;
for (const key of keys) tauri = (await buildSite(key)) || tauri;

if (tauri) {
  console.log("app icons: pnpm tauri icon app-icon.json (in native/) ...");
  const r = spawnSync("pnpm tauri icon app-icon.json", { cwd: join(repo, "native"), stdio: "inherit", shell: true });
  if (r.status !== 0) { console.error("tauri icon failed; run it in native/ by hand: pnpm tauri icon app-icon.json"); process.exit(1); }
}
