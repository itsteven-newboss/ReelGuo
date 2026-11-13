import { Empty, Spinner } from "@reelguo/ui";
import { WifiOff } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useAppConfig } from "./api";
import { useEffect } from "react";
import { AgeGate, AgeVerificationRequired, RegionBlocked } from "./gates/Gates";
import { isLegalPath, LegalPage } from "./pages/Legal";
import { PortalShell } from "./portal/PortalShell";
import { useAuth } from "./state/auth";
import { useSession } from "./state/session";

export default function App() {
  const path = window.location.pathname;
  // Legal pages stay reachable without the age gate and from blocked regions.
  if (isLegalPath(path)) return <LegalPage path={path} />;
  return <GatedApp />;
}

/**
 * Boot order: region check (via /api/v1/config) -> 18+ confirmation -> verified-age account where the country
 * requires one -> the app.
 */
function GatedApp() {
  const { t } = useTranslation();
  const regionBlocked = useSession((s) => s.regionBlocked);
  const ageConfirmed = useSession((s) => s.ageConfirmed);
  const config = useAppConfig();
  const authStatus = useAuth((s) => s.status);
  const ageVerified = useAuth((s) => !!s.user?.age_verified);

  // Restores the signed-in session from the refresh cookie once the region check has passed.
  useEffect(() => {
    if (config.data) void useAuth.getState().boot();
  }, [config.data]);

  if (regionBlocked) return <RegionBlocked />;
  if (config.isPending) {
    return (
      <div className="absolute inset-0 bg-black flex items-center justify-center">
        <Spinner />
      </div>
    );
  }
  if (config.isError) {
    return (
      <div className="absolute inset-0 bg-canvas flex items-center justify-center">
        <Empty icon={<WifiOff size={26} />} title={t("boot.offlineTitle")} sub={t("boot.offlineSub")} action={t("common.retry")} onAction={() => void config.refetch()} />
      </div>
    );
  }
  if (!ageConfirmed) return <AgeGate />;
  if (config.data?.age_check === "strict" && !ageVerified) {
    return authStatus === "booting"
      ? <div className="absolute inset-0 bg-black flex items-center justify-center"><Spinner /></div>
      : <AgeVerificationRequired />;
  }
  return <PortalShell />;
}
