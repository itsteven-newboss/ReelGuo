/**
 * Touch gesture state machine for the mobile player (UI-REFERENCE §3, "Gestures").
 * Pure: the component feeds pointer events and timer callbacks in, and acts on the returned effects.
 *
 *   tap            -> toggle clean mode (after a short wait, in case a second tap follows)
 *   double tap     -> play / pause
 *   long press     -> 2x speed while held; slide up past LOCK_SLIDE_PX and release to lock 2x
 *   horizontal drag-> scrub (full width = full duration), seek on release
 *   vertical drag  -> left to the native scroll-snap (next / previous item)
 */

export const TAP_MAX_MS = 260;
export const TAP_MAX_MOVE_PX = 16;
export const DOUBLE_TAP_MS = 280;
export const SINGLE_TAP_DELAY_MS = 250;
export const LONG_PRESS_MS = 380;
export const LOCK_SLIDE_PX = 64;
export const DRAG_START_PX = 16;
export const HORIZONTAL_RATIO = 1.2;
/** ReelGuo addition: scrubbing never starts within this distance of a screen edge (iOS back swipe). */
export const EDGE_GUARD_PX = 24;

export type GesturePhase = "idle" | "pressing" | "hold" | "scrub" | "pan";

export type GestureState = {
  phase: GesturePhase;
  startX: number;
  startY: number;
  startT: number;
  /** Playback position when the press started, for scrubbing. */
  base: number;
  duration: number;
  width: number;
  /** Whether scrubbing is allowed for this press (not started at a screen edge). */
  scrubAllowed: boolean;
  lockArmed: boolean;
  scrubTarget: number | null;
  lastTapAt: number;
};

export type GestureEffect =
  | { type: "start-long-press-timer" }
  | { type: "cancel-long-press-timer" }
  | { type: "hold-start" }
  | { type: "hold-armed"; armed: boolean }
  | { type: "hold-end"; lock: boolean }
  | { type: "scrub"; target: number; fraction: number }
  | { type: "scrub-end"; target: number | null }
  | { type: "single-tap-pending" }
  | { type: "double-tap" };

export const initialGesture: GestureState = {
  phase: "idle", startX: 0, startY: 0, startT: 0, base: 0, duration: 0, width: 1,
  scrubAllowed: true, lockArmed: false, scrubTarget: null, lastTapAt: -Infinity,
};

type Result = { state: GestureState; effects: GestureEffect[] };

export function pointerDown(
  s: GestureState,
  e: { x: number; y: number; t: number; base: number; duration: number; width: number; viewportWidth: number },
): Result {
  if (s.phase !== "idle") return { state: s, effects: [] };
  const scrubAllowed = e.x > EDGE_GUARD_PX && e.x < e.viewportWidth - EDGE_GUARD_PX;
  return {
    state: {
      ...s, phase: "pressing", startX: e.x, startY: e.y, startT: e.t,
      base: e.base, duration: e.duration, width: Math.max(1, e.width), scrubAllowed, lockArmed: false, scrubTarget: null,
    },
    effects: [{ type: "start-long-press-timer" }],
  };
}

export function longPressElapsed(s: GestureState): Result {
  if (s.phase !== "pressing") return { state: s, effects: [] };
  return { state: { ...s, phase: "hold", lockArmed: false }, effects: [{ type: "hold-start" }] };
}

export function pointerMove(s: GestureState, e: { x: number; y: number }): Result {
  const dx = e.x - s.startX;
  const dy = e.y - s.startY;

  if (s.phase === "hold") {
    const armed = s.startY - e.y > LOCK_SLIDE_PX;
    return armed === s.lockArmed
      ? { state: s, effects: [] }
      : { state: { ...s, lockArmed: armed }, effects: [{ type: "hold-armed", armed }] };
  }

  if (s.phase === "pressing") {
    if (s.scrubAllowed && s.duration > 0 && Math.abs(dx) > DRAG_START_PX && Math.abs(dx) > Math.abs(dy) * HORIZONTAL_RATIO) {
      return scrubTo({ ...s, phase: "scrub" }, dx, [{ type: "cancel-long-press-timer" }]);
    }
    if (Math.abs(dy) > DRAG_START_PX || Math.abs(dx) > DRAG_START_PX) {
      return { state: { ...s, phase: "pan" }, effects: [{ type: "cancel-long-press-timer" }] };
    }
    return { state: s, effects: [] };
  }

  if (s.phase === "scrub") return scrubTo(s, dx, []);
  return { state: s, effects: [] };
}

function scrubTo(s: GestureState, dx: number, effects: GestureEffect[]): Result {
  const target = Math.max(0, Math.min(s.duration - 0.2, s.base + (dx / s.width) * s.duration));
  return {
    state: { ...s, scrubTarget: target },
    effects: [...effects, { type: "scrub", target, fraction: s.duration > 0 ? target / s.duration : 0 }],
  };
}

export function pointerUp(s: GestureState, e: { x: number; y: number; t: number; cancelled: boolean }): Result {
  const reset: GestureState = { ...initialGesture, lastTapAt: s.lastTapAt };
  switch (s.phase) {
    case "hold":
      return { state: reset, effects: [{ type: "hold-end", lock: !e.cancelled && s.lockArmed }] };
    case "scrub":
      return { state: reset, effects: [{ type: "scrub-end", target: e.cancelled ? null : s.scrubTarget }] };
    case "pressing": {
      const effects: GestureEffect[] = [{ type: "cancel-long-press-timer" }];
      const moved = Math.hypot(e.x - s.startX, e.y - s.startY);
      if (e.cancelled || e.t - s.startT >= TAP_MAX_MS || moved >= TAP_MAX_MOVE_PX) return { state: reset, effects };
      if (e.t - s.lastTapAt < DOUBLE_TAP_MS) {
        return { state: { ...reset, lastTapAt: -Infinity }, effects: [...effects, { type: "double-tap" }] };
      }
      return { state: { ...reset, lastTapAt: e.t }, effects: [...effects, { type: "single-tap-pending" }] };
    }
    default:
      return { state: reset, effects: [] };
  }
}
