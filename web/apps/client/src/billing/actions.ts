import { ApiError, unwrap } from "@reelguo/api-client";
import { api, queryClient, type Product, type Wallet } from "../api";
import type { ResumeTarget } from "../state/nav";
import { useUi } from "../state/ui";
import { apiErrorText, t } from "../i18n";

const RETURN_KEY = "rg.pay.return.v1";

function message(e: unknown) {
  return apiErrorText(e);
}

/** After an unlock or a purchase, everything that depends on entitlements is refetched. */
export function refreshEntitlements(seriesId?: number) {
  void queryClient.invalidateQueries({ queryKey: ["wallet"] });
  void queryClient.invalidateQueries({ queryKey: ["wallet-entries"] });
  void queryClient.invalidateQueries({ queryKey: seriesId ? ["access", seriesId] : ["access"] });
  return queryClient.invalidateQueries({ queryKey: ["play"] });
}

/** Sign-in or sign-out: entitlements belong to the account, so drop them and re-check playback. */
export function resetEntitlements() {
  queryClient.removeQueries({ queryKey: ["wallet"] });
  queryClient.removeQueries({ queryKey: ["wallet-entries"] });
  queryClient.removeQueries({ queryKey: ["access"] });
  void queryClient.invalidateQueries({ queryKey: ["play"] });
}

export type UnlockResult = "ok" | "insufficient" | "failed";

/** Buys one episode with coins (idempotent on the server), then reloads its play link. */
export async function unlockEpisode(id: number, ep: number, opts: { auto?: boolean } = {}): Promise<UnlockResult> {
  const toast = useUi.getState().toast;
  try {
    const r = unwrap(await api.POST("/api/v1/series/{id}/episodes/{ep}/unlock", { params: { path: { id, ep } } }));
    queryClient.setQueryData<Wallet>(["wallet"], r.wallet);
    void queryClient.invalidateQueries({ queryKey: ["access", id] });
    void queryClient.invalidateQueries({ queryKey: ["wallet-entries"] });
    await queryClient.invalidateQueries({ queryKey: ["play", id, ep] });
    if (r.result === "unlocked") toast(t(opts.auto ? "billing.autoUnlocked" : "billing.unlocked", { ep, coins: r.spent }), "ok");
    return "ok";
  } catch (e) {
    if (e instanceof ApiError && e.code === "insufficient_coins") {
      void queryClient.invalidateQueries({ queryKey: ["wallet"] });
      return "insufficient";
    }
    toast(message(e));
    return "failed";
  }
}

export async function setAutoUnlock(on: boolean) {
  try {
    const w = unwrap(await api.PUT("/api/v1/me/wallet/settings", { body: { auto_unlock: on } }));
    queryClient.setQueryData<Wallet>(["wallet"], w);
    useUi.getState().toast(on ? t("billing.autoOn") : t("billing.autoOff"), on ? "ok" : "info");
  } catch (e) {
    useUi.getState().toast(message(e));
  }
}

/**
 * Creates the order and leaves for the provider's payment page. The page returns to /pay/result?order=…; what to
 * resume afterwards is kept in this tab's sessionStorage.
 */
export async function startCheckout(product: Product, resume?: ResumeTarget): Promise<void> {
  const r = unwrap(await api.POST("/api/v1/orders", { body: { product_id: product.id } }));
  try {
    sessionStorage.setItem(RETURN_KEY, JSON.stringify({ orderNo: r.order.order_no, resume }));
  } catch {
    /* storage unavailable: the result page simply has no "continue watching" button */
  }
  window.location.assign(r.checkout_url);
}

export function returnTarget(orderNo: string): ResumeTarget | undefined {
  try {
    const saved = JSON.parse(sessionStorage.getItem(RETURN_KEY) ?? "null") as { orderNo?: string; resume?: ResumeTarget } | null;
    return saved?.orderNo === orderNo ? saved.resume : undefined;
  } catch {
    return undefined;
  }
}

export function clearReturnTarget() {
  try {
    sessionStorage.removeItem(RETURN_KEY);
  } catch {
    /* ignore */
  }
}
