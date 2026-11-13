import { unwrap } from "@reelguo/api-client";
import { Spinner } from "@reelguo/ui";
import { useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { Heart, MessageCircle, MoreHorizontal, SendHorizontal, X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { api, type Comment } from "../api";
import { fmtAgo, fmtCount } from "../player/format";
import { errorMessage, useAuth } from "../state/auth";
import { useUi } from "../state/ui";
import { Avatar } from "./Avatar";
import { useReport } from "./Report";

export type CommentTarget = { kind: "series"; id: number; ep: number; title: string } | { kind: "post"; id: number; title: string };

const targetKey = (t: CommentTarget) => ["comments", t.kind, t.id, t.kind === "series" ? t.ep : 0] as const;

function useComments(target: CommentTarget) {
  const uid = useAuth((s) => s.user?.id ?? 0);
  return useInfiniteQuery({
    // The viewer is part of the key: signed-in viewers also see their own comments under review.
    queryKey: [...targetKey(target), uid],
    initialPageParam: 1,
    queryFn: async ({ pageParam: page }) => unwrap(target.kind === "series"
      ? await api.GET("/api/v1/series/{id}/episodes/{ep}/comments", { params: { path: { id: target.id, ep: target.ep }, query: { page } } })
      : await api.GET("/api/v1/posts/{id}/comments", { params: { path: { id: target.id }, query: { page } } })),
    getNextPageParam: (last) => (last.pagination.page < last.pagination.pages ? last.pagination.page + 1 : undefined),
    staleTime: 15_000,
  });
}

/** 评论 panel (UI-REFERENCE §6): used in the mobile sheet, the desktop dock and on post pages. */
export function CommentsPanel({ target, total, onClose }: { target: CommentTarget; total: number; onClose?: () => void }) {
  const comments = useComments(target);
  const qc = useQueryClient();
  const { status, user, openLogin } = useAuth();
  const toast = useUi((s) => s.toast);
  const [text, setText] = useState("");
  const [replyTo, setReplyTo] = useState<Comment | null>(null);
  const [sending, setSending] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const signedIn = status === "signed_in";

  const items = comments.data?.pages.flatMap((p) => p.items) ?? [];
  const lastPage = comments.data?.pages.at(-1);
  const count = Math.max(total, lastPage?.pagination.total ?? 0);

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: targetKey(target) });
    void qc.invalidateQueries({ queryKey: [target.kind === "series" ? "series" : "post", target.id] });
  };

  const send = async () => {
    const t = text.trim();
    if (!t) return;
    if (!signedIn) return openLogin();
    setSending(true);
    try {
      const body = { text: t, parent_id: replyTo?.id ?? null };
      unwrap(target.kind === "series"
        ? await api.POST("/api/v1/series/{id}/episodes/{ep}/comments", { params: { path: { id: target.id, ep: target.ep } }, body })
        : await api.POST("/api/v1/posts/{id}/comments", { params: { path: { id: target.id } }, body }));
      setText("");
      setReplyTo(null);
      toast("评论已提交，审核通过后展示", "ok");
      refresh();
    } catch (e) {
      toast(errorMessage(e));
    } finally {
      setSending(false);
    }
  };

  const reply = (c: Comment) => {
    if (!signedIn) return openLogin();
    setReplyTo(c);
    inputRef.current?.focus();
  };

  return (
    <div className="flex flex-col flex-1 min-h-0">
      <div className={`shrink-0 flex items-center gap-2 ${onClose ? "px-4 h-12" : "px-5 pt-5 pb-2"}`}>
        <span className={`font-bold text-on ${onClose ? "text-[13.5px]" : "text-[16px]"}`}>{onClose ? "评论" : "评论区"}</span>
        <span className={`text-mut ${onClose ? "text-[10.5px]" : "ml-auto text-[12px]"}`}>{fmtCount(count)} 条评论{target.kind === "series" && ` · 第 ${target.ep} 集`}</span>
        {onClose && (
          <button onClick={onClose} aria-label="关闭" className="ml-auto w-7 h-7 rounded-full bg-s3 flex items-center justify-center text-mut pressable">
            <X size={13} />
          </button>
        )}
      </div>

      <div className="flex-1 min-h-0 scroll-y px-4">
        {items.length > 0 ? items.map((c) => (
          <CommentRow key={c.id} c={c} onReply={reply} onChanged={refresh} />
        )) : comments.isPending ? (
          <div className="flex justify-center py-10"><Spinner size={28} /></div>
        ) : comments.isError ? (
          <div className="flex flex-col items-center py-10 gap-2 text-[11.5px] text-mut">
            评论加载失败
            <button onClick={() => void comments.refetch()} className="px-4 h-8 rounded-(--r-ctl) bg-s3 text-on/80 pressable">重试</button>
          </div>
        ) : !onClose ? (
          <p className="py-3 text-[13px] text-mut">还没有评论，来抢沙发～</p>
        ) : (
          <div className="flex flex-col items-center py-12 gap-1.5 text-center">
            <span className="w-12 h-12 rounded-full bg-s3 flex items-center justify-center text-dim mb-1"><MessageCircle size={22} /></span>
            <span className="text-[13px] font-semibold text-on/85">还没有评论</span>
            <span className="text-[11px] text-mut">来抢第一个沙发</span>
          </div>
        )}
        {items.length > 0 && (
          <div className="flex justify-center py-4">
            {comments.isFetchingNextPage ? <Spinner size={20} />
              : comments.hasNextPage ? (
                <button onClick={() => void comments.fetchNextPage()} className="px-4 h-8 rounded-(--r-ctl) bg-s3 text-[11.5px] text-on/80 pressable">查看更多</button>
              ) : <span className="text-[10.5px] text-dim">— 到底啦 —</span>}
          </div>
        )}
      </div>

      {replyTo && (
        <div className="shrink-0 flex items-center gap-2 px-4 h-8 bg-s2/70 text-[11px] text-mut">
          <span className="truncate">回复 @{replyTo.nickname}：{replyTo.text}</span>
          <button onClick={() => setReplyTo(null)} aria-label="取消回复" className="ml-auto text-dim hover:text-on"><X size={13} /></button>
        </div>
      )}
      <form className="shrink-0 flex items-center gap-2 px-3 py-2.5 bg-s2/70" onSubmit={(e) => { e.preventDefault(); void send(); }}>
        <Avatar size={30} name={user?.nickname ?? "我"} color={user?.avatar_color} />
        <input ref={inputRef} value={text} onChange={(e) => setText(e.target.value)} maxLength={500}
          onFocus={(e) => { if (!signedIn) { e.currentTarget.blur(); openLogin(); } }}
          placeholder={!signedIn ? "登录后参与评论" : replyTo ? `回复 @${replyTo.nickname}` : `请留下你对《${target.title}》的看法`}
          aria-label="评论内容"
          className="flex-1 min-w-0 h-9 rounded-(--r-ctl) bg-s4 px-3.5 text-[12.5px] text-on placeholder:text-dim outline-none focus:bg-s3" />
        <button type="submit" disabled={!text.trim() || sending} aria-label="发送"
          className={`w-9 h-9 rounded-full flex items-center justify-center shrink-0 pressable ${text.trim() ? "bg-primary text-on-primary" : "bg-s3 text-dim"}`}>
          {sending ? <Spinner size={16} /> : <SendHorizontal size={16} />}
        </button>
      </form>
    </div>
  );
}

function CommentRow({ c, onReply, onChanged, isReply = false }: {
  c: Comment; onReply: (c: Comment) => void; onChanged: () => void; isReply?: boolean;
}) {
  const { status, user, openLogin } = useAuth();
  const openReport = useReport((s) => s.open);
  const toast = useUi((s) => s.toast);
  const [liked, setLiked] = useState(c.liked);
  const [likes, setLikes] = useState(c.like_count);
  const [menu, setMenu] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [moreReplies, setMoreReplies] = useState<Comment[] | null>(null);
  const [loadingReplies, setLoadingReplies] = useState(false);
  const pending = c.status === "pending";
  const mine = user?.id === c.user_id;

  useEffect(() => {
    setLiked(c.liked);
    setLikes(c.like_count);
  }, [c.liked, c.like_count]);

  const like = async () => {
    if (status !== "signed_in") return openLogin();
    if (pending) return toast("评论审核通过后才能点赞");
    const on = !liked;
    setLiked(on);
    setLikes((n) => n + (on ? 1 : -1));
    try {
      const path = { params: { path: { id: c.id } } };
      const r = unwrap(on ? await api.PUT("/api/v1/comments/{id}/like", path) : await api.DELETE("/api/v1/comments/{id}/like", path));
      setLikes(r.count);
    } catch (e) {
      setLiked(!on);
      setLikes((n) => n + (on ? -1 : 1));
      toast(errorMessage(e));
    }
  };

  const remove = async () => {
    if (!confirmDelete) return setConfirmDelete(true);
    setMenu(false);
    try {
      const res = await api.DELETE("/api/v1/comments/{id}", { params: { path: { id: c.id } } });
      if (!res.response.ok) throw new Error();
      toast("已删除", "ok");
      onChanged();
    } catch {
      toast("删除失败，请重试");
    }
  };

  const loadReplies = async () => {
    setLoadingReplies(true);
    try {
      const page = unwrap(await api.GET("/api/v1/comments/{id}/replies", { params: { path: { id: c.id }, query: { page: 1 } } }));
      setMoreReplies(page.items);
    } catch (e) {
      toast(errorMessage(e));
    } finally {
      setLoadingReplies(false);
    }
  };

  const replies = moreReplies ?? c.replies;
  const hidden = c.reply_count - replies.filter((r) => r.status === "approved").length;

  return (
    <div className={`flex gap-2.5 ${isReply ? "pt-2.5" : "py-3 border-b border-s3 last:border-0"}`}>
      <Avatar size={isReply ? 24 : 32} name={c.nickname} color={c.avatar_color} />
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-1.5 min-w-0">
          <span className={`${isReply ? "text-[11px]" : "text-[11.5px]"} font-semibold text-on/90 truncate`}>{c.nickname}</span>
          {c.pinned && <span className="shrink-0 px-1 rounded text-[9.5px] font-bold bg-sec text-primary">置顶</span>}
          {pending && <span className="shrink-0 px-1 rounded text-[9.5px] font-bold bg-s4 text-mut">审核中</span>}
          <span className="shrink-0 text-[10px] text-dim">{fmtAgo(c.created_at)}</span>
          <div className="relative ml-auto shrink-0">
            <button onClick={() => { setMenu((v) => !v); setConfirmDelete(false); }} aria-label="更多" className="w-6 h-5 flex items-center justify-center text-dim hover:text-on">
              <MoreHorizontal size={14} />
            </button>
            {menu && (
              <div className="absolute right-0 top-6 z-10 min-w-[96px] py-1 rounded-(--r-sm) bg-s4 elev-2 a-scale-in">
                {mine ? (
                  <button onClick={() => void remove()} className="w-full h-8 px-3 text-left text-[12px] text-danger hover:bg-white/5">
                    {confirmDelete ? "确认删除" : "删除"}
                  </button>
                ) : (
                  <button onClick={() => { setMenu(false); openReport({ type: "comment", id: c.id, label: `评论：@${c.nickname}「${c.text.slice(0, 30)}」` }); }}
                    className="w-full h-8 px-3 text-left text-[12px] text-on/85 hover:bg-white/5">举报</button>
                )}
              </div>
            )}
          </div>
        </div>
        <p className={`${isReply ? "text-[12px]" : "text-[12.5px]"} text-on/80 mt-1 leading-relaxed whitespace-pre-wrap break-words`}>{c.text}</p>
        <div className="flex items-center gap-3.5 mt-1.5 text-[10.5px] text-dim">
          <button onClick={() => void like()} aria-pressed={liked} aria-label="点赞" className={`flex items-center gap-1 pressable ${liked ? "text-like" : ""}`}>
            <Heart size={11} className={liked ? "fill-current" : ""} />{likes > 0 ? fmtCount(likes) : "赞"}
          </button>
          {!isReply && !pending && <button onClick={() => onReply(c)} className="pressable hover:text-on">回复</button>}
        </div>

        {!isReply && replies.length > 0 && (
          <div className="mt-1 pl-0.5">
            {replies.map((r) => <CommentRow key={r.id} c={r} onReply={onReply} onChanged={onChanged} isReply />)}
          </div>
        )}
        {!isReply && !moreReplies && hidden > 0 && (
          <button onClick={() => void loadReplies()} disabled={loadingReplies} className="mt-2 text-[10.5px] text-primary pressable">
            {loadingReplies ? "加载中…" : `展开 ${hidden} 条回复`}
          </button>
        )}
      </div>
    </div>
  );
}
