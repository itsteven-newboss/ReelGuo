import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useQuery } from "@tanstack/react-query";
import { formatTime, regionName } from "../regions";
import { api } from "../session";

const ACTIONS: Record<string, string> = {
  "geo.block": "屏蔽地区",
  "geo.block.update": "修改屏蔽备注",
  "geo.unblock": "解除屏蔽",
  "admin.bootstrap": "创建初始管理员",
  "admin.login": "登录",
  "admin.create": "添加管理员",
  "admin.enable": "启用管理员",
  "admin.disable": "停用管理员",
  "admin.password": "重置密码",
  "category.update": "修改频道",
  "series.create": "新建剧集",
  "series.update": "修改剧集",
  "series.cover": "上传封面",
  "series.publish": "发布剧集",
  "series.unpublish": "下架剧集",
  "episode.create": "新建分集",
  "episode.update": "修改分集",
  "episode.replace_source": "替换分集视频",
  "episode.retry": "重试处理",
  "episode.delete": "删除分集",
  "import.run": "批量导入",
  "post.create": "新建文章",
  "post.update": "修改文章",
  "post.cover": "上传文章封面",
  "post.publish": "发布文章",
  "post.unpublish": "下架文章",
  "post.delete": "删除文章",
  "topic.create": "新建专题",
  "topic.update": "修改专题",
  "topic.delete": "删除专题",
  "announcement.create": "新增公告",
  "announcement.update": "修改公告",
  "announcement.delete": "删除公告",
  "comment.approve": "通过评论",
  "comment.reject": "拒绝评论",
  "comment.pin": "置顶评论",
  "comment.unpin": "取消置顶评论",
  "moderation.terms": "修改敏感词",
  "report.dismiss": "驳回举报",
  "report.takedown": "举报下架",
  "member.enable": "解封会员",
  "member.disable": "封禁会员",
  "member.age_verify": "标记年龄已验证",
  "member.age_unverify": "撤销年龄验证",
  "age.rule.set": "设置年龄验证规则",
  "age.rule.remove": "取消年龄验证规则",
  "product.create": "新增商品",
  "product.update": "修改商品",
  "order.refund": "订单退款",
  "member.wallet_adjust": "调整会员钱包",
  "app_release.create": "添加 App 版本",
  "app_release.update": "修改 App 版本",
  "app_release.publish": "发布 App 版本",
  "app_release.withdraw": "撤回 App 版本",
  "app_release.delete": "删除 App 版本",
};

const TARGETS: Record<string, string> = {
  user: "账号", series: "剧集", episode: "分集", category: "频道", import: "导入", post: "文章",
  comment: "评论", report: "举报", moderation_terms: "敏感词", age_rule: "年龄验证", product: "商品", order: "订单", app_release: "App 版本",
};

function describe(detailsJson: string | null): string {
  if (!detailsJson) return "";
  try {
    const d = JSON.parse(detailsJson) as Record<string, unknown>;
    const parts: string[] = [];
    if ("previous_note" in d) parts.push(`原备注：${d.previous_note || "—"}`);
    if ("note" in d) parts.push(`备注：${d.note || "—"}`);
    for (const [k, v] of Object.entries(d)) {
      if (k === "note" || k === "previous_note") continue;
      parts.push(`${k}: ${typeof v === "object" ? JSON.stringify(v) : String(v)}`);
    }
    return parts.join("；");
  } catch {
    return detailsJson;
  }
}

export function AuditLogPage() {
  const log = useQuery({
    queryKey: ["admin", "audit-log"],
    queryFn: async () => unwrap(await api.GET("/api/admin/audit-log", { params: { query: { limit: 200 } } })),
  });

  return (
    <div className="max-w-[1100px]">
      <h1 className="text-[20px] font-bold">操作日志</h1>
      <p className="mt-2 text-[12.5px] text-mut">最近 200 条后台操作，最新的在前。日志只追加，不能修改或删除。</p>

      <div className="mt-5 rounded-(--r-md) bg-s1 overflow-hidden elev-1">
        {log.isPending ? (
          <div className="flex justify-center py-10"><Spinner /></div>
        ) : log.isError ? (
          <p className="p-6 text-[13px] text-danger">加载失败</p>
        ) : (
          <table className="w-full text-[13px]">
            <thead className="text-left text-[11.5px] text-dim bg-s2">
              <tr>
                <th className="px-4 py-2.5 font-medium">时间</th>
                <th className="px-4 py-2.5 font-medium">操作人</th>
                <th className="px-4 py-2.5 font-medium">操作</th>
                <th className="px-4 py-2.5 font-medium">对象</th>
                <th className="px-4 py-2.5 font-medium">详情</th>
              </tr>
            </thead>
            <tbody>
              {log.data.length === 0 && (
                <tr><td colSpan={5} className="px-4 py-8 text-center text-dim">暂无记录</td></tr>
              )}
              {log.data.map((e) => (
                <tr key={e.id} className="border-t border-white/5 align-top">
                  <td className="px-4 py-3 text-mut whitespace-nowrap">{formatTime(e.at)}</td>
                  <td className="px-4 py-3">{e.actor}</td>
                  <td className="px-4 py-3">{ACTIONS[e.action] ?? e.action}</td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    {e.target_type === "blocked_country" ? `${regionName(e.target_id)}（${e.target_id}）` : `${TARGETS[e.target_type] ?? e.target_type} ${e.target_id}`}
                  </td>
                  <td className="px-4 py-3 text-mut">{describe(e.details_json)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
