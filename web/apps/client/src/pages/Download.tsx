import { unwrap, type Schemas } from "@reelguo/api-client";
import { Empty, Spinner } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { Apple, Download as DownloadIcon, Monitor, Smartphone } from "lucide-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../api";
import { fmtDay } from "../billing/money";
import { Breadcrumb, Container, PageTitle } from "../portal/kit";

type Release = Schemas["AppReleaseDto"];

const PLATFORMS: Record<string, { icon: ReactNode; name: string; tip: string; button: string }> = {
  android: { icon: <Smartphone size={20} />, name: "download.android", tip: "download.androidTip", button: "download.getApk" },
  "windows-x86_64": { icon: <Monitor size={20} />, name: "download.windows", tip: "download.windowsTip", button: "download.getInstaller" },
  "darwin-aarch64": { icon: <Apple size={20} />, name: "download.macArm", tip: "download.macTip", button: "download.getMac" },
  "darwin-x86_64": { icon: <Apple size={20} />, name: "download.macIntel", tip: "download.macTip", button: "download.getMac" },
  "linux-x86_64": { icon: <Monitor size={20} />, name: "download.linux", tip: "download.linuxTip", button: "download.getLinux" },
};

/** 下载 App: the newest published build per platform (Admin → App 版本), plus how to use the web app on iPhone. */
export function DownloadPage() {
  const { t } = useTranslation();
  const releases = useQuery({
    queryKey: ["app-releases"],
    queryFn: async () => unwrap(await api.GET("/api/v1/app/releases")),
    staleTime: 5 * 60_000,
  });

  return (
    <Container>
      <Breadcrumb items={[{ label: "ReelGuo", to: "/" }, { label: t("download.title") }]} />
      <PageTitle sub={t("download.sub")}>{t("download.title")}</PageTitle>
      <div className="mt-6">
        <div className="space-y-3 max-w-[880px]">
          {releases.isPending ? <div className="flex justify-center py-12"><Spinner /></div> : (releases.data ?? []).length === 0 ? (
            <div className="py-8"><Empty icon={<DownloadIcon size={24} />} title={t("download.soon")} sub={t("download.soonSub")} /></div>
          ) : releases.data!.map((r) => <ReleaseCard key={r.platform} release={r} />)}

          <div className="rounded-(--r-md) bg-s2 p-4 flex gap-3">
            <span className="w-10 h-10 rounded-full bg-s3 flex items-center justify-center text-mut shrink-0"><Apple size={20} /></span>
            <div className="min-w-0">
              <div className="text-[13.5px] font-semibold text-on">{t("download.ios")}</div>
              <p className="text-[11.5px] text-mut leading-relaxed mt-0.5">{t("download.iosTip")}</p>
            </div>
          </div>
          <p className="text-[11px] text-dim leading-relaxed px-1">{t("download.notice")}</p>
        </div>
      </div>
    </Container>
  );
}

function ReleaseCard({ release: r }: { release: Release }) {
  const { t } = useTranslation();
  const p = PLATFORMS[r.platform];
  if (!p) return null;
  return (
    <div className="rounded-(--r-md) bg-s2 p-4 elev-1">
      <div className="flex items-center gap-3">
        <span className="w-10 h-10 rounded-full bg-pc text-on-pc flex items-center justify-center shrink-0">{p.icon}</span>
        <div className="flex-1 min-w-0">
          <div className="text-[13.5px] font-semibold text-on">{t(p.name)}</div>
          <div className="text-[11px] text-mut">{t("download.version", { version: r.version })}{r.published_at && ` · ${fmtDay(r.published_at)}`}</div>
        </div>
        <a href={r.url} rel="noreferrer" className="h-9 px-4 rounded-full bg-primary text-on-primary text-[12.5px] font-bold flex items-center gap-1.5 pressable shrink-0">
          <DownloadIcon size={14} />{t(p.button)}
        </a>
      </div>
      {r.notes && <p className="mt-3 text-[11.5px] text-mut whitespace-pre-wrap leading-relaxed">{r.notes}</p>}
      <p className="mt-2 text-[11px] text-dim leading-relaxed">{t(p.tip)}</p>
      {r.sha256 && <p className="mt-1.5 text-[10px] text-dim font-mono break-all">SHA-256 {r.sha256}</p>}
    </div>
  );
}
