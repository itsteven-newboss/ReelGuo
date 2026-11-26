import { create } from "zustand";
import { hasUserActivation, setLiveMuted } from "../player/sound";
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
  /**
   * Sound is on by default. `muted` is the viewer's own choice (the mute buttons, M, the volume slider), remembered
   * for the next visit. Videos play muted while `muted || soundBlocked`: see `soundOff`.
   */
  muted: boolean;
  setMuted: (muted: boolean) => void;
  /** The mute buttons and M: sound on when it is off (by choice or blocked), else mute. */
  toggleSound: () => void;
  /**
   * The browser refused to start a video with sound (no tap or click on the page yet, e.g. a link opened straight
   * into a series): it plays muted until the viewer's first tap, click or key press anywhere, which turns sound on.
   */
  soundBlocked: boolean;
  blockSound: () => void;
  /** Settings → 启动时静音: off (default) = launches keep the last choice; on = every launch starts muted. */
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
  // Read once at launch: sound on, unless the viewer muted last time or asked for every launch to start muted.
  // (rg.sound replaces rg.muted, which also recorded the browser's autoplay fallback, not only the viewer's choice.)
  muted: storage.get("rg.muteOnStartup") === "1" || storage.get("rg.sound") === "off",
  setMuted: (muted) => {
    storage.set("rg.sound", muted ? "off" : "on");
    setLiveMuted(muted);
    set({ muted, soundBlocked: false });
  },
  toggleSound: () => {
    const { muted, soundBlocked, setMuted } = get();
    setMuted(!(muted || soundBlocked));
  },
  soundBlocked: false,
  blockSound: () => { if (!get().muted) set({ soundBlocked: true }); },
  muteOnStartup: storage.get("rg.muteOnStartup") === "1",
  setMuteOnStartup: (on) => {
    storage.set("rg.muteOnStartup", on ? "1" : "0");
    set({ muteOnStartup: on });
  },
  volume: Number(storage.get("rg.volume") ?? "0.8") || 0.8,
  setVolume: (volume) => {
    storage.set("rg.volume", String(volume));
    const muted = volume === 0;
    if (muted !== get().muted || get().soundBlocked) get().setMuted(muted);
    set({ volume });
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

/** Whether videos play muted: the viewer muted them, or the browser has not allowed sound yet. */
export const soundOff = (s: Pick<UiState, "muted" | "soundBlocked">) => s.muted || s.soundBlocked;

// While sound is blocked, the viewer's first tap, click or key press anywhere turns it on, inside that gesture.
// Listeners on document run after React's (on the root), so a mute button's own click has already decided.
if (typeof document !== "undefined") {
  const unblock = () => {
    const ui = useUi.getState();
    if (!ui.soundBlocked || ui.muted || !hasUserActivation()) return;
    setLiveMuted(false);
    useUi.setState({ soundBlocked: false });
  };
  for (const type of ["click", "keydown", "touchend"] as const) document.addEventListener(type, unblock, { passive: true });
}
