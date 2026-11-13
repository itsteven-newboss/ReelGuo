import type { Schemas } from "@reelguo/api-client";

export type UploadTicket = Schemas["UploadTicket"];

/**
 * Sends a file to wherever the API's ticket points: straight to object storage (presigned URL)
 * in production, or to the API's own upload endpoint in development. Uses XHR for progress events.
 */
export function uploadFile(ticket: UploadTicket, file: File, onProgress: (fraction: number) => void, signal?: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open(ticket.method, ticket.url);
    for (const [name, value] of Object.entries(ticket.headers)) xhr.setRequestHeader(name, value);
    // Same-origin API uploads need the session cookie; presigned storage URLs must not get it.
    xhr.withCredentials = ticket.url.startsWith("/");
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) onProgress(e.loaded / e.total);
    };
    xhr.onload = () => (xhr.status >= 200 && xhr.status < 300 ? resolve() : reject(new Error(`上传失败（HTTP ${xhr.status}）`)));
    xhr.onerror = () => reject(new Error("上传失败：网络错误"));
    xhr.onabort = () => reject(new Error("上传已取消"));
    signal?.addEventListener("abort", () => xhr.abort());
    xhr.send(file);
  });
}

export function formatBytes(n: number) {
  if (n >= 1 << 30) return `${(n / (1 << 30)).toFixed(1)} GB`;
  if (n >= 1 << 20) return `${(n / (1 << 20)).toFixed(1)} MB`;
  return `${Math.max(1, Math.round(n / 1024))} KB`;
}

export function formatDuration(seconds: number) {
  const s = Math.round(seconds);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}
