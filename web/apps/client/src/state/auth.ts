import { ApiError, unwrap, type Schemas } from "@reelguo/api-client";
import { create } from "zustand";
import { api, queryClient, type SeriesDetail } from "../api";
import { resetEntitlements } from "../billing/actions";
import { storage } from "../storage";
import { useLibrary, type WatchProgress } from "./library";
import { closeOverlay, openOverlay } from "./nav";
import { clearAccess, onRefresh, refreshAccess, setAccess, type AuthResponse } from "./token";
import { useUi } from "./ui";

export type MeUser = Schemas["MeUserDto"];
type Me = Schemas["MeDto"];

type AuthState = {
  /** "booting" until the refresh cookie has been tried once. */
  status: "booting" | "signed_out" | "signed_in";
  user: MeUser | null;
  follows: number[];
  postLikes: number[];
  postFavs: number[];

  loginOpen: boolean;
  /** Opens the login sheet (as a back-button layer). */
  openLogin: () => void;
  closeLogin: () => void;

  boot: () => Promise<void>;
  login: (username: string, password: string) => Promise<MeUser>;
  register: (input: { username: string; password: string; nickname: string; acceptTerms: boolean }) => Promise<MeUser>;
  logout: () => Promise<void>;
};

let booting: Promise<void> | null = null;

/**
 * Not a secret: only remembers that this device signed in, so visitors who never did don't send a refresh
 * request (and get a 401) on every start. The session itself is the httpOnly cookie.
 */
const HINT_KEY = "rg.signedIn.v1";

export const useAuth = create<AuthState>((set, get) => ({
  status: "booting",
  user: null,
  follows: [],
  postLikes: [],
  postFavs: [],

  loginOpen: false,
  openLogin: () => {
    if (get().loginOpen) return;
    set({ loginOpen: true });
    openOverlay(() => set({ loginOpen: false }));
  },
  closeLogin: () => {
    if (get().loginOpen) closeOverlay();
  },

  boot: () => {
    // Once per page load (React StrictMode runs effects twice in development).
    booting ??= (async () => {
      if (storage.get(HINT_KEY) === null) {
        set({ status: "signed_out" });
        return;
      }
      const result = await refreshAccess();
      if (result.kind !== "ok") {
        set({ status: "signed_out" });
        return;
      }
      await signedIn(result.auth, false);
    })();
    return booting;
  },

  login: async (username, password) => {
    const auth = unwrap(await api.POST("/api/v1/auth/login", { body: { username, password } }));
    await signedIn(auth, true);
    return auth.user;
  },

  register: async ({ username, password, nickname, acceptTerms }) => {
    const auth = unwrap(await api.POST("/api/v1/auth/register", {
      body: { username, password, nickname: nickname || null, accept_terms: acceptTerms },
    }));
    await signedIn(auth, true);
    return auth.user;
  },

  logout: async () => {
    await flushHistory().catch(() => undefined);
    await api.POST("/api/v1/auth/logout").catch(() => undefined);
    signedOut();
    useUi.getState().toast("已退出登录", "ok");
  },
}));

/** A refresh that fails mid-session (expired, signed out elsewhere, account disabled) ends the session here too. */
onRefresh((result) => {
  const state = useAuth.getState();
  if (result.kind === "ok") useAuth.setState({ user: result.auth.user });
  else if (result.kind === "signed_out" && state.status === "signed_in") {
    signedOut();
    useUi.getState().toast("登录已过期，请重新登录");
  }
});

function signedOut() {
  clearAccess();
  storage.remove(HINT_KEY);
  // The device copy belonged to the account: don't leave it for the next person on this device.
  syncedHistory.clear();
  useLibrary.getState().replace({ likes: [], favs: [], history: [] });
  useAuth.setState({ status: "signed_out", user: null, follows: [], postLikes: [], postFavs: [] });
  queryClient.removeQueries({ queryKey: ["comments"] });
  resetEntitlements();
}

/**
 * After sign-in: on an explicit login/register, what was saved on this device while signed out is added to the
 * account (merge). On a later app start the account's copy simply replaces the device copy, so something removed
 * on another device doesn't come back.
 */
async function signedIn(auth: AuthResponse, merge: boolean) {
  setAccess(auth.access_token, auth.expires_in);
  storage.set(HINT_KEY, "1");
  useAuth.setState({ status: "signed_in", user: auth.user });
  // Bought episodes and VIP belong to the account: re-check anything locked a moment ago.
  resetEntitlements();
  try {
    const lib = useLibrary.getState();
    const history = lib.history.slice(0, 100).map(toHistoryItem);
    let me: Me;
    if (merge) {
      me = unwrap(await api.POST("/api/v1/me/merge", { body: { likes: lib.likes, favs: lib.favs, history } }));
    } else {
      if (history.length) await api.PUT("/api/v1/me/history", { body: { items: history } });
      me = unwrap(await api.GET("/api/v1/me"));
    }
    const remote = unwrap(await api.GET("/api/v1/me/history"));
    const items: WatchProgress[] = remote.map((h) => ({ id: h.id, ep: h.ep, t: h.t, dur: h.dur, at: h.at }));
    syncedHistory.clear();
    for (const h of items) syncedHistory.set(h.id, h.at);
    lib.replace({ likes: me.interactions.like_video_ids, favs: me.interactions.favorite_video_ids, history: items });
    useAuth.setState({
      user: me.user,
      follows: me.interactions.follow_user_ids,
      postLikes: me.interactions.like_post_ids,
      postFavs: me.interactions.favorite_post_ids,
    });
    // Counts on cached series now include this account's likes and favourites.
    void queryClient.invalidateQueries({ queryKey: ["series"] });
  } catch {
    // Signed in, but the lists couldn't be loaded: keep the device copy; the next start tries again.
  }
}

const toHistoryItem = (h: WatchProgress) => ({ id: h.id, ep: h.ep, t: h.t, dur: h.dur, at: h.at });

/* ---------------------------------------------------------------------------------------------
 * Watch history sync: changes are sent at most every 15 s, and when the app goes to the background.
 * ------------------------------------------------------------------------------------------- */

const syncedHistory = new Map<number, number>();
let historyTimer: number | undefined;

async function flushHistory(keepalive = false) {
  window.clearTimeout(historyTimer);
  historyTimer = undefined;
  if (useAuth.getState().status !== "signed_in") return;
  const items = useLibrary.getState().history.filter((h) => (syncedHistory.get(h.id) ?? 0) < h.at).slice(0, 100);
  if (!items.length) return;
  for (const h of items) syncedHistory.set(h.id, h.at);
  const res = await api.PUT("/api/v1/me/history", { body: { items: items.map(toHistoryItem) }, keepalive });
  if (!res.response.ok) for (const h of items) syncedHistory.delete(h.id);
}

useLibrary.subscribe((state, prev) => {
  if (state.history === prev.history || useAuth.getState().status !== "signed_in") return;
  historyTimer ??= window.setTimeout(() => void flushHistory().catch(() => undefined), 15_000);
});

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "hidden") void flushHistory(true).catch(() => undefined);
});

/** Clears watch history on this device and, when signed in, on the account. */
export async function clearWatchHistory() {
  useLibrary.getState().clearHistory();
  if (useAuth.getState().status !== "signed_in") return;
  window.clearTimeout(historyTimer);
  historyTimer = undefined;
  syncedHistory.clear();
  await api.DELETE("/api/v1/me/history").catch(() => undefined);
}

/* ---------------------------------------------------------------------------------------------
 * Interactions. Series likes and favourites also work signed out (device only); follows and post
 * interactions need an account. Every change is optimistic and undone if the server refuses it.
 * ------------------------------------------------------------------------------------------- */

export function errorMessage(e: unknown): string {
  return e instanceof ApiError ? e.message : "网络异常，请稍后重试";
}

const signedInNow = () => useAuth.getState().status === "signed_in";

function patchSeriesCounts(id: number, patch: Partial<SeriesDetail["counts"]>) {
  queryClient.setQueryData<SeriesDetail>(["series", id], (d) => (d ? { ...d, counts: { ...d.counts, ...patch } } : d));
}

/** Returns the new state, or null when the change failed (already undone). */
export async function toggleSeriesLike(id: number): Promise<boolean | null> {
  const lib = useLibrary.getState();
  const on = !lib.isLiked(id);
  lib.setLike(id, on);
  if (!signedInNow()) return on;
  try {
    const path = { params: { path: { id } } };
    const r = unwrap(on ? await api.PUT("/api/v1/series/{id}/like", path) : await api.DELETE("/api/v1/series/{id}/like", path));
    patchSeriesCounts(id, { like: r.count });
    return on;
  } catch (e) {
    lib.setLike(id, !on);
    useUi.getState().toast(errorMessage(e));
    return null;
  }
}

export async function toggleSeriesFav(id: number): Promise<boolean | null> {
  const lib = useLibrary.getState();
  const on = !lib.isFav(id);
  lib.setFav(id, on);
  if (!signedInNow()) return on;
  try {
    const path = { params: { path: { id } } };
    const r = unwrap(on ? await api.PUT("/api/v1/series/{id}/collect", path) : await api.DELETE("/api/v1/series/{id}/collect", path));
    patchSeriesCounts(id, { collect: r.count });
    return on;
  } catch (e) {
    lib.setFav(id, !on);
    useUi.getState().toast(errorMessage(e));
    return null;
  }
}

/** Signed out, counts from the server don't include this device's likes yet, so they're added for display. */
export function useCountBump() {
  const signedIn = useAuth((s) => s.status === "signed_in");
  return (on: boolean) => (!signedIn && on ? 1 : 0);
}

function toggleIn(list: number[], id: number, on: boolean) {
  return on ? [id, ...list.filter((x) => x !== id)] : list.filter((x) => x !== id);
}

export async function toggleFollow(userId: number, name: string) {
  const auth = useAuth.getState();
  if (auth.status !== "signed_in") return auth.openLogin();
  if (auth.user?.id === userId) return useUi.getState().toast("不能关注自己");
  const on = !auth.follows.includes(userId);
  useAuth.setState({ follows: toggleIn(auth.follows, userId, on) });
  try {
    const path = { params: { path: { id: userId } } };
    unwrap(on ? await api.PUT("/api/v1/users/{id}/follow", path) : await api.DELETE("/api/v1/users/{id}/follow", path));
    useUi.getState().toast(on ? `已关注 ${name}` : "已取消关注", on ? "ok" : "info");
  } catch (e) {
    useAuth.setState({ follows: toggleIn(useAuth.getState().follows, userId, !on) });
    useUi.getState().toast(errorMessage(e));
  }
}

/** Post likes and favourites. Returns the new count, or null when signed out or the change failed. */
export async function togglePost(kind: "like" | "collect", id: number): Promise<number | null> {
  const auth = useAuth.getState();
  if (auth.status !== "signed_in") {
    auth.openLogin();
    return null;
  }
  const key = kind === "like" ? "postLikes" : "postFavs";
  const on = !auth[key].includes(id);
  useAuth.setState({ [key]: toggleIn(auth[key], id, on) });
  try {
    const path = { params: { path: { id } } };
    const r = kind === "like"
      ? unwrap(on ? await api.PUT("/api/v1/posts/{id}/like", path) : await api.DELETE("/api/v1/posts/{id}/like", path))
      : unwrap(on ? await api.PUT("/api/v1/posts/{id}/collect", path) : await api.DELETE("/api/v1/posts/{id}/collect", path));
    useUi.getState().toast(kind === "like" ? (on ? "已加入喜欢" : "已取消喜欢") : (on ? "已收藏" : "已取消收藏"),
      on ? (kind === "like" ? "heart" : "star") : "info");
    return r.count;
  } catch (e) {
    useAuth.setState({ [key]: toggleIn(useAuth.getState()[key], id, !on) });
    useUi.getState().toast(errorMessage(e));
    return null;
  }
}
