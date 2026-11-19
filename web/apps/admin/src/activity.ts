import { useEffect, useRef } from "react";
import { api } from "./session";

const EVERY_MS = 30_000;

/**
 * Tells the server that long work is running in this tab (优化视频 or 文件夹上传), every 30 s while `active`, and that
 * it stopped when it ends. scripts/staging/update.ps1 waits while any is reported, so an update never cuts it off.
 * Failures are ignored: the work itself doesn't depend on the server answering.
 */
export function useActivityHeartbeat(kind: "optimize" | "upload", active: boolean, detail: string) {
  const latest = useRef(detail);
  latest.current = detail;
  useEffect(() => {
    if (!active) return undefined;
    const send = (on: boolean) => void api.POST("/api/admin/activity", { body: { kind, detail: latest.current, active: on } }).catch(() => undefined);
    send(true);
    const timer = window.setInterval(() => send(true), EVERY_MS);
    return () => { window.clearInterval(timer); send(false); };
  }, [kind, active]);
}
