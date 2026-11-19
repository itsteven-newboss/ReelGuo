import type { Schemas } from "@reelguo/api-client";
import { readMp4Header, type Mp4Header } from "./mp4";

/**
 * The delivery folder on the admin's PC, as the browser can see it. Files are classified by what they are, not where
 * they sit: `.json` is a details file, an image is a cover, an `.mp4` is an episode of the folder it is in, and a
 * downloader's placeholder (`….mp4.qkdownloading`, `.part`, …) marks an episode still on its way. So the whole
 * delivery folder (VideoDetails/, Images/, Videos/<title>/<title>_<n>.mp4) can be picked, or one title's folder.
 *
 * Nothing here decides anything about the catalogue: it builds the listing the server judges, and keeps the File
 * objects so the runner can send the ones the server asks for.
 */

export type Listing = Schemas["LibraryListing"];
export type TitlePlan = Schemas["LibraryTitlePlan"];
type VideoFile = Schemas["LibraryVideoFile"];

/** One file found, wherever it came from (a directory handle, or a folder picked with the plain file input). */
export type LocalEntry = { name: string; parent: string; grandparent: string; depth: number; get: () => Promise<File> };

/**
 * Folders named for what they hold rather than for a title. Deliveries also come as `<title>/video/<title>_01.mp4`
 * with `<title>/Images/<any name>.jpg`: there the title is the file name's prefix (or the folder above), and a
 * picture belongs to the title folder above it.
 */
const GENERIC_VIDEO = /^(videos?|video ?files|视频|视频文件|mp4|episodes?|剧集|正片)$/i;
const GENERIC_IMAGE = /^(images?|imgs?|pics?|pictures?|covers?|posters?|封面|图片|海报|剧照)$/i;
const EPISODE = /_(\d{1,4})(?:-[0-9a-f]{6,})?\.mp4$/i;

/** The title folder an episode file belongs to. */
function titleFolderOf(e: LocalEntry) {
  if (!GENERIC_VIDEO.test(e.parent.trim())) return e.parent;
  const prefix = EPISODE.test(e.name) ? e.name.replace(EPISODE, "").trim() : "";
  if (prefix) return prefix;
  return e.grandparent && !GENERIC_VIDEO.test(e.grandparent.trim()) ? e.grandparent : e.parent;
}

/** The folder above a picture in an `Images`-like folder: the title it may belong to. */
function imageOwnerOf(e: LocalEntry) {
  return GENERIC_IMAGE.test(e.parent.trim()) && e.grandparent && !GENERIC_IMAGE.test(e.grandparent.trim()) ? e.grandparent : null;
}

/** The server's title key (CosKeys.NameKey): NFKC, lower case, no leading "(Dubbed)", only letters and digits. */
const nameKey = (s: string) =>
  s.normalize("NFKC").trim().replace(/^\(\s*dubbed\s*\)\s*/i, "").toLowerCase().replace(/[^\p{L}\p{Nd}]/gu, "");
const stem = (name: string) => name.replace(/\.[^.]+$/, "");

export type FolderIndex = {
  listing: Listing;
  /** `folder \n file name` → file */
  videos: Map<string, File>;
  /** image file name → file */
  images: Map<string, File>;
  files: number;
};

const IMAGE = /\.(jpe?g|png|webp)$/i;
const PLACEHOLDER = /\.mp4\.[a-z0-9]+$|\.(part|crdownload|qkdownloading|tmp|download)$/i;

export const videoKey = (folder: string, name: string) => `${folder}\n${name}`;

/** Every file under a directory the admin picked, three levels down (`<delivery>/Videos/<title>/…`). */
export async function* walkHandle(dir: FileSystemDirectoryHandle, depth = 0, parent = dir.name, grandparent = ""): AsyncGenerator<LocalEntry> {
  if (depth > 4) return;
  for await (const entry of (dir as unknown as { values(): AsyncIterable<FileSystemHandle> }).values()) {
    if (entry.kind === "directory") yield* walkHandle(entry as FileSystemDirectoryHandle, depth + 1, entry.name, parent);
    else yield { name: entry.name, parent, grandparent, depth, get: () => (entry as FileSystemFileHandle).getFile() };
  }
}

/** The same, from a folder picked with `<input webkitdirectory>` or dropped where handles are not offered. */
export function* walkFileList(files: Iterable<File>): Generator<LocalEntry> {
  for (const file of files) {
    const parts = (file.webkitRelativePath || file.name).split("/");
    yield {
      name: file.name,
      parent: parts.length > 1 ? parts[parts.length - 2]! : "",
      grandparent: parts.length > 2 ? parts[parts.length - 3]! : "",
      depth: parts.length - 1,
      get: () => Promise.resolve(file),
    };
  }
}

/** Header readings by file identity, so a re-listing every few minutes reads nothing it has read before. */
const probes = new Map<string, Mp4Header>();
const sizes = new Map<string, { width: number; height: number }>();
const identity = (f: File, folder = "") => `${folder}\n${f.name}\n${f.size}\n${f.lastModified}`;

async function imageSize(file: File) {
  const id = identity(file);
  const known = sizes.get(id);
  if (known) return known;
  try {
    const bmp = await createImageBitmap(file);
    const size = { width: bmp.width, height: bmp.height };
    bmp.close();
    sizes.set(id, size);
    return size;
  } catch {
    return { width: 0, height: 0 };
  }
}

async function probe(file: File, folder: string) {
  const id = identity(file, folder);
  const known = probes.get(id);
  if (known) return known;
  const header = await readMp4Header(file);
  // A file still being written must be read again next time, so only a finished one is remembered.
  if (header.complete) probes.set(id, header);
  return header;
}

/** Runs `work` over `items` a few at a time: thousands of small reads go faster in parallel, not all at once. */
async function inBatches<T>(items: T[], size: number, work: (item: T) => Promise<void>, onProgress?: (done: number) => void) {
  for (let i = 0; i < items.length; i += size) {
    await Promise.all(items.slice(i, i + size).map(work));
    onProgress?.(Math.min(items.length, i + size));
  }
}

export async function buildIndex(entries: AsyncIterable<LocalEntry> | Iterable<LocalEntry>, onProgress?: (done: number, total: number) => void): Promise<FolderIndex> {
  const all: LocalEntry[] = [];
  for await (const e of entries) all.push(e);

  const details: Schemas["LibraryDetails"][] = [];
  const images: Schemas["LibraryImage"][] = [];
  const folders = new Map<string, { files: VideoFile[]; downloading: string[] }>();
  const videoFiles = new Map<string, File>();
  const imageFiles = new Map<string, File>();
  const folderOf = (name: string) => {
    let f = folders.get(name);
    if (!f) folders.set(name, (f = { files: [], downloading: [] }));
    return f;
  };

  // Pictures in an `Images`-like folder are settled once the titles are known (below).
  const inImageFolders: { owner: string; name: string; file: File; width: number; height: number }[] = [];

  await inBatches(all, 24, async (e) => {
    const lower = e.name.toLowerCase();
    if (PLACEHOLDER.test(lower)) {
      // A placeholder ("x.mp4.part") names the episode file it will become.
      if (e.depth > 0) folderOf(titleFolderOf({ ...e, name: e.name.replace(/\.[a-z0-9]+$/i, "") })).downloading.push(e.name);
      return;
    }
    if (lower.endsWith(".json")) {
      const file = await e.get();
      details.push({ name: e.name, text: file.size <= 65536 ? await file.text() : null });
    } else if (IMAGE.test(lower)) {
      const file = await e.get();
      const { width, height } = await imageSize(file);
      const owner = imageOwnerOf(e);
      if (owner) {
        inImageFolders.push({ owner, name: e.name, file, width, height });
        return;
      }
      images.push({ name: e.name, bytes: file.size, width, height });
      imageFiles.set(e.name, file);
    } else if (lower.endsWith(".mp4") && e.depth > 0) {
      const file = await e.get();
      const folder = titleFolderOf(e);
      const h = await probe(file, folder);
      folderOf(folder).files.push({
        name: e.name, bytes: file.size, duration_sec: h.durationSec, width: h.width, height: h.height,
        complete: h.complete, faststart: h.faststart, video_codec: h.videoCodec, audio_codec: h.audioCodec,
      });
      videoFiles.set(videoKey(folder, e.name), file);
    }
  }, (done) => onProgress?.(done, all.length));

  // A picture named after a title keeps its name (Images/<title>.jpg). Otherwise, when the folder above `Images` is
  // a title (<title>/Images/9月14日-封面.jpg), the tallest such picture is that title's cover, listed under its name.
  const titleKeys = new Set([...folders.keys()].map(nameKey));
  const owned = new Map<string, (typeof inImageFolders)[number]>();
  const ratio = (w: number, h: number) => (w > 0 ? h / w : 0);
  for (const img of inImageFolders) {
    if (!titleKeys.has(nameKey(stem(img.name))) && titleKeys.has(nameKey(img.owner))) {
      const best = owned.get(img.owner);
      if (!best || ratio(img.width, img.height) > ratio(best.width, best.height)
        || (ratio(img.width, img.height) === ratio(best.width, best.height) && img.height > best.height)) owned.set(img.owner, img);
      continue;
    }
    if (imageFiles.has(img.name)) continue;
    images.push({ name: img.name, bytes: img.file.size, width: img.width, height: img.height });
    imageFiles.set(img.name, img.file);
  }
  for (const [owner, img] of owned) {
    const name = owner + img.name.slice(img.name.lastIndexOf(".")).toLowerCase();
    if (imageFiles.has(name)) continue; // a picture already named after the title wins
    images.push({ name, bytes: img.file.size, width: img.width, height: img.height });
    imageFiles.set(name, img.file);
  }

  return {
    listing: {
      details,
      images,
      folders: [...folders].map(([name, f]) => ({ name, files: f.files, downloading: f.downloading })),
    },
    videos: videoFiles,
    images: imageFiles,
    files: all.length,
  };
}

/** Just one title's pieces, for creating its series. */
export function listingFor(index: FolderIndex, title: Pick<TitlePlan, "folder_name" | "image_name" | "details_name">): Listing {
  return {
    details: (index.listing.details ?? []).filter((d) => d.name === title.details_name),
    images: (index.listing.images ?? []).filter((i) => i.name === title.image_name),
    folders: (index.listing.folders ?? []).filter((f) => f.name === title.folder_name),
  };
}

// ---------- the remembered folder (Edge and Chrome) ----------

const DB = "reelguo-library";
function store(mode: IDBTransactionMode): Promise<IDBObjectStore> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(DB, 1);
    open.onupgradeneeded = () => open.result.createObjectStore("folders");
    open.onsuccess = () => resolve(open.result.transaction("folders", mode).objectStore("folders"));
    open.onerror = () => reject(open.error);
  });
}

async function recall<T>(key: string): Promise<T | null> {
  try {
    const s = await store("readonly");
    return await new Promise((resolve) => {
      const req = s.get(key);
      req.onsuccess = () => resolve((req.result as T | undefined) ?? null);
      req.onerror = () => resolve(null);
    });
  } catch {
    return null;
  }
}

async function keep(key: string, value: unknown) {
  try {
    (await store("readwrite")).put(value, key);
  } catch {
    /* a private window or blocked storage: the folder is simply picked again next time */
  }
}

/** The folder handle picked last time, if the browser can keep one. Never throws. */
export const rememberedFolder = () => recall<FileSystemDirectoryHandle>("last");
export const rememberFolder = (handle: FileSystemDirectoryHandle) => keep("last", handle);

/**
 * 优化视频's folders, kept so that after a reload, a closed tab or a sign-out the admin continues with one click
 * instead of picking the folders again (and maybe a new, empty output folder, which would redo every episode).
 */
export type OptimizeMemory = { source: FileSystemDirectoryHandle; target: FileSystemDirectoryHandle; unfinished: boolean };
export const rememberedOptimize = () => recall<OptimizeMemory>("optimize");
export const rememberOptimize = (memory: OptimizeMemory) => keep("optimize", memory);

type AccessMode = "read" | "readwrite";
type PermissionHandle = FileSystemDirectoryHandle & {
  queryPermission?: (d: { mode: AccessMode }) => Promise<PermissionState>;
  requestPermission?: (d: { mode: AccessMode }) => Promise<PermissionState>;
};

/** Access to a remembered folder: asks again where the browser wants a click (usually once per visit). */
export async function canAccess(handle: FileSystemDirectoryHandle, ask: boolean, mode: AccessMode = "read") {
  const h = handle as PermissionHandle;
  if (!h.queryPermission) return true;
  if ((await h.queryPermission({ mode })) === "granted") return true;
  return ask && h.requestPermission ? (await h.requestPermission({ mode })) === "granted" : false;
}

export const canRead = (handle: FileSystemDirectoryHandle, ask: boolean) => canAccess(handle, ask, "read");

export const canPickFolders = () => typeof (window as unknown as { showDirectoryPicker?: unknown }).showDirectoryPicker === "function";

/** The browser's folder picker. Cancelling is "cancelled"; a refusal is "failed", so the page falls back to the plain input. */
export async function pickFolder(): Promise<FileSystemDirectoryHandle | "cancelled" | "failed"> {
  try {
    return await (window as unknown as { showDirectoryPicker: (o: object) => Promise<FileSystemDirectoryHandle> })
      .showDirectoryPicker({ mode: "read" });
  } catch (e) {
    return e instanceof DOMException && e.name === "AbortError" ? "cancelled" : "failed";
  }
}

/** The episode number in "Title_12.mp4" (the server parses it too; this only finds the file it asks for). */
export function episodeNumber(name: string) {
  const m = /_(\d{1,4})(?:-[0-9a-f]{6,})?\.mp4/i.exec(name);
  return m ? Number(m[1]) : null;
}
