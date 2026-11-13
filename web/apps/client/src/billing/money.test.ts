import { describe, expect, it } from "vitest";
import { fmtCoins, fmtDelta, fmtMoney, isLocked, shouldAutoUnlock } from "./money";

describe("money formatting", () => {
  it("formats store prices", () => {
    expect(fmtMoney(499, "USD")).toBe("US$4.99");
    expect(fmtMoney(9999, "usd")).toBe("US$99.99");
    expect(fmtMoney(1000, "EUR")).toBe("10.00 EUR");
  });

  it("formats coin balances and deltas", () => {
    expect(fmtCoins(1250)).toBe("1,250");
    expect(fmtCoins(-30)).toBe("-30");
    expect(fmtDelta(550)).toBe("+550");
    expect(fmtDelta(-30)).toBe("-30");
    expect(fmtDelta(0)).toBe("0");
  });
});

describe("episode access", () => {
  const paid = { ep_num: 3, is_free: false };
  it("never locks free episodes", () => {
    expect(isLocked({ ep_num: 1, is_free: true }, null)).toBe(false);
  });
  it("locks paid episodes for guests and non-buyers", () => {
    expect(isLocked(paid, null)).toBe(true);
    expect(isLocked(paid, { vip: false, unlocked: new Set([4]) })).toBe(true);
  });
  it("opens bought episodes, and everything while VIP", () => {
    expect(isLocked(paid, { vip: false, unlocked: new Set([3]) })).toBe(false);
    expect(isLocked(paid, { vip: true, unlocked: new Set() })).toBe(false);
  });
});

describe("auto-unlock", () => {
  const base = { enabled: true, intent: true, balance: 100, price: 30, tried: false };
  it("unlocks when the viewer chose the episode and can afford it", () => {
    expect(shouldAutoUnlock(base)).toBe(true);
  });
  it("never spends on a swipe, twice, when off, or beyond the balance", () => {
    expect(shouldAutoUnlock({ ...base, intent: false })).toBe(false);
    expect(shouldAutoUnlock({ ...base, tried: true })).toBe(false);
    expect(shouldAutoUnlock({ ...base, enabled: false })).toBe(false);
    expect(shouldAutoUnlock({ ...base, balance: 29 })).toBe(false);
  });
});
