import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronDown, ChevronUp } from "lucide-react";
import { useState } from "react";
import { api } from "../session";
import { Card, ErrorText, PageHeader, formatTime } from "../ui";

type LegalPage = Schemas["AdminLegalPageDto"];

/**
 * 法律条款: the site's legal pages are always shown in full; each also shows a "待法务审核" notice above its text until
 * it is turned off here (on by default). The text itself ships with each release.
 */
export function LegalPagesAdmin() {
  const list = useQuery({ queryKey: ["admin", "legal"], queryFn: async () => unwrap(await api.GET("/api/admin/legal")) });
  return (
    <>
      <PageHeader title="法律条款"
        sub="用户协议、隐私政策、内容政策和充值协议在网站上始终完整显示。法务审核期间，每页顶部显示「本页内容为草稿，正在等待法务审核」的提示（默认开启）；法务确认某页后，在这里关闭该页的提示。条款正文随版本更新，需修改请联系开发。" />
      {list.isPending ? <div className="flex justify-center py-16"><Spinner /></div> : list.isError ? <ErrorText error={list.error} /> : (
        <div className="space-y-3">{list.data.map((p) => <LegalRow key={p.slug} page={p} />)}</div>
      )}
    </>
  );
}

function LegalRow({ page }: { page: LegalPage }) {
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const toggle = useMutation({
    mutationFn: async (underReview: boolean) =>
      unwrap(await api.PUT("/api/admin/legal/{slug}", { params: { path: { slug: page.slug } }, body: { under_review: underReview } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["admin", "legal"] }),
  });
  const on = toggle.isPending ? toggle.variables : page.under_review;
  return (
    <Card className="p-4">
      <div className="flex items-center gap-4">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-[14px] font-semibold">{page.title}</span>
            <span className="text-[12px] text-dim">{page.path}</span>
            <span className={`h-[20px] px-2 rounded-full text-[11px] inline-flex items-center ${on ? "bg-star/15 text-star" : "bg-success/15 text-success"}`}>
              {on ? "待法务审核" : "已审核"}
            </span>
          </div>
          <div className="mt-1 text-[11.5px] text-dim">
            {page.updated_at ? `${formatTime(page.updated_at)} · ${(page.updated_by ?? "").replace(/^admin:/, "")} 设置` : "默认：显示审核提示"}
          </div>
        </div>
        <label className="flex items-center gap-2 text-[13px] cursor-pointer select-none">
          显示审核提示
          <button type="button" role="switch" aria-checked={on} aria-label={`${page.title}：显示待法务审核提示`} disabled={toggle.isPending}
            onClick={() => toggle.mutate(!page.under_review)}
            className={`relative w-[42px] h-[24px] rounded-full transition-colors disabled:opacity-60 ${on ? "bg-primary" : "bg-s4"}`}>
            <span className={`absolute top-[3px] w-[18px] h-[18px] rounded-full bg-white shadow transition-all ${on ? "left-[21px]" : "left-[3px]"}`} />
          </button>
        </label>
        <button type="button" onClick={() => setOpen(!open)} className="h-8 px-3 rounded-(--r-ctl) text-[12.5px] text-mut hover:text-on hover:bg-(--p-hover) inline-flex items-center gap-1">
          {open ? <ChevronUp size={14} /> : <ChevronDown size={14} />}{open ? "收起正文" : "查看正文"}
        </button>
      </div>
      <ErrorText error={toggle.error} />
      {open && (
        <div className="mt-3 max-h-[480px] overflow-y-auto rounded-(--r-sm) bg-s2 px-5 py-3 text-[13px] leading-relaxed text-on/85 [&_h2]:text-[14.5px] [&_h2]:font-bold [&_h2]:text-on [&_h2]:mt-4 [&_h2]:mb-1.5 [&_p]:my-2 [&_ul]:list-disc [&_ul]:pl-5 [&_ol]:list-decimal [&_ol]:pl-5 [&_li]:my-1"
          dangerouslySetInnerHTML={{ __html: page.html }} />
      )}
    </Card>
  );
}
