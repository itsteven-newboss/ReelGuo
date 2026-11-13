import { createElement, type AnchorHTMLAttributes, type MouseEvent } from "react";
import { create } from "zustand";
import { clearOverlays } from "./state/nav";

/**
 * The portal uses real URLs (shareable, bookmarkable, back/forward), matching the reference site's page structure.
 * A tiny router is enough: one store with the current path and query, history push/replace, and link interception.
 */
type Location = { path: string; query: URLSearchParams; key: number };

const read = (key: number): Location => ({ path: window.location.pathname, query: new URLSearchParams(window.location.search), key });

export const useLocation = create<Location>(() => read(0));

let keySeq = 1;
/** Pages opened inside the app since it loaded, so "back" never leaves the site. */
let inAppSteps = 0;
function sync() {
  useLocation.setState(read(keySeq++));
}

export function installRouter() {
  const onPop = () => {
    const { path, query } = useLocation.getState();
    // Overlay entries (sheets, dialogs) share the page's URL; only real page changes re-render the route.
    if (window.location.pathname !== path || window.location.search !== (query.size ? `?${query}` : "")) {
      inAppSteps = Math.max(0, inAppSteps - 1);
      sync();
    }
  };
  window.addEventListener("popstate", onPop);
  return () => window.removeEventListener("popstate", onPop);
}

/** Goes to an in-app URL, closing any open sheet first and scrolling to the top. */
export function navigate(to: string, opts: { replace?: boolean; keepScroll?: boolean } = {}) {
  clearOverlays();
  const target = new URL(to, window.location.origin);
  const current = window.location.pathname + window.location.search;
  if (target.pathname + target.search === current && !opts.replace) {
    window.scrollTo({ top: 0, behavior: "smooth" });
    return;
  }
  if (opts.replace) window.history.replaceState({}, "", target.pathname + target.search);
  else {
    window.history.pushState({}, "", target.pathname + target.search);
    inAppSteps++;
  }
  sync();
  if (!opts.keepScroll) window.scrollTo(0, 0);
}

/** Back if there is somewhere to go back to in this app, otherwise home. */
export function goBack(fallback = "/") {
  if (inAppSteps > 0) window.history.back();
  else navigate(fallback, { replace: true });
}

/** An anchor that navigates in-app (real href for new tabs, middle clicks and crawlers). */
export function Link({ to, onClick, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement> & { to: string }) {
  return createElement("a", {
    ...rest,
    href: to,
    onClick: (e: MouseEvent<HTMLAnchorElement>) => {
      onClick?.(e);
      if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || rest.target === "_blank") return;
      e.preventDefault();
      navigate(to);
    },
  });
}

/** Path patterns like /video/:id → { id }. */
export function match(pattern: string, path: string): Record<string, string> | null {
  const p = pattern.split("/").filter(Boolean);
  const s = path.split("/").filter(Boolean);
  if (p.length !== s.length) return null;
  const params: Record<string, string> = {};
  for (let i = 0; i < p.length; i++) {
    if (p[i]!.startsWith(":")) params[p[i]!.slice(1)] = decodeURIComponent(s[i]!);
    else if (p[i] !== s[i]) return null;
  }
  return params;
}

/** URLs for every page, so links stay consistent. */
export const href = {
  home: () => "/",
  channel: (slug: string, sort?: string) => `/c/${slug}${sort && sort !== "latest" ? `?sort=${sort}` : ""}`,
  topics: () => "/topics",
  topic: (id: number) => `/topics/${id}`,
  ranks: (kind = "hot") => `/ranks/${kind}`,
  posts: () => "/posts",
  post: (id: number) => `/post/${id}`,
  wayHome: () => "/go-home",
  download: () => "/download",
  search: (q?: string) => (q ? `/search?q=${encodeURIComponent(q)}` : "/search"),
  /** /video/{id}, or /video/{id}/ep-{n} for a chosen episode (one URL per episode, for search engines). */
  video: (id: number, ep?: number) => `/video/${id}${ep ? `/ep-${ep}` : ""}`,
  me: () => "/me",
  wallet: () => "/me/wallet",
  store: (resume?: { id: number; ep: number }, focus?: "vip") => {
    const q = new URLSearchParams();
    if (resume) q.set("resume", `${resume.id}:${resume.ep}`);
    if (focus) q.set("focus", focus);
    return `/store${q.size ? `?${q}` : ""}`;
  },
  settings: () => "/settings",
};
