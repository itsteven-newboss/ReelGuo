import { unwrap, ApiError } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { Activity, BarChart3, Clapperboard, Coins, FileUp, Flag, Flame, Globe, History, KeyRound, Layers, LayoutGrid, LogOut, Megaphone, MessageSquareText, Receipt, ShieldCheck, Smartphone, Trophy, UserRound, Users } from "lucide-react";
import { useState, type FormEvent, type ReactNode } from "react";
import { AdminsPage } from "./pages/Admins";
import { AnnouncementsPage } from "./pages/Announcements";
import { AppReleasesPage } from "./pages/AppReleases";
import { AgeRulesPage } from "./pages/AgeRules";
import { AnalyticsPage } from "./pages/Analytics";
import { SystemStatusPage } from "./pages/SystemStatus";
import { AuditLogPage } from "./pages/AuditLog";
import { BlockedCountriesPage } from "./pages/BlockedCountries";
import { ChannelsPage } from "./pages/Channels";
import { CommentsPage } from "./pages/Comments";
import { ImportPage } from "./pages/Import";
import { MembersPage } from "./pages/Members";
import { OrdersPage } from "./pages/Orders";
import { ProductsPage } from "./pages/Products";
import { PostsAdminPage } from "./pages/Posts";
import { RanksAdminPage } from "./pages/Ranks";
import { ReportsPage } from "./pages/Reports";
import { SeriesEditor } from "./pages/SeriesEditor";
import { SeriesListPage } from "./pages/SeriesList";
import { TopicsPage } from "./pages/Topics";
import { api, setSession, signOut, useSession, type AdminSession } from "./session";

type Section = "status" | "analytics" | "series" | "import" | "posts" | "topics" | "announcements" | "ranks" | "channels" | "comments" | "reports" | "members" | "orders" | "products" | "geo" | "age" | "app" | "admins" | "audit";

const NAV: { key: Section; label: string; icon: ReactNode }[] = [
  { key: "status", label: "系统状态", icon: <Activity size={17} /> },
  { key: "analytics", label: "数据概览", icon: <BarChart3 size={17} /> },
  { key: "series", label: "内容管理", icon: <Clapperboard size={17} /> },
  { key: "import", label: "批量导入", icon: <FileUp size={17} /> },
  { key: "posts", label: "吃瓜文章", icon: <Flame size={17} /> },
  { key: "topics", label: "专题", icon: <Layers size={17} /> },
  { key: "announcements", label: "公告", icon: <Megaphone size={17} /> },
  { key: "ranks", label: "榜单", icon: <Trophy size={17} /> },
  { key: "channels", label: "频道", icon: <LayoutGrid size={17} /> },
  { key: "comments", label: "评论审核", icon: <MessageSquareText size={17} /> },
  { key: "reports", label: "举报", icon: <Flag size={17} /> },
  { key: "members", label: "会员", icon: <UserRound size={17} /> },
  { key: "orders", label: "订单与营收", icon: <Receipt size={17} /> },
  { key: "products", label: "商品", icon: <Coins size={17} /> },
  { key: "geo", label: "地区屏蔽", icon: <Globe size={17} /> },
  { key: "age", label: "年龄验证", icon: <ShieldCheck size={17} /> },
  { key: "app", label: "App 版本", icon: <Smartphone size={17} /> },
  { key: "admins", label: "管理员", icon: <Users size={17} /> },
  { key: "audit", label: "操作日志", icon: <History size={17} /> },
];

export default function App() {
  const session = useSession();
  if (session.isPending) {
    return <div className="h-full flex items-center justify-center bg-canvas"><Spinner /></div>;
  }
  return session.data ? <Layout session={session.data} /> : <Login />;
}

function Layout({ session }: { session: AdminSession }) {
  const [section, setSection] = useState<Section>("series");
  // Queue sizes for the nav badges; refreshed every minute.
  const summary = useQuery({
    queryKey: ["admin", "moderation-summary"],
    queryFn: async () => unwrap(await api.GET("/api/admin/moderation/summary")),
    refetchInterval: 60_000,
  });
  const badges: Partial<Record<Section, { count: number; urgent: boolean }>> = {
    comments: { count: summary.data?.pending_comments ?? 0, urgent: false },
    reports: { count: summary.data?.open_reports ?? 0, urgent: (summary.data?.urgent_reports ?? 0) > 0 },
  };
  // "new" opens an empty editor; a number opens that series.
  const [editing, setEditing] = useState<number | "new" | null>(null);

  const open = (key: Section) => {
    setSection(key);
    setEditing(null);
  };

  let page: ReactNode;
  if (section === "series" && editing !== null) page = <SeriesEditor id={editing} onBack={() => setEditing(null)} onCreated={(id) => setEditing(id)} />;
  else if (section === "series") page = <SeriesListPage onOpen={setEditing} />;
  else if (section === "status") page = <SystemStatusPage />;
  else if (section === "analytics") page = <AnalyticsPage />;
  else if (section === "import") page = <ImportPage onOpenSeries={(id) => { setSection("series"); setEditing(id); }} />;
  else if (section === "posts") page = <PostsAdminPage />;
  else if (section === "topics") page = <TopicsPage />;
  else if (section === "announcements") page = <AnnouncementsPage />;
  else if (section === "ranks") page = <RanksAdminPage />;
  else if (section === "channels") page = <ChannelsPage />;
  else if (section === "comments") page = <CommentsPage />;
  else if (section === "reports") page = <ReportsPage />;
  else if (section === "members") page = <MembersPage />;
  else if (section === "orders") page = <OrdersPage />;
  else if (section === "products") page = <ProductsPage />;
  else if (section === "geo") page = <BlockedCountriesPage />;
  else if (section === "age") page = <AgeRulesPage />;
  else if (section === "app") page = <AppReleasesPage />;
  else if (section === "admins") page = <AdminsPage currentUserId={session.id} />;
  else page = <AuditLogPage />;

  return (
    <div className="h-full flex bg-canvas text-on">
      <aside className="w-[220px] shrink-0 flex flex-col p-3 gap-1">
        <div className="flex items-center gap-2 px-2.5 py-3">
          <img src="/icon.svg" alt="" className="w-6 h-6 rounded-md" />
          <span className="text-[14px] font-bold">ReelGuo</span>
          <span className="text-[11px] text-dim">后台</span>
        </div>
        {NAV.map((item) => (
          <button
            key={item.key}
            onClick={() => open(item.key)}
            className={`relative flex items-center gap-3 h-10 px-3 rounded-(--r-md) text-[13px] ${section === item.key ? "bg-s3 font-semibold" : "text-mut hover:bg-(--p-hover) hover:text-on"}`}
          >
            {section === item.key && <span className="absolute left-0 top-[9px] bottom-[9px] w-[3px] rounded-sm bg-primary" />}
            {item.icon}
            {item.label}
            {!!badges[item.key]?.count && (
              <span className={`ml-auto min-w-5 h-5 px-1.5 rounded-full text-[10.5px] font-bold flex items-center justify-center tabular-nums ${badges[item.key]!.urgent ? "bg-danger text-on-danger" : "bg-s4 text-on"}`}>
                {badges[item.key]!.count > 99 ? "99+" : badges[item.key]!.count}
              </span>
            )}
          </button>
        ))}
        <div className="mt-auto px-2.5 py-2 text-[11px] text-dim">
          已登录：<span className="text-mut">{session.nickname || session.username}</span>
        </div>
        <button onClick={() => void signOut()} className="flex items-center gap-3 h-9 px-3 rounded-(--r-md) text-[12.5px] text-mut hover:bg-(--p-hover) hover:text-on">
          <LogOut size={16} />
          退出
        </button>
      </aside>
      <main className="flex-1 min-w-0 my-3 mr-3 rounded-[13px] bg-bg overflow-auto">
        <div className="p-6 max-w-[1200px]">{page}</div>
      </main>
    </div>
  );
}

function Login() {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setSession(unwrap(await api.POST("/api/admin/auth/login", { body: { username: username.trim(), password } })));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "无法连接服务器");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="h-full flex items-center justify-center bg-canvas p-6">
      <form onSubmit={submit} className="w-full max-w-[360px] bg-s2 rounded-(--r-lg) p-6 elev-3 a-scale-in">
        <div className="w-12 h-12 rounded-(--r-md) bg-pc text-on-pc flex items-center justify-center mb-4">
          <KeyRound size={22} />
        </div>
        <h1 className="text-[18px] font-bold">ReelGuo 后台</h1>
        <p className="text-[12px] text-mut mt-1">所有操作都会以你的账号记录在操作日志中。</p>

        <label className="block mt-5 text-[12px] text-mut">账号</label>
        <input required value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username"
          className="mt-1 w-full h-10 px-3 rounded-(--r-sm) bg-s3 text-on text-[14px] outline-none focus:ring-2 ring-primary" />

        <label className="block mt-3 text-[12px] text-mut">密码</label>
        <input required type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password"
          className="mt-1 w-full h-10 px-3 rounded-(--r-sm) bg-s3 text-on text-[14px] outline-none focus:ring-2 ring-primary" />

        {error && <p className="mt-3 text-[12px] text-danger">{error}</p>}

        <button disabled={busy} className="mt-5 w-full h-10 rounded-(--r-ctl) bg-primary text-on-primary text-[13px] font-bold pressable disabled:opacity-60">
          {busy ? "登录中…" : "登录"}
        </button>
      </form>
    </div>
  );
}
