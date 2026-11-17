import { ALL_FORMATS, BlobSource, BufferTarget, canEncodeVideo, Conversion, Input, Mp4OutputFormat, Output, Quality } from "mediabunny";
import { buildIndex, episodeNumber, videoKey, walkHandle, type FolderIndex } from "./scan";

/**
 * Optimises a delivery folder in the browser before upload (Edge / Chrome): every episode becomes an MP4 that plays
 * well from COS (H.264 + AAC, index at the front, about 1.5-2 Mbit/s at 1080×1920), written to another folder in the
 * layout the folder upload expects: Videos/<title>/<title>_<n>.mp4, Images/<title>.jpg, VideoDetails/*.json.
 *
 * Encoding uses WebCodecs, so the PC's graphics hardware does the work where it can. Episodes that already qualify
 * are copied unchanged. Titles, episode numbers and covers come from the same scanner the upload uses, so what the
 * upload would see in the source is what ends up in the output. Same rules as scripts/prepare-videos.ps1.
 */

/** Above this, an episode is re-encoded even if it is otherwise playable: it would stall on phones. */
const MAX_MBPS = 8;
/** Longest allowed short side; larger videos are scaled down. */
const MAX_SHORT_SIDE = 1080;
const KEYFRAME_SECONDS = 2;
/** H.264 quantizer: lower is better and larger. 26 matches the PowerShell script's CRF 23 in quality and size. */
const QUANTIZER = 26;

export type OptimizeJob = {
  title: string;
  number: number;
  /** The file name, kept as delivered (it is the COS object name later). */
  name: string;
  file: File;
  bytes: number;
  durationSec: number;
  action: "convert" | "copy";
  /** Why it is converted, in admin language. */
  reason: string;
};

export type OptimizePlan = {
  index: FolderIndex;
  jobs: OptimizeJob[];
  /** Files that can't be handled yet: no episode number in the name, still downloading, not a complete MP4. */
  skipped: { title: string; name: string; reason: string }[];
  titles: number;
};

/** Whether this browser can do the work: folder access with writing, and WebCodecs H.264 encoding. */
export async function optimizerSupport(): Promise<string | null> {
  if (typeof (window as unknown as { showDirectoryPicker?: unknown }).showDirectoryPicker !== "function") {
    return "此浏览器不能读写本机文件夹。请使用电脑版 Edge 或 Chrome。";
  }
  if (typeof (window as unknown as { VideoEncoder?: unknown }).VideoEncoder !== "function") {
    return "此浏览器不支持视频编码（WebCodecs）。请使用最新版电脑版 Edge 或 Chrome。";
  }
  const ok = await canEncodeVideo("avc", { width: 1080, height: 1920, quality: new Quality({ bitrate: targetBitrate(1080, 1920) }) }).catch(() => false);
  return ok ? null : "此浏览器无法编码 H.264 视频。请更新 Edge / Chrome，或在电脑上运行 scripts\\prepare-videos.ps1。";
}

/** Reads the source folder and decides, per episode, whether to convert or copy it. */
export async function planOptimize(source: FileSystemDirectoryHandle, onProgress?: (done: number, total: number) => void): Promise<OptimizePlan> {
  const index = await buildIndex(walkHandle(source), onProgress);
  const jobs: OptimizeJob[] = [];
  const skipped: OptimizePlan["skipped"] = [];
  for (const folder of index.listing.folders ?? []) {
    for (const name of folder.downloading ?? []) skipped.push({ title: folder.name, name, reason: "还在下载" });
    for (const v of folder.files ?? []) {
      const number = episodeNumber(v.name);
      if (number === null) { skipped.push({ title: folder.name, name: v.name, reason: "文件名里没有集数（应为 名称_集数.mp4）" }); continue; }
      if (!v.complete) { skipped.push({ title: folder.name, name: v.name, reason: "文件不完整（可能还在下载）" }); continue; }
      const file = index.videos.get(videoKey(folder.name, v.name));
      if (!file) continue;
      const reasons: string[] = [];
      if (!v.faststart) reasons.push("索引在文件末尾");
      if (!v.video_codec || !/^avc[13]$/i.test(v.video_codec)) reasons.push(`视频编码 ${v.video_codec ?? "未知"}`);
      if (v.audio_codec && v.audio_codec.toLowerCase() !== "mp4a") reasons.push(`音频编码 ${v.audio_codec}`);
      const mbps = v.duration_sec > 0 ? (v.bytes * 8) / v.duration_sec / 1_000_000 : 0;
      if (mbps > MAX_MBPS) reasons.push(`码率 ${mbps.toFixed(1)} Mbit/s`);
      if (Math.min(v.width, v.height) > MAX_SHORT_SIDE) reasons.push(`分辨率 ${v.width}×${v.height}`);
      jobs.push({
        title: folder.name, number, name: v.name, file, bytes: v.bytes, durationSec: v.duration_sec,
        action: reasons.length ? "convert" : "copy", reason: reasons.length ? reasons.join("，") : "已符合要求，直接复制",
      });
    }
  }
  jobs.sort((a, b) => a.title.localeCompare(b.title, "zh-CN") || a.number - b.number);
  return { index, jobs, skipped, titles: new Set(jobs.map((j) => j.title)).size };
}

/** Where a job lands in the output folder. */
export const outputPath = (job: Pick<OptimizeJob, "title" | "name">) => ["Videos", job.title, job.name];

/** The fallback bitrate target: 2 Mbit/s at 1080×1920, scaled with the picture area. */
function targetBitrate(width: number, height: number) {
  const scaled = (2_000_000 * width * height) / (1080 * 1920);
  return Math.round(Math.min(4_000_000, Math.max(800_000, scaled)) / 100_000) * 100_000;
}

const even = (n: number) => Math.max(2, Math.round(n / 2) * 2);

const DISCARD_REASON: Record<string, string> = {
  undecodable_source_codec: "此浏览器无法解码该编码",
  unknown_source_codec: "无法识别的编码",
  no_encodable_target_codec: "此浏览器无法编码为 H.264 / AAC",
};

/**
 * Converts one episode. Returns the finished MP4 (index at the front, so it is assembled in memory: about 80 MB for
 * a 6-minute episode). `onProgress` gets 0..1 and the seconds of video processed.
 */
export async function convertEpisode(file: File, onProgress: (progress: number, seconds: number) => void, signal: AbortSignal): Promise<ArrayBuffer> {
  const input = new Input({ formats: ALL_FORMATS, source: new BlobSource(file) });
  try {
    const video = await input.getPrimaryVideoTrack();
    if (!video) throw new Error("文件里没有视频画面");
    if (!(await video.canDecode())) throw new Error(`此浏览器无法解码 ${video.codec ?? "该"} 视频，请用 scripts\\prepare-videos.ps1 转换`);
    const w = video.displayWidth;
    const h = video.displayHeight;
    const scale = Math.min(1, MAX_SHORT_SIDE / Math.min(w, h));
    const size = scale < 1 ? { width: even(w * scale), height: even(h * scale) } : {};
    const output = new Output({ format: new Mp4OutputFormat({ fastStart: "in-memory" }), target: new BufferTarget() });
    const conversion = await Conversion.init({
      input,
      output,
      tracks: "primary",
      video: {
        codec: "avc",
        ...size,
        fit: "contain",
        // Constant quality (quantizer, like ffmpeg's CRF) where the encoder supports it, else a bitrate target.
        // Measured on a real 16 Mbit/s 1080×1920 episode in Edge: quantizer 26 gave 1.6 Mbit/s at SSIM 0.989; the
        // 2 Mbit/s target gave 2.8 Mbit/s at the same quality (the encoder overshoots its target).
        quality: new Quality({ quantizer: QUANTIZER, bitrate: targetBitrate(size.width ?? w, size.height ?? h), bitrateMode: "variable" }),
        keyFrameInterval: KEYFRAME_SECONDS,
        forceTranscode: true,
      },
      // AAC is copied as it is; anything else becomes AAC.
      audio: { codec: "aac", quality: new Quality({ bitrate: 128_000 }) },
    });
    const lost = conversion.discardedTracks.filter((d) => d.reason !== "max_track_count_of_type_reached" && d.reason !== "max_track_count_reached");
    if (!conversion.isValid || lost.length > 0) {
      const why = lost.map((d) => `${d.track.type === "audio" ? "音频" : "视频"}：${DISCARD_REASON[d.reason] ?? d.reason}`).join("；");
      throw new Error(`无法转换（${why || "不支持的文件"}），请用 scripts\\prepare-videos.ps1 转换`);
    }
    conversion.onProgress = onProgress;
    const cancel = () => void conversion.cancel();
    signal.addEventListener("abort", cancel, { once: true });
    try {
      await conversion.execute();
    } finally {
      signal.removeEventListener("abort", cancel);
    }
    const buffer = (output.target as BufferTarget).buffer;
    if (!buffer) throw new Error("转换没有产生文件");
    return buffer;
  } finally {
    input.dispose();
  }
}

// ---------- the output folder ----------

async function directoryAt(root: FileSystemDirectoryHandle, parts: string[], create: boolean) {
  let dir = root;
  for (const part of parts) dir = await dir.getDirectoryHandle(part, { create });
  return dir;
}

/** Size of a finished file in the output folder, or null when it isn't there. */
export async function existingSize(root: FileSystemDirectoryHandle, parts: string[]): Promise<number | null> {
  try {
    const dir = await directoryAt(root, parts.slice(0, -1), false);
    const file = await (await dir.getFileHandle(parts[parts.length - 1]!)).getFile();
    return file.size;
  } catch {
    return null;
  }
}

/**
 * Writes a file. The browser writes to a temporary "<name>.crswap" and swaps it in when closed, so a half-written
 * episode is never seen under its real name (the upload page treats the temporary file as still downloading).
 */
export async function writeFileAt(root: FileSystemDirectoryHandle, parts: string[], data: Blob | ArrayBuffer) {
  const dir = await directoryAt(root, parts.slice(0, -1), true);
  const handle = await dir.getFileHandle(parts[parts.length - 1]!, { create: true });
  const writable = await handle.createWritable();
  try {
    await writable.write(data);
    await writable.close();
  } catch (e) {
    await writable.abort().catch(() => undefined);
    throw e;
  }
}

/** Covers and details files go with the videos, under their usual folders. */
export async function copyExtras(plan: OptimizePlan, root: FileSystemDirectoryHandle) {
  for (const image of plan.index.listing.images ?? []) {
    const file = plan.index.images.get(image.name);
    if (file && (await existingSize(root, ["Images", image.name])) !== file.size) await writeFileAt(root, ["Images", image.name], file);
  }
  for (const d of plan.index.listing.details ?? []) {
    if (d.text !== null && d.text !== undefined) await writeFileAt(root, ["VideoDetails", d.name], new Blob([d.text], { type: "application/json" }));
  }
}

/** The output must be a separate folder: not the source, not inside it, and not holding it. */
export async function checkFolders(source: FileSystemDirectoryHandle, target: FileSystemDirectoryHandle): Promise<string | null> {
  if (await source.isSameEntry(target)) return "输出文件夹不能和原始文件夹相同，请新建一个空文件夹";
  if (await source.resolve(target)) return "输出文件夹不能在原始文件夹里面，请选择别处的文件夹";
  if (await target.resolve(source)) return "输出文件夹不能包含原始文件夹，请新建一个空文件夹";
  return null;
}
