/**
 * Warms the browser cache for the next item so its first frame appears almost instantly:
 * master playlist -> a low variant playlist -> its init segment and first media segment.
 * Media responses are cacheable (segments immutable, playlists 60 s), so hls.js reuses them.
 */
const warmed = new Set<string>();

export async function prefetchStart(masterUrl: string, signal?: AbortSignal) {
  if (warmed.has(masterUrl)) return;
  warmed.add(masterUrl);
  try {
    const base = new URL(masterUrl, window.location.href);
    const master = await (await fetch(base, { signal })).text();
    const variants = master.split("\n").map((l) => l.trim()).filter((l) => l && !l.startsWith("#"));
    // Match hls.js startLevel 1 (the second rendition) when available.
    const variant = variants[Math.min(1, variants.length - 1)];
    if (!variant) return;

    const variantUrl = new URL(variant, base);
    const playlist = await (await fetch(variantUrl, { signal })).text();
    const init = /#EXT-X-MAP:URI="([^"]+)"/.exec(playlist)?.[1];
    const firstSegment = playlist.split("\n").map((l) => l.trim()).find((l) => l && !l.startsWith("#"));
    await Promise.all(
      [init, firstSegment].filter((u): u is string => !!u).map((u) => fetch(new URL(u, variantUrl), { signal }).then((r) => r.arrayBuffer())),
    );
  } catch {
    warmed.delete(masterUrl); // best effort; try again next time
  }
}
