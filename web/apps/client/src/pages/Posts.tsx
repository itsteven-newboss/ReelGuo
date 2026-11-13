import { Img, Spinner } from "@reelguo/ui";
import DOMPurify from "dompurify";
import { Flag, Flame, Heart, Play, Star } from "lucide-react";
import { useMemo, useState, type ReactNode } from "react";
import { usePlay, usePost, useSeries } from "../api";
import { fmtCount } from "../player/format";
import { VideoSurface } from "../player/VideoSurface";
import { Breadcrumb, Container, EmptyState, Panel } from "../portal/kit";
import { href, Link } from "../router";
import { CommentsPanel } from "../social/Comments";
import { useReport } from "../social/Report";
import { togglePost, useAuth } from "../state/auth";
import { useUi } from "../state/ui";

/** Second sanitising layer (the API already sanitises on save). Links open in a new tab. */
function cleanHtml(html: string): string {
  const clean = DOMPurify.sanitize(html, {
    ALLOWED_TAGS: ["p", "br", "strong", "em", "del", "h2", "h3", "h4", "ul", "ol", "li", "blockquote", "a", "img", "hr", "code", "pre"],
    ALLOWED_ATTR: ["href", "src", "alt", "title", "loading"],
  });
  const doc = new DOMParser().parseFromString(clean, "text/html");
  doc.querySelectorAll("a").forEach((a) => {
    a.setAttribute("target", "_blank");
    a.setAttribute("rel", "noopener noreferrer nofollow");
  });
  return doc.body.innerHTML;
}

/** One 吃瓜 post: cover (plays the linked series inline), article, actions and comments. */
export function PostPage({ id }: { id: number }) {
  const { muted, volume, toast } = useUi();
  const post = usePost(id);
  const [playing, setPlaying] = useState(false);
  const linked = post.data?.series ?? null;
  const series = useSeries(playing ? linked?.id : undefined);
  const firstEp = series.data?.episodes[0]?.ep_num;
  const play = usePlay(playing ? linked?.id : undefined, firstEp);
  const html = useMemo(() => cleanHtml(post.data?.content_html ?? ""), [post.data?.content_html]);
  const liked = useAuth((s) => s.postLikes.includes(id));
  const faved = useAuth((s) => s.postFavs.includes(id));
  const openReport = useReport((s) => s.open);
  const [counts, setCounts] = useState<{ like?: number; collect?: number }>({});
  const toggle = async (kind: "like" | "collect") => {
    const count = await togglePost(kind, id);
    if (count !== null) setCounts((c) => ({ ...c, [kind]: count }));
  };

  return (
    <Container>
      <Breadcrumb items={[{ label: "ReelGuo", to: "/" }, { label: "热点吃瓜", to: href.posts() }, { label: post.data?.title ?? "帖子" }]} />
      {post.isError ? <EmptyState title="内容不存在或已下架" /> : !post.data ? <div className="flex justify-center py-16"><Spinner /></div> : (
        <div className="mt-5 grid gap-6 lg:grid-cols-[minmax(0,1fr)_360px]">
          <article className="min-w-0">
            <h1 className="text-[24px] lg:text-[30px] font-bold text-on leading-snug">{post.data.title}</h1>
            <div className="mt-2.5 flex items-center gap-3 text-[13px] text-mut">
              <span>ReelGuo吃瓜</span>
              {post.data.published_at && <span>{new Date(post.data.published_at).toLocaleDateString("sv-SE")}</span>}
              <span className="flex items-center gap-1"><Flame size={13} />{fmtCount(post.data.hot)}</span>
            </div>
            <div className="relative mt-5 w-full rounded-[14px] overflow-hidden bg-black" style={{ aspectRatio: "16 / 9" }}>
              {playing && play.data ? (
                <VideoSurface src={play.data.video_url} poster={play.data.poster} active muted={muted} volume={volume} rate={1} fit="contain" controls />
              ) : (
                <button className="absolute inset-0" aria-label="播放" onClick={() => (linked ? setPlaying(true) : toast("该帖子没有视频"))}>
                  <Img src={post.data.cover} eager />
                  {linked && (
                    <span className="absolute inset-0 flex items-center justify-center">
                      <span className="w-16 h-16 rounded-full bg-black/55 backdrop-blur-sm flex items-center justify-center">
                        {playing ? <Spinner size={26} /> : <Play size={26} className="text-white fill-current ml-1" />}
                      </span>
                    </span>
                  )}
                </button>
              )}
            </div>
            {post.data.tags.length > 0 && (
              <div className="flex flex-wrap gap-1.5 mt-4">
                {post.data.tags.slice(0, 8).map((tag) => <span key={tag} className="h-[24px] px-2.5 rounded-md bg-white/[.06] text-[12px] text-mut flex items-center">#{tag}</span>)}
              </div>
            )}
            <div className="article-body mt-4 !text-[15px]" dangerouslySetInnerHTML={{ __html: html }} />
            <div className="flex items-center gap-2.5 mt-6">
              <Action on={liked} onClick={() => void toggle("like")} icon={<Heart size={15} className={liked ? "text-like fill-current" : ""} />}>{fmtCount(counts.like ?? post.data.like_count)}</Action>
              <Action on={faved} onClick={() => void toggle("collect")} icon={<Star size={15} className={faved ? "text-star fill-current" : ""} />}>{faved ? "已收藏" : "收藏"}</Action>
              <button onClick={() => openReport({ type: "post", id, label: `文章：${post.data!.title}` })}
                className="ml-auto h-10 px-4 rounded-[10px] text-[13px] text-dim hover:text-mut flex items-center gap-1.5"><Flag size={13} />举报</button>
            </div>
          </article>
          <aside className="flex flex-col gap-4">
            {linked && (
              <Panel className="p-4">
                <div className="text-[13px] text-mut mb-3">本文相关剧集</div>
                <Link to={href.video(linked.id)} className="flex gap-3 group">
                  <span className="relative w-[72px] h-[96px] rounded-[8px] overflow-hidden bg-s2 shrink-0"><Img src={linked.cover} /></span>
                  <span className="min-w-0 flex-1">
                    <span className="block text-[15px] font-bold text-on truncate group-hover:text-primary">{linked.title}</span>
                    <span className="block mt-1 text-[12px] text-dim">共 {linked.episode_count} 集</span>
                    <span className="mt-3 inline-flex h-8 px-4 rounded-full bg-primary text-on-primary text-[12.5px] font-bold items-center">观看正片</span>
                  </span>
                </Link>
              </Panel>
            )}
            <Panel className="h-[560px] flex flex-col overflow-hidden">
              <CommentsPanel target={{ kind: "post", id, title: post.data.title }} total={post.data.comment_count} />
            </Panel>
          </aside>
        </div>
      )}
    </Container>
  );
}

function Action({ icon, on = false, onClick, children }: { icon: ReactNode; on?: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button onClick={onClick} aria-pressed={on}
      className={`h-10 px-5 rounded-[10px] border flex items-center gap-1.5 text-[13px] font-semibold tabular-nums ${on ? "border-primary/40 bg-primary/10 text-on" : "border-white/[.08] bg-[#141414] text-on/85 hover:border-white/20"}`}>
      {icon}{children}
    </button>
  );
}
