import { ApiError } from "@reelguo/api-client";
import { Empty, Spinner } from "@reelguo/ui";
import { Coins, Crown, CreditCard, FlaskConical } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useSeries, useStore, useWallet, type Product } from "../api";
import { Breadcrumb, Container, PageTitle } from "../portal/kit";
import { useLocation } from "../router";
import { useAuth } from "../state/auth";
import { closeOverlay, openOverlay, type ResumeTarget } from "../state/nav";
import { useUi } from "../state/ui";
import { CloseButton, Modal } from "../ui/Overlays";
import { startCheckout } from "./actions";
import { fmtCoins, fmtDay, fmtMoney } from "./money";

/** Portal page frame for the account pages: breadcrumb, title, a readable column. */
export function AccountFrame({ crumbs, title, sub, children }: { crumbs: { label: string; to?: string }[]; title: string; sub?: string; children: ReactNode }) {
  return (
    <Container>
      <Breadcrumb items={[{ label: "ReelGuo", to: "/" }, ...crumbs]} />
      <PageTitle sub={sub}>{title}</PageTitle>
      <div className="mt-6 max-w-[880px]">{children}</div>
    </Container>
  );
}

/** Balance and VIP, shared by the store and the wallet page. */
export function BalanceCard({ action }: { action?: ReactNode }) {
  const { t } = useTranslation();
  const signedIn = useAuth((s) => s.status === "signed_in");
  const wallet = useWallet(signedIn);
  const w = wallet.data;
  return (
    <div className="rounded-(--r-lg) bg-s2 p-4 elev-1">
      <div className="flex items-start gap-3">
        <span className="w-10 h-10 rounded-full bg-star/15 text-star flex items-center justify-center shrink-0"><Coins size={20} /></span>
        <div className="flex-1 min-w-0">
          <div className="text-[11px] text-mut">{t("billing.coinBalance")}</div>
          <div className="text-[24px] font-bold text-on tabular-nums leading-tight">{w ? fmtCoins(w.balance) : "—"}</div>
          {!!w?.bonus_coins && <div className="text-[10.5px] text-dim">{t("billing.bonusIncluded", { n: fmtCoins(w.bonus_coins) })}</div>}
        </div>
        {action}
      </div>
      <div className="mt-3 pt-3 border-t border-white/5 flex items-center gap-2 text-[12px]">
        <Crown size={14} className={w?.vip_active ? "text-star" : "text-dim"} />
        <span className={w?.vip_active ? "text-on" : "text-mut"}>
          {w?.vip_active && w.vip_until ? t("billing.vipActive", { date: fmtDay(w.vip_until) }) : t("billing.vipNone")}
        </span>
      </div>
    </div>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <>
      <div className="text-[10.5px] font-semibold text-dim tracking-wide px-1 mt-5 mb-2">{title}</div>
      {children}
    </>
  );
}

/** 充值中心: coin packs and VIP passes. Buying sends the viewer to the provider's payment page. */
export function StorePage() {
  const { t } = useTranslation();
  const { query } = useLocation();
  const [resumeId, resumeEp] = (query.get("resume") ?? "").split(":").map(Number);
  const resumeSeries = useSeries(resumeId || undefined);
  const resume: ResumeTarget | undefined = resumeId && resumeEp && resumeSeries.data ? { id: resumeId, ep: resumeEp, title: resumeSeries.data.title } : undefined;
  const focus = query.get("focus") === "vip" ? "vip" : undefined;
  const wide = true;
  const store = useStore();
  const signedIn = useAuth((s) => s.status === "signed_in");
  const openLogin = useAuth((s) => s.openLogin);
  const toast = useUi((s) => s.toast);
  const [picked, setPicked] = useState<Product | null>(null);
  const [busy, setBusy] = useState(false);
  const vipRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (focus === "vip" && store.data) vipRef.current?.scrollIntoView({ block: "start" });
  }, [focus, store.data]);

  const pick = (p: Product) => {
    if (!signedIn) return openLogin();
    setPicked(p);
    openOverlay(() => setPicked(null));
  };

  const pay = async () => {
    if (!picked) return;
    setBusy(true);
    try {
      await startCheckout(picked, resume);
      // The page is navigating away to the payment page.
    } catch (e) {
      setBusy(false);
      toast(e instanceof ApiError ? e.message : "网络异常，请稍后重试");
    }
  };

  const coins = store.data?.products.filter((p) => p.kind === "coins") ?? [];
  const vip = store.data?.products.filter((p) => p.kind === "vip") ?? [];

  const confirm = picked && store.data && (
    <div className="p-5">
      <div className="flex items-center gap-2 mb-4">
        <span className="text-[15px] font-bold text-on">{t("billing.confirmTitle")}</span>
        <span className="ml-auto"><CloseButton onClick={closeOverlay} /></span>
      </div>
      <div className="rounded-(--r-md) bg-s3 p-3.5 text-[12.5px] space-y-2">
        <Line label={t("billing.item")} value={picked.name} />
        {picked.kind === "coins"
          ? <Line label={t("billing.youGet")} value={picked.bonus_coins ? t("billing.coinsWithBonus", { n: fmtCoins(picked.coins), bonus: fmtCoins(picked.bonus_coins) }) : t("billing.coinsN", { n: fmtCoins(picked.coins) })} />
          : <Line label={t("billing.youGet")} value={t("billing.vipDays", { n: picked.vip_days })} />}
        <Line label={t("billing.payWith")} value={store.data.payment_method} />
        <Line label={t("billing.amount")} value={fmtMoney(picked.price_cents, picked.currency)} strong />
      </div>
      {store.data.billing_descriptor && <p className="mt-3 text-[11px] text-mut">{t("billing.descriptor", { name: store.data.billing_descriptor })}</p>}
      <button onClick={() => void pay()} disabled={busy}
        className="mt-4 w-full h-11 rounded-(--r-ctl) bg-primary text-on-primary text-[13.5px] font-bold flex items-center justify-center gap-2 pressable disabled:opacity-70">
        {busy ? <><Spinner size={16} />{t("billing.paying")}</> : <><CreditCard size={16} />{t("billing.confirmPay", { price: fmtMoney(picked.price_cents, picked.currency) })}</>}
      </button>
    </div>
  );

  return (
    <AccountFrame crumbs={[{ label: "个人中心", to: "/me" }, { label: t("billing.storeTitle") }]} title={t("billing.storeTitle")} sub={t("billing.storeSub")}>
        <div>
          {signedIn && <BalanceCard />}
          {resume && <p className="mt-3 text-[11.5px] text-mut">{t("billing.resumeHint", { title: resume.title, ep: resume.ep })}</p>}

          {store.isPending ? (
            <div className="flex justify-center py-12"><Spinner /></div>
          ) : store.isError ? (
            <div className="py-10">
              <Empty icon={<Coins size={24} />} title={store.error instanceof ApiError && store.error.code === "store_unavailable" ? t("billing.unavailable") : t("billing.loadFailed")}
                action={t("common.retry")} onAction={() => void store.refetch()} />
            </div>
          ) : (
            <>
              {store.data.sandbox && (
                <div className="mt-3 flex items-start gap-2 rounded-(--r-md) bg-star/10 p-3 text-[11.5px] text-star leading-relaxed">
                  <FlaskConical size={14} className="mt-0.5 shrink-0" />{t("billing.sandbox")}
                </div>
              )}

              {coins.length > 0 && (
                <Section title={t("billing.coinPacks")}>
                  <div className={`grid gap-2.5 ${wide ? "grid-cols-4" : "grid-cols-2"}`}>
                    {coins.map((p) => (
                      <button key={p.id} onClick={() => pick(p)}
                        className="relative rounded-(--r-md) bg-s2 p-3.5 text-left elev-1 pressable hover:bg-s3 outline-none focus-visible:ring-2 ring-primary">
                        {p.badge && <span className="absolute top-0 right-0 px-2 h-5 rounded-bl-(--r-sm) rounded-tr-(--r-md) bg-primary text-on-primary text-[10px] font-bold flex items-center">{p.badge}</span>}
                        <div className="flex items-baseline gap-1">
                          <Coins size={14} className="text-star self-center" />
                          <span className="text-[20px] font-bold text-on tabular-nums">{fmtCoins(p.coins)}</span>
                        </div>
                        <div className="h-5 mt-0.5">
                          {!!p.bonus_coins && <span className="text-[10.5px] text-star font-semibold">{t("billing.bonusChip", { n: fmtCoins(p.bonus_coins) })}</span>}
                        </div>
                        <div className="mt-2 text-[13px] font-semibold text-on tabular-nums">{fmtMoney(p.price_cents, p.currency)}</div>
                      </button>
                    ))}
                  </div>
                </Section>
              )}

              {vip.length > 0 && (
                <div ref={vipRef}>
                  <Section title={t("billing.vipPlans")}>
                    <div className="rounded-(--r-md) bg-s2 divide-y divide-s3 elev-1">
                      {vip.map((p) => (
                        <button key={p.id} onClick={() => pick(p)} className="w-full flex items-center gap-3 px-3.5 py-3 text-left pressable hover:bg-s3 outline-none focus-visible:ring-2 ring-primary">
                          <span className="w-9 h-9 rounded-full bg-star/15 text-star flex items-center justify-center shrink-0"><Crown size={17} /></span>
                          <div className="flex-1 min-w-0">
                            <div className="text-[13px] font-semibold text-on flex items-center gap-1.5">
                              {p.name}
                              {p.badge && <span className="px-1.5 h-4 rounded bg-primary text-on-primary text-[9.5px] font-bold inline-flex items-center">{p.badge}</span>}
                            </div>
                            <div className="text-[10.5px] text-mut">{t("billing.vipDays", { n: p.vip_days })} · {t("billing.vipPerk")}</div>
                          </div>
                          <span className="text-[13px] font-bold text-on tabular-nums">{fmtMoney(p.price_cents, p.currency)}</span>
                        </button>
                      ))}
                    </div>
                  </Section>
                </div>
              )}

              <div className="mt-5 rounded-(--r-sm) bg-s3/60 p-3 text-[11px] text-mut leading-relaxed space-y-1">
                <p>{t("billing.payMethod", { method: store.data.payment_method })}</p>
                {store.data.billing_descriptor && <p>{t("billing.descriptor", { name: store.data.billing_descriptor })}</p>}
                <p>{t("billing.notes1")}</p>
                <p>{t("billing.notes2")}</p>
                <p>
                  {t("billing.agree")}
                  <a href="/recharge-terms" target="_blank" rel="noreferrer" className="text-primary">{t("billing.rechargeTerms")}</a>
                </p>
              </div>
            </>
          )}
        </div>

      <Modal open={!!picked} onClose={closeOverlay} width={380}>{confirm}</Modal>
    </AccountFrame>
  );
}

function Line({ label, value, strong }: { label: string; value: string; strong?: boolean }) {
  return (
    <div className="flex items-center gap-3">
      <span className="text-mut">{label}</span>
      <span className={`ml-auto text-right ${strong ? "text-[15px] font-bold text-on" : "text-on"}`}>{value}</span>
    </div>
  );
}
