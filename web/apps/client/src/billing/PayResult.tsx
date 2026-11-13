import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { CircleCheck, CircleX, Clock } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, useWallet } from "../api";
import { useAuth } from "../state/auth";
import { href, navigate } from "../router";
import { clearReturnTarget, refreshEntitlements, returnTarget } from "./actions";
import { fmtCoins, fmtDay } from "./money";

const POLL_MS = 1500;
const GIVE_UP_MS = 60_000;

/**
 * /pay/result?order=…: where the provider's payment page sends the buyer back. The order turns paid when the
 * provider's server notice arrives, which can be a moment after the redirect, so this page polls.
 */
export function PayResultPage({ orderNo }: { orderNo: string }) {
  const { t } = useTranslation();
  const status = useAuth((s) => s.status);
  const signedIn = status === "signed_in" && /^[A-Z0-9]{10,32}$/.test(orderNo);
  const onBack = () => navigate("/");
  const [resume] = useState(() => returnTarget(orderNo));
  const started = useRef(Date.now());
  const [timedOut, setTimedOut] = useState(false);

  const order = useQuery({
    queryKey: ["order", orderNo],
    enabled: signedIn,
    queryFn: async () => unwrap(await api.GET("/api/v1/orders/{orderNo}", { params: { path: { orderNo } } })),
    refetchInterval: (q) => (q.state.data?.status === "pending" && !timedOut ? POLL_MS : false),
    retry: false,
  });
  const wallet = useWallet(signedIn && order.data?.status === "paid");

  const state = order.data?.status;
  useEffect(() => {
    if (state !== "pending") return undefined;
    const left = GIVE_UP_MS - (Date.now() - started.current);
    const timer = window.setTimeout(() => setTimedOut(true), Math.max(0, left));
    return () => window.clearTimeout(timer);
  }, [state]);

  useEffect(() => {
    if (state === "paid") {
      void refreshEntitlements();
      clearReturnTarget();
    }
  }, [state]);

  let icon = <Spinner size={44} />;
  let title = t("billing.resultChecking");
  let sub = t("billing.resultCheckingSub");
  if (status === "signed_out") {
    icon = <Clock size={46} className="text-mut" />;
    title = t("billing.resultSignIn");
    sub = t("billing.resultSignInSub");
  } else if (order.isError) {
    icon = <CircleX size={46} className="text-danger" />;
    title = t("billing.resultUnknown");
    sub = "";
  } else if (state === "paid") {
    icon = <CircleCheck size={46} className="text-success" />;
    title = t("billing.resultPaid");
    const d = order.data!;
    sub = d.kind === "vip"
      ? (wallet.data?.vip_until ? t("billing.resultVip", { date: fmtDay(wallet.data.vip_until) }) : t("billing.resultVipShort"))
      : t("billing.resultCoins", { n: fmtCoins(d.coins + d.bonus_coins) });
  } else if (state && state !== "pending") {
    icon = <CircleX size={46} className="text-danger" />;
    title = t("billing.resultFailed");
    sub = t("billing.resultFailedSub");
  } else if (timedOut) {
    icon = <Clock size={46} className="text-star" />;
    title = t("billing.resultTimeout");
    sub = t("billing.resultTimeoutSub");
  }

  const btn = "w-full h-11 rounded-(--r-ctl) text-[13.5px] font-bold pressable";
  return (
    <div className="min-h-[70vh] flex flex-col items-center justify-center px-8 py-16 text-center">
      <div className="w-full max-w-[340px] flex flex-col items-center">
        <div className="h-14 flex items-center justify-center">{icon}</div>
        <h1 className="mt-4 text-[20px] font-bold text-on">{title}</h1>
        {sub && <p className="mt-2 text-[12.5px] text-mut leading-relaxed">{sub}</p>}
        {order.data && <p className="mt-3 text-[10.5px] text-dim">{order.data.product_name} · {t("billing.orderNo", { no: orderNo })}</p>}

        <div className="mt-8 w-full flex flex-col gap-2.5">
          {status === "signed_out" && (
            <button onClick={() => useAuth.getState().openLogin()} className={`${btn} bg-primary text-on-primary`}>{t("billing.signIn")}</button>
          )}
          {state === "paid" && resume && (
            <button onClick={() => navigate(href.video(resume.id, resume.ep))} className={`${btn} bg-primary text-on-primary`}>
              {t("billing.continueWatching", { title: resume.title, ep: resume.ep })}
            </button>
          )}
          {state && state !== "pending" && state !== "paid" && (
            <button onClick={() => navigate(href.store(resume))} className={`${btn} bg-primary text-on-primary`}>{t("billing.retry")}</button>
          )}
          {(state === "paid" || timedOut) && (
            <button onClick={() => navigate(href.wallet())} className={`${btn} bg-s3 text-on`}>{t("billing.viewWallet")}</button>
          )}
          <button onClick={onBack} className={`${btn} ${state === "paid" && !resume ? "bg-primary text-on-primary" : "bg-s2 text-mut"}`}>{t("billing.done")}</button>
        </div>
      </div>
    </div>
  );
}
