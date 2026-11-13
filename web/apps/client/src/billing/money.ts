/** Pure helpers for prices, coins and episode access (unit-tested). */

/** "US$4.99" for USD, "4.99 EUR" otherwise (the store currently sells in one currency). */
export function fmtMoney(cents: number, currency: string): string {
  const amount = (cents / 100).toFixed(2);
  return currency.toUpperCase() === "USD" ? `US$${amount}` : `${amount} ${currency.toUpperCase()}`;
}

/** 1,250 — thousands separators, negative balances keep their sign. */
export function fmtCoins(n: number): string {
  return n.toLocaleString("en-US");
}

/** Signed delta for the records list: "+550", "-30", "0". */
export function fmtDelta(n: number): string {
  return n > 0 ? `+${fmtCoins(n)}` : fmtCoins(n);
}

/** 2026-12-11 in the viewer's local time. */
export function fmtDay(iso: string): string {
  const d = new Date(iso);
  const pad = (x: number) => String(x).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export type Access = { vip: boolean; unlocked: ReadonlySet<number> };

/** A paid episode the viewer can't watch yet. Free episodes and VIP are never locked. */
export function isLocked(episode: { ep_num: number; is_free: boolean }, access: Access | null): boolean {
  if (episode.is_free) return false;
  if (!access) return true;
  return !access.vip && !access.unlocked.has(episode.ep_num);
}

/**
 * Auto-unlock spends coins without a tap, so it only runs when the viewer chose the episode (picked it or played on
 * into it) — never when they merely swiped past a series — and only when the balance covers it.
 */
export function shouldAutoUnlock(opts: { enabled: boolean; intent: boolean; balance: number; price: number; tried: boolean }): boolean {
  return opts.enabled && opts.intent && !opts.tried && opts.price > 0 && opts.balance >= opts.price;
}
