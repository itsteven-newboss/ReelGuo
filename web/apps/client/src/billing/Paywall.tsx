import { Spinner } from "@reelguo/ui";
import { ChevronRight, Coins, Crown, Lock } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useWallet, type SeriesDetail } from "../api";
import { useAuth } from "../state/auth";
import { href, navigate } from "../router";
import { setAutoUnlock, unlockEpisode } from "./actions";
import { fmtCoins, shouldAutoUnlock } from "./money";

/** Episodes that already had an automatic unlock attempt in this page session (never spend twice without a tap). */
const autoTried = new Set<string>();

/**
 * Shown over the cover when the active episode is paid and not yet the viewer's (the API answered 402
 * episode_locked). Swiping to other items keeps working around it.
 */
export function Paywall({ series, ep, intent, wide }: {
  series: SeriesDetail;
  ep: number;
  /** The viewer picked this episode or played on into it (auto-unlock may spend coins only then). */
  intent: boolean;
  wide: boolean;
}) {
  const { t } = useTranslation();
  const signedIn = useAuth((s) => s.status === "signed_in");
  const openLogin = useAuth((s) => s.openLogin);
  const wallet = useWallet(signedIn);
  const [busy, setBusy] = useState(false);

  const episode = series.episodes.find((e) => e.ep_num === ep);
  const price = episode?.price ?? 0;
  const balance = wallet.data?.balance ?? 0;
  const enough = signedIn && !!wallet.data && balance >= price;
  const freeLead = series.episodes.findIndex((e) => !e.is_free);
  const resume = { id: series.id, ep };

  const key = `${series.id}:${ep}`;
  useEffect(() => {
    if (!wallet.data || busy) return;
    if (!shouldAutoUnlock({ enabled: wallet.data.auto_unlock, intent, balance: wallet.data.balance, price, tried: autoTried.has(key) })) return;
    autoTried.add(key);
    setBusy(true);
    void unlockEpisode(series.id, ep, { auto: true }).finally(() => setBusy(false));
  }, [wallet.data, intent, price, key, busy, series.id, ep]);

  const onUnlock = async () => {
    if (!signedIn) return openLogin();
    if (!enough) return navigate(href.store(resume));
    setBusy(true);
    const r = await unlockEpisode(series.id, ep);
    setBusy(false);
    if (r === "insufficient") navigate(href.store(resume));
  };

  const primary = !signedIn ? t("billing.loginToUnlock") : enough ? t("billing.unlockFor", { price }) : t("billing.notEnough");

  return (
    <div className="absolute inset-0 z-20 flex items-center justify-center px-6 pointer-events-none" style={{ paddingBottom: wide ? 0 : 70 }}>
      <div role="dialog" aria-label={t("billing.lockedTitle", { ep })}
        className="pointer-events-auto w-full max-w-[300px] rounded-(--r-lg) p-5 text-center elev-3 a-scale-in"
        style={{ background: "rgba(20,20,24,.82)", backdropFilter: "blur(14px)", WebkitBackdropFilter: "blur(14px)" }}>
        <span className="mx-auto w-11 h-11 rounded-full bg-white/10 flex items-center justify-center text-white"><Lock size={20} /></span>
        <div className="mt-3 text-[16px] font-bold text-white">{t("billing.lockedTitle", { ep })}</div>
        <div className="mt-1 text-[11.5px] text-white/60">
          {freeLead > 0 ? t("billing.lockedSubFree", { free: freeLead }) : t("billing.lockedSub")}
        </div>

        <button onClick={() => void onUnlock()} disabled={busy || (signedIn && wallet.isPending)}
          className="mt-4 w-full h-11 rounded-(--r-ctl) bg-primary text-on-primary text-[13.5px] font-bold flex items-center justify-center gap-1.5 pressable disabled:opacity-70">
          {busy ? <><Spinner size={16} />{t("billing.unlocking")}</> : <>{enough && <Coins size={15} />}{primary}</>}
        </button>

        <div className="mt-2 text-[11px] text-white/60 tabular-nums">
          {signedIn && wallet.data ? (
            <>
              {t("billing.balance", { n: fmtCoins(balance) })}
              {" · "}
              <button onClick={() => navigate(href.store(resume))} className="text-primary font-semibold pressable">{t("billing.topUp")}</button>
            </>
          ) : t("billing.episodePrice", { price })}
        </div>

        <button onClick={() => (signedIn ? navigate(href.store(resume, "vip")) : openLogin())}
          className="mt-4 w-full h-10 rounded-(--r-ctl) bg-white/8 text-[12.5px] text-white flex items-center gap-2 px-3 pressable">
          <Crown size={15} className="text-star shrink-0" />
          <span className="flex-1 text-left">{t("billing.openVip")}</span>
          <ChevronRight size={14} className="text-white/50" />
        </button>

        {signedIn && wallet.data && (
          <label className="mt-3 flex items-center justify-center gap-1.5 text-[11px] text-white/60 cursor-pointer">
            <input type="checkbox" checked={wallet.data.auto_unlock} onChange={(e) => void setAutoUnlock(e.target.checked)}
              className="accent-[var(--p-primary)]" />
            {t("billing.autoUnlock")}
          </label>
        )}
      </div>
    </div>
  );
}
