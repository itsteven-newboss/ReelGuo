/** Where to go back to after a purchase (the locked episode that sent the viewer to the store). */
export type ResumeTarget = { id: number; ep: number; title: string };

/**
 * Transient layers (sheets, dialogs, the player's fullscreen) that the back button closes before leaving the page.
 * Opening one adds a history entry with the same URL; closing it from the UI goes through history.back(), so the
 * browser history and the UI never drift apart.
 */
const overlays: (() => void)[] = [];

export function openOverlay(close: () => void) {
  overlays.push(close);
  window.history.pushState({ rgOverlay: overlays.length }, "");
}

export function closeOverlay() {
  if (overlays.length) window.history.back();
}

/** Navigating to another page closes every open layer at once (their history entries become plain steps back). */
export function clearOverlays() {
  while (overlays.length) overlays.pop()!();
}

/** Back button: closes the top layer if there is one; page changes are handled by the router. */
export function installBackHandler() {
  const onPop = () => {
    const close = overlays.pop();
    if (close) close();
  };
  window.addEventListener("popstate", onPop);
  return () => window.removeEventListener("popstate", onPop);
}
