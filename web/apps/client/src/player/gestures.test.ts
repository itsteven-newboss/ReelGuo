import { describe, expect, it } from "vitest";
import {
  DOUBLE_TAP_MS, LOCK_SLIDE_PX, initialGesture, longPressElapsed, pointerDown, pointerMove, pointerUp,
  type GestureEffect, type GestureState,
} from "./gestures";

const down = (s: GestureState, x = 200, y = 400, t = 0) =>
  pointerDown(s, { x, y, t, base: 30, duration: 120, width: 400, viewportWidth: 400 });

const types = (effects: GestureEffect[]) => effects.map((e) => e.type);

describe("gesture machine", () => {
  it("a quick tap asks for a delayed single tap", () => {
    const a = down(initialGesture);
    const b = pointerUp(a.state, { x: 202, y: 401, t: 120, cancelled: false });
    expect(types(b.effects)).toEqual(["cancel-long-press-timer", "single-tap-pending"]);
  });

  it("two taps within 280 ms are a double tap", () => {
    const first = pointerUp(down(initialGesture).state, { x: 200, y: 400, t: 100, cancelled: false });
    const second = pointerUp(down(first.state, 200, 400, 300).state, { x: 200, y: 400, t: 100 + DOUBLE_TAP_MS - 10, cancelled: false });
    expect(types(second.effects)).toContain("double-tap");
  });

  it("a slow press is not a tap", () => {
    const r = pointerUp(down(initialGesture).state, { x: 200, y: 400, t: 300, cancelled: false });
    expect(types(r.effects)).not.toContain("single-tap-pending");
  });

  it("long press gives 2x, sliding up arms the lock, release locks", () => {
    const held = longPressElapsed(down(initialGesture).state);
    expect(types(held.effects)).toEqual(["hold-start"]);
    const armed = pointerMove(held.state, { x: 200, y: 400 - LOCK_SLIDE_PX - 1 });
    expect(armed.effects).toEqual([{ type: "hold-armed", armed: true }]);
    const released = pointerUp(armed.state, { x: 200, y: 300, t: 900, cancelled: false });
    expect(released.effects).toEqual([{ type: "hold-end", lock: true }]);
  });

  it("long press released without sliding does not lock", () => {
    const held = longPressElapsed(down(initialGesture).state);
    expect(pointerUp(held.state, { x: 200, y: 400, t: 900, cancelled: false }).effects).toEqual([{ type: "hold-end", lock: false }]);
  });

  it("horizontal drag scrubs proportionally and seeks on release", () => {
    const start = down(initialGesture);
    const moved = pointerMove(start.state, { x: 300, y: 405 }); // +100 px of 400 = +25% of 120 s
    const scrub = moved.effects.find((e) => e.type === "scrub");
    expect(scrub).toMatchObject({ type: "scrub", target: 60 });
    const end = pointerUp(moved.state, { x: 300, y: 405, t: 500, cancelled: false });
    expect(end.effects).toEqual([{ type: "scrub-end", target: 60 }]);
  });

  it("scrub clamps to the ends", () => {
    const moved = pointerMove(down(initialGesture).state, { x: -2000, y: 400 });
    expect(moved.effects.find((e) => e.type === "scrub")).toMatchObject({ target: 0 });
  });

  it("vertical drag is left to scrolling", () => {
    const moved = pointerMove(down(initialGesture).state, { x: 205, y: 300 });
    expect(moved.state.phase).toBe("pan");
    expect(types(pointerUp(moved.state, { x: 205, y: 300, t: 200, cancelled: false }).effects)).toEqual([]);
  });

  it("presses at the screen edge never scrub (iOS back swipe)", () => {
    const edge = down(initialGesture, 10);
    const moved = pointerMove(edge.state, { x: 200, y: 400 });
    expect(moved.state.phase).toBe("pan");
  });

  it("cancelled scrub does not seek", () => {
    const moved = pointerMove(down(initialGesture).state, { x: 300, y: 400 });
    expect(pointerUp(moved.state, { x: 300, y: 400, t: 500, cancelled: true }).effects).toEqual([{ type: "scrub-end", target: null }]);
  });
});
