import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../api";
import { BRAND } from "../brand";

export const LEGAL_PATHS = {
  "/terms": "legal.terms",
  "/privacy": "legal.privacy",
  "/content-policy": "legal.contentPolicy",
  "/recharge-terms": "legal.rechargeTerms",
} as const;

export type LegalPath = keyof typeof LEGAL_PATHS;

export function isLegalPath(path: string): path is LegalPath {
  return path in LEGAL_PATHS;
}

/** Links to the three legal pages, used on the age gate and in settings. */
export function LegalLinks({ className = "" }: { className?: string }) {
  const { t } = useTranslation();
  return (
    <div className={`flex flex-wrap gap-x-3 gap-y-1 text-[12px] ${className}`}>
      {(Object.keys(LEGAL_PATHS) as LegalPath[]).map((path) => (
        <a key={path} href={path} target="_blank" rel="noreferrer" className="text-primary underline-offset-2 hover:underline">
          {t("legal.link", { name: t(LEGAL_PATHS[path]) })}
        </a>
      ))}
    </div>
  );
}

/**
 * The legal pages (用户协议, 隐私政策, 内容政策, 充值协议), reachable without the age gate and from blocked regions.
 * Their text comes from the server (one copy, also in the server-rendered page). While a page's text awaits legal
 * review (Admin → 法律条款, on by default) a notice sits above it.
 */
export function LegalPage({ path }: { path: LegalPath }) {
  const { t } = useTranslation();
  const slug = path.slice(1);
  const page = useQuery({
    queryKey: ["legal", slug],
    queryFn: async () => unwrap(await api.GET("/api/legal/{slug}", { params: { path: { slug } } })),
    staleTime: 5 * 60_000,
  });
  useEffect(() => {
    const prev = document.title;
    document.title = `${t(LEGAL_PATHS[path])} - ${BRAND}`;
    return () => { document.title = prev; };
  }, [path, t]);
  // Until the server answers (or if it can't), the notice shows: it is on unless an admin turned it off.
  const underReview = page.data?.under_review ?? true;
  return (
    <div className="absolute inset-0 bg-bg scroll-y">
      <article className="max-w-[720px] mx-auto px-5 py-8 text-on">
        <h1 className="text-[22px] font-bold">{t(LEGAL_PATHS[path])}</h1>
        {underReview && (
          <p className="mt-3 px-3 py-2.5 rounded-[8px] border border-primary/35 bg-primary/[.07] text-[12.5px] leading-relaxed text-primary">{t("legal.pending")}</p>
        )}
        {page.isPending ? (
          <div className="flex justify-center py-12"><Spinner /></div>
        ) : page.isError ? (
          <p className="mt-6 text-[13px] text-mut">{t("legal.loadFailed")}</p>
        ) : (
          <div className="mt-4 article-body legal-body" dangerouslySetInnerHTML={{ __html: page.data.html }} />
        )}
        <a href="/" className="inline-block mt-8 text-[13px] text-primary">{t("legal.backHome")}</a>
      </article>
    </div>
  );
}
