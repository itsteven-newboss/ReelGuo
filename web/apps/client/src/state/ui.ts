import { create } from "zustand";
import { storage } from "../storage";

export type ToastIcon = "ok" | "heart" | "star" | "info";
export type Toast = { id: number; msg: string; icon: ToastIcon };

type UiState = {
  /** Clean mode: every overlay (and the tab bar) hidden, video only. Toggled by a single tap. */
  clean: boolean;
  setClean: (clean: boolean) => void;
  /** Immersive fullscreen player. */
  fullscreen: boolean;
  setFullscreen: (on: boolean) => void;
  /** Playback starts muted (autoplay rules); the choice is remembered once the viewer unmutes. */
  muted: boolean;
  setMuted: (muted: boolean) => void;
  /** Settings → 启动时静音: on (default) = every launch starts muted; off = launches keep the last choice. */
  muteOnStartup: boolean;
  setMuteOnStartup: (on: boolean) => void;
  volume: number;
  setVolume: (v: number) => void;
  /** Desktop theatre mode: sidebar hidden, wider video. */
  theater: boolean;
  setTheater: (on: boolean) => void;

  toasts: Toast[];
  toast: (msg: string, icon?: ToastIcon) => void;
};

let toastId = 1;

export const useUi = create<UiState>((set, get) => ({
  clean: false,
  setClean: (clean) => set({ clean }),
  fullscreen: false,
  setFullscreen: (fullscreen) => set({ fullscreen }),
  // Read once at launch: with mute-on-startup (the default) every launch starts muted.
  muted: storage.get("rg.muteOnStartup") !== "0" || storage.get("rg.muted") !== "0",
  setMuted: (muted) => {
    storage.set("rg.muted", muted ? "1" : "0");
    set({ muted });
  },
  muteOnStartup: storage.get("rg.muteOnStartup") !== "0",
  setMuteOnStartup: (on) => {
    storage.set("rg.muteOnStartup", on ? "1" : "0");
    set({ muteOnStartup: on });
  },
  volume: Number(storage.get("rg.volume") ?? "0.8") || 0.8,
  setVolume: (volume) => {
    storage.set("rg.volume", String(volume));
    set({ volume, muted: volume === 0 });
  },
  theater: false,
  setTheater: (theater) => set({ theater }),

  toasts: [],
  toast: (msg, icon = "info") => {
    const id = toastId++;
    set({ toasts: [...get().toasts.slice(-2), { id, msg, icon }] });
    window.setTimeout(() => set({ toasts: get().toasts.filter((t) => t.id !== id) }), 2200);
  },
}));
