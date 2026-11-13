import { ApiError, createReelGuoClient, unwrap, type Schemas } from "@reelguo/api-client";
import { QueryClient, useInfiniteQuery, useQuery } from "@tanstack/react-query";

export type VideoCard = Schemas["VideoCardDto"];
export type SeriesDetail = Schemas["SeriesDetailDto"];
export type PlayInfo = Schemas["PlayInfoDto"];
export type RankItem = Schemas["RankItemDto"];
export type PostCard = Schemas["PostCardDto"];
export type PostDetail = Schemas["PostDetailDto"];
export type RankKind = "hot" | "recommend" | "potential";
import { useSession } from "./state/session";
import { accessExpiring, accessToken, APP_HEADER, refreshAccess } from "./state/token";

export type Wallet = Schemas["WalletDto"];
export type WalletEntry = Schemas["WalletEntryDto"];
export type Product = Schemas["ProductDto"];
export type Store = Schemas["StoreDto"];
export type Order = Schemas["OrderDto"];
export type SeriesAccess = Schemas["SeriesAccessDto"];

export type Comment = Schemas["CommentDto"];
export type CommentPage = Schemas["CommentPage"];
export type ReportReason = Schemas["ReportReason"];
export type ReportTarget = Schemas["ReportTarget"];

export const api = createReelGuoClient({
  onError(status, body) {
    // A 451 from any endpoint means the visitor's region is blocked: switch the whole app to that screen.
    if (status === 451) useSession.getState().setRegionBlocked(true);
    // The country got a strict age rule while the app was open: reload the boot config so the age screen shows.
    if (status === 403 && body?.error?.code === "age_verification_required") void queryClient.invalidateQueries({ queryKey: ["config"] });
  },
});

/**
 * Viewer auth: sends the in-memory access token, refreshes it shortly before it expires, and on a 401 refreshes
 * once and retries the request. A copy of each authenticated request is kept (by request id) for that retry,
 * because a sent request's body can't be read again.
 */
const retryCopies = new Map<string, Request>();
api.use({
  async onRequest({ request, id }) {
    if (new URL(request.url, window.location.origin).pathname.startsWith("/api/v1/auth/")) {
      request.headers.set(APP_HEADER, "1");
      return request;
    }
    if (accessToken() && accessExpiring()) await refreshAccess();
    const token = accessToken();
    if (!token) return request;
    request.headers.set("Authorization", `Bearer ${token}`);
    retryCopies.set(id, request.clone());
    return request;
  },
  async onResponse({ response, id }) {
    const copy = retryCopies.get(id);
    retryCopies.delete(id);
    if (response.status !== 401 || !copy) return response;
    const refreshed = await refreshAccess();
    if (refreshed.kind !== "ok") return response;
    copy.headers.set("Authorization", `Bearer ${refreshed.auth.access_token}`);
    return fetch(copy);
  },
  onError({ id }) {
    retryCopies.delete(id);
  },
});

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 60_000,
      refetchOnWindowFocus: false,
      // Client errors (4xx, including 451) will not change on retry; network and 5xx errors get two more tries.
      retry: (failures, error) => !(error instanceof ApiError && error.status < 500) && failures < 2,
    },
  },
});

export function useAppConfig() {
  return useQuery({
    queryKey: ["config"],
    queryFn: async () => unwrap(await api.GET("/api/v1/config")),
    staleTime: Infinity,
  });
}

export function useCategories() {
  return useQuery({
    queryKey: ["categories"],
    queryFn: async () => unwrap(await api.GET("/api/v1/categories")),
  });
}

const FEED_PAGE = 10;

/** Home feed, loaded page by page as the viewer swipes. */
export function useFeed() {
  return useInfiniteQuery({
    queryKey: ["feed"],
    initialPageParam: 1,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/feed", { params: { query: { page: pageParam, size: FEED_PAGE } } })),
    getNextPageParam: (last) => (last.pagination.page < last.pagination.pages ? last.pagination.page + 1 : undefined),
  });
}

export function useCategorySeries(slug: string, sort: "latest" | "hot" | "score", enabled = true) {
  return useInfiniteQuery({
    queryKey: ["category-series", slug, sort],
    enabled,
    initialPageParam: 1,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/categories/{slug}/series", {
      params: { path: { slug }, query: { sort, page: pageParam, size: 20 } },
    })),
    getNextPageParam: (last) => (last.pagination.page < last.pagination.pages ? last.pagination.page + 1 : undefined),
  });
}

export const seriesQuery = (id: number) => ({
  queryKey: ["series", id],
  queryFn: async () => unwrap(await api.GET("/api/v1/series/{id}", { params: { path: { id } } })),
  staleTime: 5 * 60_000,
});

export function useSeries(id: number | undefined) {
  return useQuery({ ...seriesQuery(id ?? 0), enabled: !!id });
}

/** Signed playback URL. Kept until 5 minutes before it expires; an expired URL is refetched on demand. */
export const playQuery = (id: number, ep: number) => ({
  queryKey: ["play", id, ep],
  queryFn: async () => unwrap(await api.GET("/api/v1/series/{id}/episodes/{ep}/play", { params: { path: { id, ep } } })),
  staleTime: (q: { state: { data?: PlayInfo } }) =>
    q.state.data ? Math.max(0, new Date(q.state.data.expires_at).getTime() - Date.now() - 5 * 60_000) : 0,
});

export function usePlay(id: number | undefined, ep: number | undefined) {
  return useQuery({ ...playQuery(id ?? 0, ep ?? 0), enabled: !!id && !!ep });
}

export function useRanks(kind: RankKind) {
  return useQuery({
    queryKey: ["ranks", kind],
    queryFn: async () => unwrap(await api.GET("/api/v1/ranks/{kind}", { params: { path: { kind } } })),
    staleTime: 5 * 60_000,
  });
}

export function useSearch(q: string) {
  return useInfiniteQuery({
    queryKey: ["search", q],
    enabled: q.length > 0,
    initialPageParam: 1,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/search", { params: { query: { q, page: pageParam, size: 20 } } })),
    getNextPageParam: (last) => (last.pagination.page < last.pagination.pages ? last.pagination.page + 1 : undefined),
  });
}

export function useSuggest(seed: number) {
  return useQuery({
    queryKey: ["suggest", seed],
    queryFn: async () => unwrap(await api.GET("/api/v1/search/suggest", { params: { query: { seed } } })),
    staleTime: 5 * 60_000,
    placeholderData: (prev) => prev,
  });
}

export function usePosts() {
  return useInfiniteQuery({
    queryKey: ["posts"],
    initialPageParam: 1,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/posts", { params: { query: { page: pageParam, size: 20 } } })),
    getNextPageParam: (last) => (last.pagination.page < last.pagination.pages ? last.pagination.page + 1 : undefined),
  });
}

export function usePost(id: number) {
  return useQuery({
    queryKey: ["post", id],
    queryFn: async () => unwrap(await api.GET("/api/v1/posts/{id}", { params: { path: { id } } })),
  });
}

/* ---------------------------------------------------------------------------------------------
 * Monetization: wallet, per-series access, store, records. Only fetched when signed in.
 * ------------------------------------------------------------------------------------------- */

export function useWallet(enabled: boolean) {
  return useQuery({
    queryKey: ["wallet"],
    enabled,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/wallet")),
    staleTime: 30_000,
  });
}

/** Paid episodes of a series this viewer may watch (bought ones, or all while VIP). */
export function useSeriesAccess(id: number | undefined, enabled: boolean) {
  return useQuery({
    queryKey: ["access", id],
    enabled: enabled && !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/series/{id}/access", { params: { path: { id: id! } } })),
    staleTime: 60_000,
  });
}

export function useStore() {
  return useQuery({
    queryKey: ["store"],
    queryFn: async () => unwrap(await api.GET("/api/v1/store")),
    staleTime: 5 * 60_000,
  });
}

export function useWalletEntries(enabled: boolean) {
  return useInfiniteQuery({
    queryKey: ["wallet-entries"],
    enabled,
    initialPageParam: 1,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/me/wallet/entries", { params: { query: { page: pageParam, size: 20 } } })),
    getNextPageParam: (last) => (last.pagination.page < last.pagination.pages ? last.pagination.page + 1 : undefined),
  });
}

/* ---------------------------------------------------------------------------------------------
 * Portal: site chrome, listings by sort, topics, ratings.
 * ------------------------------------------------------------------------------------------- */

export type Site = Schemas["SiteDto"];
export type TopicCard = Schemas["TopicCardDto"];
export type FeedSort = "latest" | "updated" | "hot" | "picks" | "random";
export type ChannelSort = "latest" | "hot" | "original" | "random" | "score" | "picks";

export function useSite() {
  return useQuery({ queryKey: ["site"], queryFn: async () => unwrap(await api.GET("/api/v1/site")), staleTime: 10 * 60_000 });
}

export function useSeriesList(sort: FeedSort, size: number) {
  return useQuery({
    queryKey: ["series-list", sort, size],
    queryFn: async () => unwrap(await api.GET("/api/v1/feed", { params: { query: { sort, size, page: 1 } } })),
    staleTime: 2 * 60_000,
  });
}

export function useChannelList(slug: string, sort: ChannelSort, size = 20, enabled = true) {
  return useInfiniteQuery({
    queryKey: ["channel-list", slug, sort, size],
    enabled,
    initialPageParam: 1,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/categories/{slug}/series", {
      params: { path: { slug }, query: { sort, page: pageParam, size } },
    })),
    getNextPageParam: (last) => (sort !== "random" && last.pagination.page < last.pagination.pages ? last.pagination.page + 1 : undefined),
    staleTime: sort === "random" ? 0 : 2 * 60_000,
  });
}

export function useTopics(size = 24) {
  return useQuery({
    queryKey: ["topics", size],
    queryFn: async () => unwrap(await api.GET("/api/v1/topics", { params: { query: { size, page: 1 } } })),
    staleTime: 5 * 60_000,
  });
}

export function useTopic(id: number) {
  return useQuery({ queryKey: ["topic", id], queryFn: async () => unwrap(await api.GET("/api/v1/topics/{id}", { params: { path: { id } } })) });
}

export function useMyRating(id: number, enabled: boolean) {
  return useQuery({
    queryKey: ["rating", id],
    enabled,
    queryFn: async () => unwrap(await api.GET("/api/v1/series/{id}/rating", { params: { path: { id } } })),
  });
}

/** One numbered page of a channel ("all" = every channel), as on the reference's channel pages. */
export function useListingPage(slug: string, sort: ChannelSort, page: number, size: number, enabled = true) {
  return useQuery({
    queryKey: ["listing-page", slug, sort, page, size],
    enabled,
    placeholderData: (prev) => prev,
    queryFn: async () => slug === "all"
      ? unwrap(await api.GET("/api/v1/feed", { params: { query: { sort: sort === "latest" ? "updated" : sort, page, size } } }))
      : unwrap(await api.GET("/api/v1/categories/{slug}/series", { params: { path: { slug }, query: { sort, page, size } } })),
    staleTime: sort === "random" ? 0 : 2 * 60_000,
  });
}

/** Every channel at once ("全部"), with the same sorts as a channel page. */
export function useAllSeries(sort: ChannelSort, size = 20) {
  return useInfiniteQuery({
    queryKey: ["all-series", sort, size],
    initialPageParam: 1,
    queryFn: async ({ pageParam }) => unwrap(await api.GET("/api/v1/feed", {
      params: { query: { sort: sort === "latest" ? "updated" : sort, page: pageParam, size } },
    })),
    getNextPageParam: (last) => (sort !== "random" && last.pagination.page < last.pagination.pages ? last.pagination.page + 1 : undefined),
    staleTime: sort === "random" ? 0 : 2 * 60_000,
  });
}
