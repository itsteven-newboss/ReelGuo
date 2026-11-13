import { create } from "zustand";
import { storage } from "../storage";

const AGE_KEY = "rg.age_confirmed.v1";

type Theme = "dark" | "light";

type SessionState = {
  /** Set when any API call answers 451 (country on the block list). */
  regionBlocked: boolean;
  setRegionBlocked: (blocked: boolean) => void;

  /** The 18+ confirmation, remembered on this device. */
  ageConfirmed: boolean;
  confirmAge: () => void;

  theme: Theme;
  setTheme: (theme: Theme) => void;
};

function applyTheme(theme: Theme) {
  document.documentElement.classList.toggle("theme-light", theme === "light");
  document.documentElement.style.colorScheme = theme;
}

const initialTheme: Theme = storage.get("rg.theme") === "light" ? "light" : "dark";
applyTheme(initialTheme);

export const useSession = create<SessionState>((set) => ({
  regionBlocked: false,
  setRegionBlocked: (regionBlocked) => set({ regionBlocked }),

  ageConfirmed: storage.get(AGE_KEY) !== null,
  confirmAge: () => {
    storage.set(AGE_KEY, new Date().toISOString());
    set({ ageConfirmed: true });
  },

  theme: initialTheme,
  setTheme: (theme) => {
    storage.set("rg.theme", theme);
    applyTheme(theme);
    set({ theme });
  },
}));
