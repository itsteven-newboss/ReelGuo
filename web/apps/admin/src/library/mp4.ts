/**
 * What an MP4's own boxes say about it, read from a few kilobytes and never the whole file: a library listing reads
 * thousands of these. Nothing is decoded. (The same reader the Viora platform's delivery page uses.)
 *
 * - `complete`: the top-level boxes add up to exactly the file's size. A file still being written stops short,
 *   whatever the downloader calls it.
 * - `faststart`: the index (`moov`) comes before the media (`mdat`). COS does not re-encode, so without this a
 *   player cannot start until the whole file has arrived.
 * - Duration, picture size and codecs come from `mvhd`, `tkhd` and the first sample entry of `stsd`.
 */
export type Mp4Header = {
  complete: boolean;
  faststart: boolean;
  durationSec: number;
  width: number;
  height: number;
  videoCodec: string | null;
  audioCodec: string | null;
};

type Box = { type: string; start: number; size: number; header: number };

async function bytes(file: Blob, start: number, length: number): Promise<DataView> {
  const buf = await file.slice(start, Math.min(file.size, start + length)).arrayBuffer();
  return new DataView(buf);
}

const fourcc = (v: DataView, at: number) =>
  String.fromCharCode(v.getUint8(at), v.getUint8(at + 1), v.getUint8(at + 2), v.getUint8(at + 3));

/** The boxes laid end to end between two offsets: 8-byte headers, a 64-bit size when the short one says 1. */
async function boxesOf(file: Blob, start: number, end: number, limit = 64): Promise<{ boxes: Box[]; reachedEnd: boolean }> {
  const boxes: Box[] = [];
  let at = start;
  while (at + 8 <= end && boxes.length < limit) {
    const h = await bytes(file, at, 16);
    if (h.byteLength < 8) break;
    let size = h.getUint32(0);
    let header = 8;
    if (size === 1) {
      if (h.byteLength < 16) break;
      size = Number(h.getBigUint64(8));
      header = 16;
    } else if (size === 0) size = end - at;
    if (size < header) break;
    boxes.push({ type: fourcc(h, 4), start: at, size, header });
    at += size;
  }
  return { boxes, reachedEnd: at === end };
}

/** Reads one box's body whole: only ever used inside `moov`, which is small. */
const body = (file: Blob, b: Box) => bytes(file, b.start + b.header, b.size - b.header);

export async function readMp4Header(file: Blob): Promise<Mp4Header> {
  const result: Mp4Header = { complete: false, faststart: false, durationSec: 0, width: 0, height: 0, videoCodec: null, audioCodec: null };
  const top = await boxesOf(file, 0, file.size);
  const order = top.boxes.map((b) => b.type);
  result.complete = top.reachedEnd && order.includes("moov") && order.includes("mdat");
  result.faststart = order.indexOf("moov") > -1 && order.indexOf("moov") < order.indexOf("mdat");

  const moov = top.boxes.find((b) => b.type === "moov");
  if (!moov || moov.size > 64 * 1024 * 1024) return result;
  const inner = await boxesOf(file, moov.start + moov.header, moov.start + moov.size, 256);
  for (const b of inner.boxes) {
    if (b.type === "mvhd") {
      const v = await body(file, b);
      const version = v.getUint8(0);
      const scale = version ? v.getUint32(20) : v.getUint32(12);
      const duration = version ? Number(v.getBigUint64(24)) : v.getUint32(16);
      if (scale > 0) result.durationSec = Math.round(duration / scale);
    }
    if (b.type !== "trak") continue;
    const kids = (await boxesOf(file, b.start + b.header, b.start + b.size)).boxes;
    const tkhd = kids.find((k) => k.type === "tkhd");
    let width = 0;
    let height = 0;
    if (tkhd) {
      const v = await body(file, tkhd);
      width = v.getUint32(v.byteLength - 8) >>> 16;
      height = v.getUint32(v.byteLength - 4) >>> 16;
    }
    // mdia > minf > stbl > stsd: the first sample entry's type is the codec.
    let node: Box | undefined = kids.find((k) => k.type === "mdia");
    for (const name of ["minf", "stbl", "stsd"]) {
      if (!node) break;
      const parent: Box = node;
      node = (await boxesOf(file, parent.start + parent.header, parent.start + parent.size)).boxes.find((k) => k.type === name);
    }
    let codec: string | null = null;
    if (node) {
      const v = await bytes(file, node.start + node.header + 8 + 4, 4);
      if (v.byteLength === 4) codec = fourcc(v, 0);
    }
    if (width && height) {
      result.width = width;
      result.height = height;
      result.videoCodec = codec;
    } else if (codec) result.audioCodec = codec;
  }
  return result;
}
