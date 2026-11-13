import { useEffect } from "react";

/**
 * Keeps the screen on while `on` (a video is playing on screen). The browser drops the lock when the page is hidden,
 * so it is taken again when the page comes back. Unsupported browsers simply let the screen sleep.
 */
export function useWakeLock(on: boolean) {
  useEffect(() => {
    if (!on || typeof navigator === "undefined" || !("wakeLock" in navigator)) return undefined;
    let lock: WakeLockSentinel | null = null;
    let cancelled = false;
    const acquire = async () => {
      if (document.visibilityState !== "visible" || (lock && !lock.released)) return;
      try {
        const next = await navigator.wakeLock.request("screen");
        if (cancelled) void next.release().catch(() => undefined);
        else lock = next;
      } catch {
        /* denied (e.g. battery saver) */
      }
    };
    void acquire();
    const onVisible = () => void acquire();
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      cancelled = true;
      document.removeEventListener("visibilitychange", onVisible);
      void lock?.release().catch(() => undefined);
    };
  }, [on]);
}
