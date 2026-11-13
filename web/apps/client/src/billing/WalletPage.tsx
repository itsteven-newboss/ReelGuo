import { Empty, Spinner } from "@reelguo/ui";
import { ChevronRight, Coins, Crown, Lock, Receipt, RotateCcw } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useWallet, useWalletEntries, type WalletEntry } from "../api";
import { useAuth } from "../state/auth";
import { href, Link, navigate } from "../router";
import { Toggle } from "../ui/Overlays";
import { setAutoUnlock } from "./actions";
import { fmtCoins, fmtDay, fmtDelta } from "./money";
import { AccountFrame, BalanceCard } from "./StorePage";

/** 我的钱包: balance, VIP, the auto-unlock switch and 消费记录 (every change to the wallet). */
export function WalletPage() {
  const { t } = useTranslation();
  const signedIn = useAuth((s) => s.status === "signed_in");
  const wallet = useWallet(signedIn);
  const entries = useWalletEntries(signedIn);
  const items = entries.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <AccountFrame crumbs={[{ label: "个人中心", to: href.me() }, { label: t("billing.wallet") }]} title={t("billing.wallet")} sub={t("billing.walletSub")}>
        <div>
          <BalanceCard action={
            <button onClick={() => navigate(href.store())} className="h-9 px-4 rounded-full bg-primary text-on-primary text-[12.5px] font-bold pressable shrink-0">
              {t("billing.topUp")}
            </button>
          } />

          <div className="mt-3 rounded-(--r-md) bg-s2 flex items-center gap-3 px-3.5 py-3">
            <span className="w-8 h-8 rounded-full bg-s3 flex items-center justify-center text-primary shrink-0"><Lock size={15} /></span>
            <div className="flex-1 min-w-0">
              <div className="text-[12.5px] text-on">{t("billing.autoUnlock")}</div>
              <div className="text-[10.5px] text-mut">{t("billing.autoUnlockSub")}</div>
            </div>
            <Toggle on={wallet.data?.auto_unlock ?? false} onChange={(on) => void setAutoUnlock(on)} label={t("billing.autoUnlock")} />
          </div>

          <div className="text-[10.5px] font-semibold text-dim tracking-wide px-1 mt-5 mb-1.5">{t("billing.records")}</div>
          <div className="rounded-(--r-md) bg-s2 divide-y divide-s3">
            {entries.isPending ? (
              <div className="flex justify-center py-10"><Spinner /></div>
            ) : items.length === 0 ? (
              <div className="py-6"><Empty icon={<Receipt size={22} />} title={t("billing.noRecords")} sub={t("billing.noRecordsSub")} /></div>
            ) : items.map((e) => <EntryRow key={e.id} entry={e} />)}
          </div>
          {entries.hasNextPage && (
            <button onClick={() => void entries.fetchNextPage()} disabled={entries.isFetchingNextPage}
              className="mt-3 w-full h-9 rounded-(--r-ctl) bg-s3 text-[12px] text-mut pressable">
              {entries.isFetchingNextPage ? t("common.loading") : t("billing.loadMore")}
            </button>
          )}
        </div>
    </AccountFrame>
  );
}

const KIND_ICON = {
  purchase: <Coins size={15} />,
  unlock: <Lock size={14} />,
  refund: <RotateCcw size={14} />,
  chargeback: <RotateCcw size={14} />,
  adjustment: <Receipt size={14} />,
} as const;

function EntryRow({ entry: e }: { entry: WalletEntry }) {
  const { t } = useTranslation();
  const coins = e.coins + e.bonus_coins;
  const when = new Date(e.at);
  return (
    <div className="flex items-center gap-3 px-3.5 py-3">
      <span className="w-8 h-8 rounded-full bg-s3 flex items-center justify-center text-mut shrink-0">{KIND_ICON[e.kind]}</span>
      <div className="flex-1 min-w-0">
        <div className="text-[12.5px] text-on leading-snug clamp-2">{e.description}</div>
        <div className="text-[10.5px] text-dim">
          {t(`billing.kind.${e.kind}`)} · {fmtDay(e.at)} {when.toTimeString().slice(0, 5)}
          {e.order_no && <> · {e.order_no}</>}
        </div>
      </div>
      <div className="text-right shrink-0">
        {coins !== 0 && <div className={`text-[13px] font-bold tabular-nums ${coins > 0 ? "text-star" : "text-on"}`}>{fmtDelta(coins)}</div>}
        {e.vip_days !== 0 && <div className="text-[10.5px] text-mut flex items-center gap-0.5 justify-end"><Crown size={10} />{t("billing.vipDaysDelta", { n: fmtDelta(e.vip_days) })}</div>}
        {!!e.bonus_coins && coins !== 0 && <div className="text-[10px] text-dim">{t("billing.bonusPart", { n: fmtDelta(e.bonus_coins) })}</div>}
      </div>
    </div>
  );
}

/** The wallet strip on 我的 (signed in, monetization on). */
export function WalletStrip() {
  const { t } = useTranslation();
  const wallet = useWallet(true);
  const w = wallet.data;
  return (
    <div className="rounded-[14px] bg-[#141414] border border-white/[.06] flex items-center gap-3 p-4">
      <Link to={href.wallet()} className="flex-1 min-w-0 flex items-center gap-3 text-left">
        <span className="w-9 h-9 rounded-full bg-star/15 text-star flex items-center justify-center shrink-0"><Coins size={18} /></span>
        <span className="flex-1 min-w-0">
          <span className="block text-[15px] font-bold text-on tabular-nums">
            {w ? fmtCoins(w.balance) : "—"} <span className="text-[11px] text-mut font-normal">{t("billing.coins")}</span>
          </span>
          <span className="block text-[10.5px] text-mut truncate">
            {w?.vip_active && w.vip_until ? t("billing.vipActive", { date: fmtDay(w.vip_until) }) : t("billing.vipNone")}
          </span>
        </span>
        <ChevronRight size={16} className="text-dim shrink-0" />
      </Link>
      <button onClick={() => navigate(href.store())} className="h-8 px-3.5 rounded-full bg-primary text-on-primary text-[12px] font-bold pressable shrink-0">
        {t("billing.topUp")}
      </button>
    </div>
  );
}
