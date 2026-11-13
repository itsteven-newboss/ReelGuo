import { useQueryClient } from "@tanstack/react-query";
import { Brush, ChevronRight, Download, FileText, Heart, Info, Smartphone, VolumeX } from "lucide-react";
import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { nativeApp } from "../native";
import { Breadcrumb, Container, PageTitle } from "../portal/kit";
import { href, navigate } from "../router";
import { useLibrary } from "../state/library";
import { useUi } from "../state/ui";
import { Modal, Toggle } from "../ui/Overlays";
import { LEGAL_PATHS, type LegalPath } from "./Legal";

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <>
      <div className="text-[12px] font-semibold text-dim tracking-wide px-1 mt-6 mb-2">{title}</div>
      <div className="rounded-[14px] bg-[#141414] border border-white/[.06] divide-y divide-white/[.06]">{children}</div>
    </>
  );
}

function Row({ icon, title, sub, right, onClick }: { icon: ReactNode; title: string; sub?: string; right?: ReactNode; onClick?: () => void }) {
  const body = (
    <>
      <span className="w-9 h-9 rounded-full bg-white/[.06] flex items-center justify-center text-primary shrink-0">{icon}</span>
      <div className="flex-1 min-w-0 text-left">
        <div className="text-[14px] text-on">{title}</div>
        {sub && <div className="text-[12px] text-mut mt-0.5">{sub}</div>}
      </div>
      {right}
    </>
  );
  return onClick
    ? <button onClick={onClick} className="w-full flex items-center gap-3 px-4 py-3.5 hover:bg-white/[.02]">{body}</button>
    : <div className="flex items-center gap-3 px-4 py-3.5">{body}</div>;
}

/** 设置: playback, data on this device, about. */
export function SettingsPage() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const { muteOnStartup, setMuteOnStartup, toast } = useUi();
  const { favs, history, clearAll } = useLibrary();
  const [clearOpen, setClearOpen] = useState(false);

  const clearCaches = async () => {
    qc.clear();
    clearAll();
    try {
      for (const key of await caches.keys()) await caches.delete(key);
    } catch { /* Cache Storage unavailable */ }
    setClearOpen(false);
    toast(t("settings.cleared"), "ok");
  };

  return (
    <Container>
      <Breadcrumb items={[{ label: "ReelGuo", to: "/" }, { label: "个人中心", to: href.me() }, { label: t("settings.title") }]} />
      <PageTitle sub={t("settings.sub")}>{t("settings.title")}</PageTitle>
      <div className="max-w-[880px]">
        <Section title={t("settings.playback")}>
          <Row icon={<VolumeX size={16} />} title={t("settings.muteOnStartup")} sub={t("settings.muteOnStartupSub")}
            right={<Toggle on={muteOnStartup} onChange={setMuteOnStartup} label={t("settings.muteOnStartup")} />} />
        </Section>

        <Section title={t("settings.localData")}>
          <Row icon={<Heart size={16} />} title={t("settings.library")} sub={t("settings.librarySub", { favs: favs.length, history: history.length })} />
          <Row icon={<Brush size={16} />} title={t("settings.clear")} sub={t("settings.clearSub")}
            right={<ChevronRight size={15} className="text-mut" />} onClick={() => setClearOpen(true)} />
        </Section>

        <Section title={t("settings.about")}>
          <Row icon={<Info size={16} />} title="ReelGuo" sub={t("settings.version", { version: __APP_VERSION__ })} />
          {nativeApp ? (
            <Row icon={<Smartphone size={16} />} title={t("settings.appVersion")}
              sub={t(nativeApp.platform === "android" ? "settings.appAndroid" : "settings.appDesktop", { version: nativeApp.version })} />
          ) : (
            <Row icon={<Download size={16} />} title={t("download.title")} sub={t("settings.downloadSub")}
              right={<ChevronRight size={15} className="text-mut" />} onClick={() => navigate(href.download())} />
          )}
          {(Object.keys(LEGAL_PATHS) as LegalPath[]).map((path) => (
            <Row key={path} icon={<FileText size={16} />} title={t(LEGAL_PATHS[path])}
              right={<ChevronRight size={15} className="text-mut" />} onClick={() => window.open(path, "_blank", "noopener")} />
          ))}
        </Section>

        <p className="mt-5 rounded-[12px] bg-white/[.04] p-4 text-[12.5px] text-mut leading-relaxed">{t("settings.adultNotice")}</p>
      </div>

      <Modal open={clearOpen} onClose={() => setClearOpen(false)}>
        <div className="p-5">
          <div className="text-[15px] font-bold text-on">{t("settings.clearTitle")}</div>
          <p className="text-[12px] text-mut mt-2 leading-relaxed">{t("settings.clearBody")}</p>
          <div className="flex gap-2 mt-5">
            <button onClick={() => setClearOpen(false)} className="flex-1 h-9 rounded-(--r-ctl) bg-s4 text-[12.5px] text-on pressable">{t("common.cancel")}</button>
            <button onClick={() => void clearCaches()} className="flex-1 h-9 rounded-(--r-ctl) bg-primary text-on-primary text-[12.5px] font-bold pressable">{t("settings.clearConfirm")}</button>
          </div>
        </div>
      </Modal>
    </Container>
  );
}
