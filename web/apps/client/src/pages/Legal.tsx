import { useTranslation } from "react-i18next";

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
          《{t(LEGAL_PATHS[path])}》
        </a>
      ))}
    </div>
  );
}

/**
 * Placeholder legal pages. These are reachable without the age gate and from blocked regions.
 * The text is written by the legal team before launch; the key commitments are listed here so the
 * pages are never empty.
 */
export function LegalPage({ path }: { path: LegalPath }) {
  const { t } = useTranslation();
  return (
    <div className="absolute inset-0 bg-bg scroll-y">
      <article className="max-w-[720px] mx-auto px-5 py-8 text-on">
        <h1 className="text-[22px] font-bold">{t(LEGAL_PATHS[path])}</h1>
        <p className="mt-3 text-[13px] text-mut">{t("legal.pending")}</p>
        {path === "/content-policy" && (
          <ul className="mt-6 space-y-2 text-[14px] leading-relaxed list-disc pl-5">
            <li>本站内容仅面向年满 18 周岁的成年人。</li>
            <li>所有角色均为成年人，包括 AI 生成与动画形式的角色。</li>
            <li>未经本人书面同意，不发布任何真实人物的肖像或形象。</li>
            <li>本站不提供任何“换脸”生成功能。</li>
            <li>任何人都可以举报违规内容，我们会尽快审核并下架。</li>
          </ul>
        )}
        {path === "/recharge-terms" && (
          <ul className="mt-6 space-y-2 text-[14px] leading-relaxed list-disc pl-5">
            <li>金币是本站内的虚拟道具，仅可用于解锁剧集，不可提现、转让或兑换现金。</li>
            <li>已解锁的剧集可在账号内永久观看（内容因版权或法律原因下架的除外）。</li>
            <li>VIP 为一次性购买的有效期权益，到期后不会自动续费；有效期内再次购买会顺延。</li>
            <li>银行卡账单上的商户名称以充值页面显示为准。</li>
            <li>退款按适用法律及本协议处理；发生退款或拒付时，将扣回对应的金币或 VIP 天数。</li>
          </ul>
        )}
        <a href="/" className="inline-block mt-8 text-[13px] text-primary">{t("legal.backHome")}</a>
      </article>
    </div>
  );
}
