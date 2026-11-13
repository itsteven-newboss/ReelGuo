import { ApiError, unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState, type FormEvent } from "react";
import { formatTime, regionName } from "../regions";
import { api } from "../session";

const LIST_KEY = ["admin", "age-rules"];

/** 年龄验证: countries where only verified-age accounts may watch. Set after legal review, like the block list. */
export function AgeRulesPage() {
  const qc = useQueryClient();
  const list = useQuery({ queryKey: LIST_KEY, queryFn: async () => unwrap(await api.GET("/api/admin/geo/age-rules")) });
  const codes = useQuery({
    queryKey: ["admin", "country-codes"],
    queryFn: async () => unwrap(await api.GET("/api/admin/geo/country-codes")),
    staleTime: Infinity,
  });
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: LIST_KEY });
    void qc.invalidateQueries({ queryKey: ["admin", "audit-log"] });
  };

  const upsert = useMutation({
    mutationFn: async ({ code, note }: { code: string; note: string }) =>
      unwrap(await api.PUT("/api/admin/geo/age-rules/{countryCode}", { params: { path: { countryCode: code } }, body: { mode: "strict", note } })),
    onSuccess: refresh,
  });
  const remove = useMutation({
    mutationFn: async (code: string) => {
      const r = await api.DELETE("/api/admin/geo/age-rules/{countryCode}", { params: { path: { countryCode: code } } });
      if (!r.response.ok) unwrap(r);
    },
    onSuccess: refresh,
  });

  const listed = new Set(list.data?.map((r) => r.country_code));
  const options = useMemo(
    () => (codes.data ?? []).map((code) => ({ code, name: regionName(code) })).sort((a, b) => a.name.localeCompare(b.name, "zh-CN")),
    [codes.data],
  );
  const [code, setCode] = useState("");
  const [note, setNote] = useState("");

  function add(e: FormEvent) {
    e.preventDefault();
    if (!code) return;
    upsert.mutate({ code, note }, { onSuccess: () => { setCode(""); setNote(""); } });
  }

  const error = upsert.error ?? remove.error;

  return (
    <div className="max-w-[1000px]">
      <h1 className="text-[20px] font-bold">年龄验证</h1>
      <div className="mt-2 text-[12.5px] text-mut leading-relaxed space-y-1">
        <p>未列出的国家/地区：首次访问时确认已满 18 周岁（当前默认）。</p>
        <p>列表中的国家/地区（严格验证）：只有<b className="text-on">已验证年龄的账号</b>登录后才能看到任何内容，其他访客只能看到「需要年龄验证」页面，API 也会拒绝内容请求。
          在线验证服务接入之前，可在「会员」页手动标记已核验的账号。修改立即生效（多实例部署时最多延迟 60 秒）。</p>
      </div>

      <form onSubmit={add} className="mt-5 flex flex-wrap items-end gap-3 bg-s2 rounded-(--r-md) p-4">
        <label className="flex flex-col gap-1 text-[12px] text-mut">
          国家/地区
          <select value={code} onChange={(e) => setCode(e.target.value)} required
            className="h-9 min-w-[240px] px-2 rounded-(--r-sm) bg-s3 text-on text-[13px] outline-none">
            <option value="">请选择…</option>
            {options.filter((o) => !listed.has(o.code)).map((o) => <option key={o.code} value={o.code}>{o.name}（{o.code}）</option>)}
          </select>
        </label>
        <label className="flex flex-col gap-1 text-[12px] text-mut flex-1 min-w-[240px]">
          依据（如法规名称、法务审核编号）
          <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} className="h-9 px-3 rounded-(--r-sm) bg-s3 text-on text-[13px] outline-none" />
        </label>
        <button disabled={!code || upsert.isPending} className="h-9 px-5 rounded-(--r-ctl) bg-primary text-on-primary text-[13px] font-bold pressable disabled:opacity-50">
          设为严格验证
        </button>
      </form>

      {error && <p className="mt-3 text-[12.5px] text-danger">{error instanceof ApiError ? error.message : "操作失败"}</p>}

      <div className="mt-5 rounded-(--r-md) bg-s1 overflow-hidden elev-1">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : list.isError ? (
          <p className="p-6 text-[13px] text-danger">加载失败</p>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">代码</th>
                <th className="px-4 py-2.5 font-medium">国家/地区</th>
                <th className="px-4 py-2.5 font-medium">要求</th>
                <th className="px-4 py-2.5 font-medium">依据</th>
                <th className="px-4 py-2.5 font-medium">更新人</th>
                <th className="px-4 py-2.5 font-medium">更新时间</th>
                <th className="px-4 py-2.5 font-medium text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {list.data.length === 0 && <tr><td colSpan={7} className="px-4 py-8 text-center text-dim">所有地区都使用 18+ 确认</td></tr>}
              {list.data.map((r) => (
                <tr key={r.country_code} className="border-t border-white/5">
                  <td className="px-4 py-3 font-mono font-semibold">{r.country_code}</td>
                  <td className="px-4 py-3">{regionName(r.country_code)}</td>
                  <td className="px-4 py-3"><span className="px-2 py-0.5 rounded-md text-[11px] font-semibold bg-star/20 text-star">严格验证</span></td>
                  <td className="px-4 py-3 text-mut max-w-[260px]">{r.note || "—"}</td>
                  <td className="px-4 py-3 text-mut">{r.updated_by}</td>
                  <td className="px-4 py-3 text-mut whitespace-nowrap">{formatTime(r.updated_at)}</td>
                  <td className="px-4 py-3 text-right whitespace-nowrap">
                    <button className="text-primary hover:underline" onClick={() => {
                      const next = window.prompt(`修改 ${regionName(r.country_code)}（${r.country_code}）的依据`, r.note ?? "");
                      if (next !== null) upsert.mutate({ code: r.country_code, note: next });
                    }}>修改依据</button>
                    <button className="ml-4 text-danger hover:underline" onClick={() => {
                      if (window.confirm(`确定取消 ${regionName(r.country_code)}（${r.country_code}）的严格验证？该地区将恢复为 18+ 确认。`)) remove.mutate(r.country_code);
                    }}>取消</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
