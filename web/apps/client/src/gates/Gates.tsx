import { Globe, ShieldCheck } from "lucide-react";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { LoginSheet } from "../social/LoginSheet";
import { useAuth } from "../state/auth";
import { useSession } from "../state/session";
import { ToastHost } from "../ui/Overlays";
import { LegalLinks } from "../pages/Legal";

/** Shown instead of the app when the API answers 451 for this visitor's country. */
export function RegionBlocked() {
  const { t } = useTranslation();
  return (
    <div className="absolute inset-0 bg-canvas flex items-center justify-center p-6">
      <div className="max-w-[340px] text-center a-rise">
        <div className="w-16 h-16 mx-auto rounded-(--r-md) tonal flex items-center justify-center text-primary mb-4">
          <Globe size={28} />
        </div>
        <h1 className="text-[17px] font-bold text-on">{t("region.title")}</h1>
        <p className="text-[12px] text-mut mt-2">{t("region.sub")}</p>
      </div>
    </div>
  );
}

/** 18+ confirmation on first visit, laid out like the reference: logo, two lines, enter / leave. Nothing renders behind it. */
export function AgeGate() {
  const confirmAge = useSession((s) => s.confirmAge);
  // While the gate shows, the page's server-rendered content (#seo) sits beneath it, as search engines expect of an
  // age interstitial; it is hidden again once the app takes over.
  useEffect(() => {
    document.documentElement.classList.add("gate-shown");
    return () => document.documentElement.classList.remove("gate-shown");
  }, []);
  return (
    <div className="fixed inset-0 flex items-center justify-center p-6" role="dialog" aria-modal="true" aria-labelledby="age-title"
      style={{ background: "radial-gradient(60% 45% at 50% 0%, color-mix(in srgb, var(--p-primary) 14%, transparent), transparent 70%), #0d0d0d" }}>
      <div className="w-full max-w-[500px] text-center a-rise">
        <div className="flex items-center justify-center gap-3">
          <img src="/icon.svg" alt="" className="w-14 h-14 rounded-[16px]" />
          <span className="text-left leading-none">
            <span className="block text-[26px] font-extrabold text-on">ReelGuo</span>
            <span className="block mt-1.5 text-[12px] text-mut tracking-[.2em]">AI 短剧</span>
          </span>
        </div>
        <h1 id="age-title" className="mt-10 text-[20px] lg:text-[22px] text-on leading-relaxed">本站含有成人内容，<br />仅限年满 18 周岁的成年人访问。</h1>
        <button onClick={confirmAge}
          className="mt-8 w-full h-[58px] rounded-full bg-primary text-on-primary text-[16px] font-bold ring-2 ring-white ring-offset-2 ring-offset-[#0d0d0d]">
          我已年满 18 周岁，进入网站
        </button>
        <button onClick={() => window.location.replace("https://www.google.com")}
          className="mt-4 w-full h-[58px] rounded-full bg-white/[.04] border border-white/[.08] text-mut text-[16px]">
          未满 18 周岁，离开
        </button>
        <p className="mt-8 text-[13px] text-dim">进入即表示您确认已年满 18 周岁，并同意遵守当地法律法规。</p>
        <LegalLinks className="mt-3 justify-center" />
      </div>
    </div>
  );
}

/**
 * Countries where the team set a strict age rule (admin: 年龄验证): content opens only for a signed-in account whose age
 * has been verified. The API enforces it too (403 age_verification_required); this explains it and offers sign-in.
 */
export function AgeVerificationRequired() {
  const { t } = useTranslation();
  const { user, openLogin, logout } = useAuth();
  const wide = window.matchMedia("(min-width: 900px)").matches;
  return (
    <div className="absolute inset-0 bg-canvas flex items-center justify-center p-6">
      <div className="w-full max-w-[360px] bg-s2 rounded-(--r-lg) p-6 elev-3 a-scale-in">
        <div className="w-14 h-14 rounded-(--r-md) bg-pc text-on-pc flex items-center justify-center mb-4">
          <ShieldCheck size={26} />
        </div>
        <h1 className="text-[18px] font-bold text-on">{t("ageCheck.title")}</h1>
        <p className="text-[13px] text-mut mt-2 leading-relaxed">{t("ageCheck.body")}</p>
        {user && <p className="text-[12px] text-star mt-2">{t("ageCheck.unverified", { name: user.username })}</p>}
        <p className="text-[11px] text-dim mt-2">{t("ageCheck.sub")}</p>
        <LegalLinks className="mt-3" />
        {user ? (
          <button onClick={() => void logout().then(openLogin)} className="mt-6 w-full h-11 rounded-(--r-ctl) bg-primary text-on-primary text-[14px] font-bold pressable">
            {t("ageCheck.switchAccount")}
          </button>
        ) : (
          <button onClick={openLogin} className="mt-6 w-full h-11 rounded-(--r-ctl) bg-primary text-on-primary text-[14px] font-bold pressable">
            {t("ageCheck.login")}
          </button>
        )}
        <p className="text-[11px] text-dim mt-3 text-center">{t("ageCheck.soon")}</p>
      </div>
      <LoginSheet wide={wide} />
      <ToastHost />
    </div>
  );
}
