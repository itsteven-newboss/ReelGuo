import { create } from "zustand";
import { storage } from "../storage";

/** Where a viewer stopped: series id, episode, seconds, duration, when. */
export type WatchProgress = { id: number; ep: number; t: number; dur: number; at: number };

type Persisted = { favs: number[]; likes: number[]; history: WatchProgress[]; searches: string[] };

const KEY = "rg.library.v1";
const MAX_HISTORY = 80;
const MAX_SEARCHES = 12;

function load(): Persisted {
  try {
    const raw = storage.get(KEY);
    const parsed = raw ? (JSON.parse(raw) as Partial<Persisted>) : {};
    return {
      favs: Array.isArray(parsed.favs) ? parsed.favs.filter((x) => typeof x === "number") : [],
      likes: Array.isArray(parsed.likes) ? parsed.likes.filter((x) => typeof x === "number") : [],
      history: Array.isArray(parsed.history) ? parsed.history.filter((h) => h && typeof h.id === "number").slice(0, MAX_HISTORY) : [],
      searches: Array.isArray(parsed.searches) ? parsed.searches.filter((x) => typeof x === "string").slice(0, MAX_SEARCHES) : [],
    };
  } catch {
    return { favs: [], likes: [], history: [], searches: [] };
  }
}

type LibraryState = Persisted & {
  isFav: (id: number) => boolean;
  /** Returns the new state (true = now a favourite). */
  toggleFav: (id: number) => boolean;
  isLiked: (id: number) => boolean;
  toggleLike: (id: number) => boolean;
  recordProgress: (p: Omit<WatchProgress, "at">) => void;
  progressOf: (id: number) => WatchProgress | undefined;
  clearHistory: () => void;
  addSearch: (q: string) => void;
  clearSearches: () => void;
  /** Clears favourites, likes, history and searches. */
  clearAll: () => void;
  /** Replaces lists with the account's copy (after sign-in) or empties them (after sign-out). */
  replace: (next: Partial<Pick<Persisted, "favs" | "likes" | "history">>) => void;
  /** Sets one series' like/favourite state without toggling (to undo an optimistic change). */
  setLike: (id: number, on: boolean) => void;
  setFav: (id: number, on: boolean) => void;
};

/**
 * Favourites, likes and watch history live on the device, so they work without an account.
 * When signed in, state/auth.ts mirrors every change to the account and loads the account's copy on sign-in.
 */
export const useLibrary = create<LibraryState>((set, get) => {
  const persist = () => {
    const { favs, likes, history, searches } = get();
    storage.set(KEY, JSON.stringify({ favs, likes, history, searches }));
  };
  return {
    ...load(),
    isFav: (id) => get().favs.includes(id),
    toggleFav: (id) => {
      const on = !get().favs.includes(id);
      set({ favs: on ? [id, ...get().favs] : get().favs.filter((x) => x !== id) });
      persist();
      return on;
    },
    isLiked: (id) => get().likes.includes(id),
    toggleLike: (id) => {
      const on = !get().likes.includes(id);
      set({ likes: on ? [id, ...get().likes] : get().likes.filter((x) => x !== id) });
      persist();
      return on;
    },
    recordProgress: (p) => {
      const entry: WatchProgress = { ...p, at: Date.now() };
      set({ history: [entry, ...get().history.filter((h) => h.id !== p.id)].slice(0, MAX_HISTORY) });
      persist();
    },
    progressOf: (id) => get().history.find((h) => h.id === id),
    clearHistory: () => {
      set({ history: [] });
      persist();
    },
    addSearch: (q) => {
      set({ searches: [q, ...get().searches.filter((x) => x !== q)].slice(0, MAX_SEARCHES) });
      persist();
    },
    clearSearches: () => {
      set({ searches: [] });
      persist();
    },
    clearAll: () => {
      set({ favs: [], likes: [], history: [], searches: [] });
      persist();
    },
    replace: (next) => {
      set({
        ...(next.favs ? { favs: next.favs } : {}),
        ...(next.likes ? { likes: next.likes } : {}),
        ...(next.history ? { history: next.history.slice(0, MAX_HISTORY) } : {}),
      });
      persist();
    },
    setLike: (id, on) => {
      const rest = get().likes.filter((x) => x !== id);
      set({ likes: on ? [id, ...rest] : rest });
      persist();
    },
    setFav: (id, on) => {
      const rest = get().favs.filter((x) => x !== id);
      set({ favs: on ? [id, ...rest] : rest });
      persist();
    },
  };
});
