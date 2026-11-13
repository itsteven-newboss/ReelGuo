import { unwrap, ApiError } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState, type FormEvent } from "react";
import { formatTime, regionName } from "../regions";
import { api } from "../session";

const LIST_KEY = ["admin", "blocked-countries"];

export function BlockedCountriesPage() {
  const qc = useQueryClient();
  const list = useQuery({
    queryKey: LIST_KEY,
    queryFn: async () => unwrap(await api.GET("/api/admin/geo/blocked-countries")),
  });
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
      unwrap(await api.PUT("/api/admin/geo/blocked-countries/{countryCode}", {
        params: { path: { countryCode: code } },
        body: { note },
      })),
    onSuccess: refresh,
  });

  const remove = useMutation({
    mutationFn: async (code: string) => {
      const result = await api.DELETE("/api/admin/geo/blocked-countries/{countryCode}", { params: { path: { countryCode: code } } });
      if (!result.response.ok) unwrap(result);
    },
    onSuccess: refresh,
  });

  const blocked = new Set(list.data?.map((b) => b.country_code));
  const options = useMemo(
    () => (codes.data ?? [])
      .map((code) => ({ code, name: regionName(code) }))
      .sort((a, b) => a.name.localeCompare(b.name, "zh-CN")),
    [codes.data],
  );

  const [code, setCode] = useState("");
  const [note, setNote] = useState("");

  function add(e: FormEvent) {
    e.preventDefault();
    if (!code) return;
    upsert.mutate({ code, note }, { onSuccess: () => { setCode(""); setNote(""); } });
  }

  function editNote(c: string, current: string | null) {
    const next = window.prompt(`修改 ${regionName(c)}（${c}）的备注`, current ?? "");
    if (next !== null) upsert.mutate({ code: c, note: next });
  }

  function unblock(c: string) {
    if (window.confirm(`确定解除对 ${regionName(c)}（${c}）的屏蔽？解除后该地区用户可以立即访问。`)) remove.mutate(c);
  }

  const error = upsert.error ?? remove.error;

  return (
    <div className="max-w-[1000px]">
      <h1 className="text-[20px] font-bold">地区屏蔽</h1>
      <div className="mt-2 text-[12.5px] text-mut leading-relaxed space-y-1">
        <p>列表中的国家/地区访问 App 和公开 API 时，会看到「当前地区暂不提供服务」。修改立即生效（多实例部署时最多延迟 60 秒）。</p>
        <p>视频与图片也按此列表屏蔽（API 自带的媒体服务和 CDN 边缘脚本都会检查）。后台和健康检查不受屏蔽影响。XX 表示无法识别的地区，T1 表示 Tor 匿名网络。</p>
      </div>

      <form onSubmit={add} className="mt-5 flex flex-wrap items-end gap-3 bg-s2 rounded-(--r-md) p-4">
        <label className="flex flex-col gap-1 text-[12px] text-mut">
          国家/地区
          <select value={code} onChange={(e) => setCode(e.target.value)} required
            className="h-9 min-w-[240px] px-2 rounded-(--r-sm) bg-s3 text-on text-[13px] outline-none">
            <option value="">请选择…</option>
            {options.filter((o) => !blocked.has(o.code)).map((o) => (
              <option key={o.code} value={o.code}>{o.name}（{o.code}）</option>
            ))}
          </select>
        </label>
        <label className="flex flex-col gap-1 text-[12px] text-mut flex-1 min-w-[240px]">
          备注（如法务审核编号）
          <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500}
            className="h-9 px-3 rounded-(--r-sm) bg-s3 text-on text-[13px] outline-none" />
        </label>
        <button disabled={!code || upsert.isPending} className="h-9 px-5 rounded-(--r-ctl) bg-primary text-on-primary text-[13px] font-bold pressable disabled:opacity-50">
          加入屏蔽
        </button>
      </form>

      {error && <p className="mt-3 text-[12.5px] text-danger">{error instanceof ApiError ? error.message : "操作失败"}</p>}

      <div className="mt-5 rounded-(--r-md) bg-s1 overflow-hidden elev-1">
        {list.isPending ? (
          <div className="flex justify-center py-10"><Spinner /></div>
        ) : list.isError ? (
          <p className="p-6 text-[13px] text-danger">加载失败</p>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">代码</th>
                <th className="px-4 py-2.5 font-medium">国家/地区</th>
                <th className="px-4 py-2.5 font-medium">备注</th>
                <th className="px-4 py-2.5 font-medium">更新人</th>
                <th className="px-4 py-2.5 font-medium">更新时间</th>
                <th className="px-4 py-2.5 font-medium text-right">操作</th>
              </tr>
            </thead>
            <tbody>
              {list.data.length === 0 && (
                <tr><td colSpan={6} className="px-4 py-8 text-center text-dim">没有屏蔽任何地区</td></tr>
              )}
              {list.data.map((b) => (
                <tr key={b.country_code} className="border-t border-white/5">
                  <td className="px-4 py-3 font-mono font-semibold">{b.country_code}</td>
                  <td className="px-4 py-3">{regionName(b.country_code)}</td>
                  <td className="px-4 py-3 text-mut max-w-[280px]">{b.note || "—"}</td>
                  <td className="px-4 py-3 text-mut">{b.updated_by}</td>
                  <td className="px-4 py-3 text-mut whitespace-nowrap">{formatTime(b.updated_at)}</td>
                  <td className="px-4 py-3 text-right whitespace-nowrap">
                    <button onClick={() => editNote(b.country_code, b.note)} className="text-primary hover:underline">修改备注</button>
                    <button onClick={() => unblock(b.country_code)} className="ml-4 text-danger hover:underline">解除屏蔽</button>
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
