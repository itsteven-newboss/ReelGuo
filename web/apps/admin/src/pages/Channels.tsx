import { unwrap, type Schemas } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { api } from "../session";
import { Button, Card, ErrorText, PageHeader, StatusBadge, inputClass } from "../ui";

type Category = Schemas["AdminCategoryDto"];
type Status = Category["status"];

const STATUS_LABELS: [Status, string][] = [["active", "开放"], ["coming_soon", "即将上线"], ["hidden", "隐藏"]];

export function ChannelsPage() {
  const list = useQuery({ queryKey: ["admin", "categories"], queryFn: async () => unwrap(await api.GET("/api/admin/categories")) });
  return (
    <>
      <PageHeader
        title="频道"
        sub="频道是数据库中的配置，改名、排序、隐藏无需发版。「即将上线」的频道只显示敬请期待，不能添加或发布内容。"
      />
      <Card className="overflow-hidden">
        {list.isPending ? <div className="flex justify-center py-10"><Spinner /></div> : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">代码</th>
                <th className="px-4 py-2.5 font-medium">名称</th>
                <th className="px-4 py-2.5 font-medium">描述</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">排序</th>
                <th className="px-4 py-2.5 font-medium">剧集数</th>
                <th className="px-4 py-2.5" />
              </tr>
            </thead>
            <tbody>{list.data?.map((c) => <ChannelRow key={c.slug} channel={c} />)}</tbody>
          </table>
        )}
      </Card>
    </>
  );
}

function ChannelRow({ channel }: { channel: Category }) {
  const qc = useQueryClient();
  const [edit, setEdit] = useState(false);
  const [name, setName] = useState(channel.name);
  const [description, setDescription] = useState(channel.description ?? "");
  const [status, setStatus] = useState<Status>(channel.status);
  const [sort, setSort] = useState(channel.sort_order);

  const save = useMutation({
    mutationFn: async () => unwrap(await api.PUT("/api/admin/categories/{slug}", {
      params: { path: { slug: channel.slug } },
      body: { name, description, status, sort_order: sort },
    })),
    onSuccess: () => {
      setEdit(false);
      void qc.invalidateQueries({ queryKey: ["admin", "categories"] });
    },
  });

  function submit() {
    if (channel.slug === "ai-huanlian" && status === "active" && channel.status !== "active"
      && !window.confirm("开放此频道后，其中所有内容都必须符合同意规则：出现的任何真实人物都需有本人签署的同意书。本平台不提供换脸生成功能。确定开放？")) return;
    save.mutate();
  }

  if (!edit) {
    return (
      <tr className="border-t border-white/5">
        <td className="px-4 py-3 font-mono text-[12px]">{channel.slug}</td>
        <td className="px-4 py-3 font-medium">{channel.name}</td>
        <td className="px-4 py-3 text-mut">{channel.description || "—"}</td>
        <td className="px-4 py-3"><StatusBadge status={channel.status} /></td>
        <td className="px-4 py-3 text-mut">{channel.sort_order}</td>
        <td className="px-4 py-3 text-mut">{channel.series_count}</td>
        <td className="px-4 py-3 text-right"><button onClick={() => setEdit(true)} className="text-primary hover:underline">编辑</button></td>
      </tr>
    );
  }

  return (
    <tr className="border-t border-white/5 bg-s2/50">
      <td className="px-4 py-3 font-mono text-[12px]">{channel.slug}</td>
      <td className="px-4 py-3"><input value={name} onChange={(e) => setName(e.target.value)} className={`${inputClass} w-full`} /></td>
      <td className="px-4 py-3"><input value={description} onChange={(e) => setDescription(e.target.value)} className={`${inputClass} w-full`} /></td>
      <td className="px-4 py-3">
        <select value={status} onChange={(e) => setStatus(e.target.value as Status)} className={inputClass}>
          {STATUS_LABELS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
      </td>
      <td className="px-4 py-3"><input type="number" value={sort} onChange={(e) => setSort(Number(e.target.value))} className={`${inputClass} w-20`} /></td>
      <td className="px-4 py-3 text-mut">{channel.series_count}</td>
      <td className="px-4 py-3 text-right whitespace-nowrap">
        <Button variant="primary" disabled={save.isPending} onClick={submit}>保存</Button>
        <Button variant="ghost" onClick={() => setEdit(false)}>取消</Button>
        <ErrorText error={save.error} />
      </td>
    </tr>
  );
}
