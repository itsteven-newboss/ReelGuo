import { useQueries } from "@tanstack/react-query";
import { Clock, LogOut, Settings, Star, UserRound } from "lucide-react";
import { useEffect, useState } from "react";
import { seriesQuery, useAppConfig, useSite, type VideoCard } from "../api";
import { WalletStrip } from "../billing/WalletPage";
import { nativeApp } from "../native";
import { fmtTime } from "../player/format";
import { href, Link } from "../router";
import { Avatar } from "../social/Avatar";
import { clearWatchHistory, useAuth } from "../state/auth";
import { useLibrary } from "../state/library";
import { useUi } from "../state/ui";
import { Breadcrumb, Container, EmptyState, Panel, PosterCard, PosterSkeleton, UnderlineTabs } from "./kit";

/** 个人中心: account card, wallet, then favourites and watch history (kept on the device, synced when signed in). */
export function AccountPage() {
  const [tab, setTab] = useState<"fav" | "history">("fav");
  const { favs, likes, history } = useLibrary();
  const { status, user, follows, openLogin, logout } = useAuth();
  const monetization = useAppConfig().data?.monetization ?? false;
  const signedIn = status === "signed_in" && !!user;
  const [confirmLogout, setConfirmLogout] = useState(false);

  const ids = (tab === "fav" ? favs : history.map((h) => h.id)).slice(0, 40);
  const results = useQueries({ queries: ids.map((id) => ({ ...seriesQuery(id), retry: false })) });
  const cards: (VideoCard & { progress?: string })[] = results.flatMap((r) => (r.data ? [{
    id: r.data.id, title: r.data.title, cover: r.data.cover, description: r.data.description, tags: r.data.tags, score: r.data.score,
    episode_count: r.data.episode_count, total_episodes: r.data.total_episodes, hot: r.data.counts.hot, like_count: r.data.counts.like,
    collect_count: r.data.counts.collect, comment_count: r.data.counts.comment, is_finished: r.data.is_finished, is_original: r.data.is_original,
    author: r.data.author, published_at: r.data.published_at, last_episode_at: r.data.last_episode_at ?? null,
  }] : []));
  const loading = results.some((r) => r.isPending);

  return (
    <Container>
      <Breadcrumb items={[{ label: "ReelGuo", to: "/" }, { label: "个人中心" }]} />
      <div className="mt-4 grid gap-4 lg:grid-cols-[minmax(0,1fr)_380px]">
        <Panel className="p-5 lg:p-6">
          {signedIn ? (
            <div className="flex items-center gap-4">
              <Avatar size={64} name={user.nickname} color={user.avatar_color} />
              <div className="flex-1 min-w-0">
                <div className="text-[20px] font-bold text-on truncate">{user.nickname}</div>
                <div className="text-[12.5px] text-mut mt-0.5">@{user.username}</div>
                <div className="text-[12.5px] text-dim mt-1 truncate">{user.sign || "这个人很懒，什么都没写"}</div>
              </div>
              <Link to={href.settings()} className="h-9 px-4 rounded-full bg-white/[.06] hover:bg-white/10 text-[13px] text-on flex items-center gap-1.5"><Settings size={14} />设置</Link>
            </div>
          ) : (
            <div className="flex items-center gap-4">
              <span className="w-16 h-16 rounded-full bg-white/[.06] flex items-center justify-center text-mut"><UserRound size={28} /></span>
              <div className="flex-1 min-w-0">
                <div className="text-[20px] font-bold text-on">登录 / 注册</div>
                <div className="text-[12.5px] text-mut mt-1">登录后可使用收藏、评论，并在不同设备间同步观看记录</div>
              </div>
              <button onClick={openLogin} disabled={status === "booting"} className="h-10 px-6 rounded-full bg-primary text-on-primary text-[14px] font-bold">登录</button>
            </div>
          )}
          <div className="mt-6 grid grid-cols-4 text-center">
            {([["点赞", likes.length], ["收藏", favs.length], ["关注", follows.length], ["观看", history.length]] as const).map(([label, n]) => (
              <div key={label}>
                <div className="text-[20px] font-bold text-on tabular-nums">{n}</div>
                <div className="text-[12px] text-mut mt-0.5">{label}</div>
              </div>
            ))}
          </div>
        </Panel>
        <div className="flex flex-col gap-3">
          {signedIn && monetization && <WalletStrip />}
          {!nativeApp && (
            <Link to={href.download()} className="rounded-[14px] bg-[#141414] border border-white/[.06] p-4 flex items-center gap-3 hover:border-primary/40">
              <img src="/icon.svg" alt="" className="w-10 h-10 rounded-[11px]" />
              <span className="flex-1"><span className="block text-[14px] font-bold text-on">下载 ReelGuo App</span><span className="block text-[12px] text-dim">安卓 · Windows · iPhone 添加到主屏幕</span></span>
            </Link>
          )}
          {signedIn && (
            <button onClick={() => (confirmLogout ? void logout().then(() => setConfirmLogout(false)) : setConfirmLogout(true))} onBlur={() => setConfirmLogout(false)}
              className={`h-11 rounded-[12px] text-[13.5px] font-semibold flex items-center justify-center gap-1.5 ${confirmLogout ? "bg-danger text-on-danger" : "bg-[#141414] border border-white/[.06] text-danger"}`}>
              <LogOut size={15} />{confirmLogout ? "确认退出" : "退出登录"}
            </button>
          )}
        </div>
      </div>

      <UnderlineTabs tabs={[["fav", "我的收藏"], ["history", "观看历史"]] as const} active={tab} onChange={setTab} />
      <div className="mt-6">
        {cards.length > 0 ? (
          <div className="grid grid-cols-2 lg:grid-cols-4 gap-x-3 gap-y-6 lg:gap-x-[18px] lg:gap-y-8">
            {cards.map((v) => {
              const h = tab === "history" ? history.find((x) => x.id === v.id) : undefined;
              return (
                <div key={v.id}>
                  <PosterCard v={v} />
                  {h && <div className="mt-1 text-[12px] text-primary">看到第 {h.ep} 集 · {fmtTime(h.t)}</div>}
                </div>
              );
            })}
          </div>
        ) : loading ? <PosterSkeleton count={4} /> : tab === "fav"
          ? <EmptyState title="还没有收藏" sub="在剧集页点「收藏」，之后就能在这里找到" />
          : <EmptyState title="还没有观看记录" sub="看过的剧集会出现在这里" />}
        {cards.length > 0 && tab === "history" && (
          <div className="flex justify-center mt-6">
            <button onClick={() => void clearWatchHistory()} className="h-9 px-6 rounded-full bg-white/[.06] text-[13px] text-mut hover:text-on flex items-center gap-1.5"><Clock size={13} />清空观看历史</button>
          </div>
        )}
      </div>
    </Container>
  );
}

/* ---------------------------------------------------------------------------------------------
 * 地址收藏提醒: once a day, the official addresses, so viewers can find the site after a domain change.
 * ------------------------------------------------------------------------------------------- */

const REMINDER_KEY = "rg.addressReminder.v1";
const AUTO_CLOSE_S = 30;

export function AddressReminder() {
  const site = useSite();
  const toast = useUi((s) => s.toast);
  const [open, setOpen] = useState(false);
  const [left, setLeft] = useState(AUTO_CLOSE_S);
  const s = site.data;
  const worth = !!s && (s.mirrors.length > 0 || !!s.permanent_domain || !!s.telegram || !!s.email);

  useEffect(() => {
    if (!worth || nativeApp || window.location.pathname === "/go-home") return;
    const today = new Date().toDateString();
    try {
      if (localStorage.getItem(REMINDER_KEY) === today) return;
      localStorage.setItem(REMINDER_KEY, today);
    } catch { /* storage unavailable: show it anyway */ }
    setOpen(true);
  }, [worth]);

  useEffect(() => {
    if (!open) return undefined;
    const t = window.setInterval(() => setLeft((n) => {
      if (n <= 1) { setOpen(false); return 0; }
      return n - 1;
    }), 1000);
    return () => window.clearInterval(t);
  }, [open]);

  if (!open || !s) return null;
  const latest = s.mirrors[0]?.replace(/^https?:\/\//, "") ?? window.location.host;
  const copyAll = () => {
    const text = [`ReelGuo 最新地址：${latest}`, s.permanent_domain && `永久域名：${s.permanent_domain}`, s.telegram && `官方交流群：${s.telegram}`,
      s.email && `官方邮箱：${s.email}`, `回家的路：${window.location.origin}${href.wayHome()}`].filter(Boolean).join("\n");
    navigator.clipboard?.writeText(text).then(() => toast("地址已复制，请妥善保存", "ok")).catch(() => toast(text));
  };
  const row = (label: string, value: string, accent = false) => (
    <div className="flex flex-wrap gap-x-3 gap-y-0.5 text-[14px] lg:text-[15px]">
      <span className={`font-bold ${accent ? "text-primary" : "text-on"}`}>{label}：</span>
      <span className={accent ? "text-primary font-semibold break-all" : "text-on/90 break-all"}>{value}</span>
    </div>
  );
  return (
    <div className="fixed inset-0 z-[70] flex items-center justify-center p-4" role="dialog" aria-modal="true" aria-labelledby="reminder-title">
      <div className="absolute inset-0 bg-black/70" onClick={() => setOpen(false)} />
      <div className="relative w-full max-w-[680px] rounded-[16px] bg-[#1a1a1a] border border-white/10 overflow-hidden a-scale-in">
        <div className="h-[52px] px-5 flex items-center bg-white/[.04] border-b border-white/[.06]">
          <span className="text-[13px] text-mut">{left}秒后将会自动关闭</span>
          <button onClick={() => setOpen(false)} aria-label="关闭" className="ml-auto text-[22px] leading-none text-on/80 hover:text-on">×</button>
        </div>
        <div className="p-5 lg:p-7">
          <div className="flex items-center gap-4">
            <h2 id="reminder-title" className="text-[22px] lg:text-[26px] font-extrabold text-primary">地址收藏提醒</h2>
            <button onClick={copyAll} className="ml-auto h-10 px-4 rounded-[10px] border border-primary/60 bg-primary/10 text-primary text-[13px] font-bold">复制全部地址</button>
          </div>
          <div className="mt-5 space-y-3">
            {row("最新地址", latest)}
            {s.permanent_domain && row("官方永久域名", s.permanent_domain, true)}
            {s.telegram && row("官方交流群", s.telegram)}
            {s.email && row("官方邮箱", `${s.email}（发送邮件，自动回复最新地址）`)}
          </div>
          <div className="my-5 h-px bg-white/[.08]" />
          <p className="text-[14px] font-bold text-primary">域名可能发生变更，请认准官方发布信息！</p>
          <p className="mt-2 text-[13.5px] text-mut">请保存「回家的路」地址发布页，方便下次快速访问！</p>
          <div className="mt-5 grid gap-3 sm:grid-cols-2">
            <Link to={href.wayHome()} onClick={() => setOpen(false)} className="h-12 rounded-[10px] bg-primary text-on-primary text-[15px] font-bold flex items-center justify-center">回家的路</Link>
            <button onClick={copyAll} className="h-12 rounded-[10px] border border-primary/60 text-primary text-[15px] font-bold flex items-center justify-center gap-1.5"><Star size={15} />保存地址</button>
          </div>
        </div>
      </div>
    </div>
  );
}
