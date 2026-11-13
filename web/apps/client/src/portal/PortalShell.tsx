import { useEffect } from "react";
import { PayResultPage } from "../billing/PayResult";
import { StorePage } from "../billing/StorePage";
import { WalletPage } from "../billing/WalletPage";
import { DownloadPage } from "../pages/Download";
import { PostPage } from "../pages/Posts";
import { SettingsPage } from "../pages/Settings";
import { installRouter, match, useLocation } from "../router";
import { LoginSheet } from "../social/LoginSheet";
import { ReportSheet } from "../social/Report";
import { installBackHandler } from "../state/nav";
import { ToastHost } from "../ui/Overlays";
import { AccountPage, AddressReminder } from "./Account";
import { FloatDock, SiteFooter, SiteHeader } from "./chrome";
import { HomePage } from "./Home";
import { Breadcrumb, Container, EmptyState } from "./kit";
import { ChannelPage, PostsPage, RanksPage, SearchPage, TopicPage, TopicsPage, WayHomePage } from "./Pages";
import { SeriesPage } from "./SeriesPage";
import type { RankKind } from "../api";

/** URL → page. Unknown paths get a not-found page inside the normal layout. */
function Route() {
  const { path, query } = useLocation();
  let m: Record<string, string> | null;
  if (path === "/") return <HomePage />;
  if ((m = match("/c/:slug", path))) return <ChannelPage key={m.slug} slug={m.slug!} />;
  if (path === "/topics") return <TopicsPage />;
  if ((m = match("/topics/:id", path))) return <TopicPage key={m.id} id={Number(m.id)} />;
  if (path === "/ranks") return <RanksPage kind="hot" />;
  if ((m = match("/ranks/:kind", path))) return <RanksPage kind={(["hot", "recommend", "potential"].includes(m.kind!) ? m.kind : "hot") as RankKind} />;
  if (path === "/posts") return <PostsPage />;
  if ((m = match("/post/:id", path))) return <PostPage key={m.id} id={Number(m.id)} />;
  if (path === "/go-home") return <WayHomePage />;
  if (path === "/download") return <DownloadPage />;
  if (path === "/search") return <SearchPage />;
  const video = /^\/video\/(\d+)(?:\/ep-(\d+))?\/?$/.exec(path);
  if (video) return <SeriesPage key={video[1]} id={Number(video[1])} ep={video[2] ? Number(video[2]) : undefined} />;
  if (path === "/me") return <AccountPage />;
  if (path === "/me/wallet") return <WalletPage />;
  if (path === "/store") return <StorePage />;
  if (path === "/pay/result") return <PayResultPage key={query.get("order") ?? ""} orderNo={query.get("order") ?? ""} />;
  if (path === "/settings") return <SettingsPage />;
  return (
    <Container>
      <Breadcrumb items={[{ label: "ReelGuo", to: "/" }, { label: "页面不存在" }]} />
      <EmptyState title="页面不存在" sub="链接可能已失效，回到首页看看吧" />
    </Container>
  );
}

/**
 * The web app's layout, as on the reference site: a top header with the channels as navigation, the page, and a
 * footer with the official addresses. Sheets and dialogs that any page can open live here too.
 */
export function PortalShell() {
  useEffect(() => installRouter(), []);
  useEffect(() => installBackHandler(), []);
  // Changing episode (/video/1/ep-2 → /ep-3) keeps the page mounted, so the player carries on.
  const key = useLocation((s) => s.path.replace(/\/ep-\d+\/?$/, ""));
  // The desktop header lies over the page: the home hero runs under it, other pages start below it.
  return (
    <div className="relative min-h-dvh flex flex-col bg-[#0b0b0d] text-white">
      <SiteHeader />
      <main key={key} className={`flex-1 pb-4 pt-14 ${key === "/" ? "lg:pt-0" : "lg:pt-[76px]"}`}>
        <Route />
      </main>
      <SiteFooter />
      <FloatDock />
      <LoginSheet wide />
      <ReportSheet wide />
      <AddressReminder />
      <ToastHost />
    </div>
  );
}
