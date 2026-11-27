import { createReelGuoClient, unwrap, type Schemas } from "@reelguo/api-client";
import { QueryClient, useQuery } from "@tanstack/react-query";

export type AdminSession = Schemas["AdminSessionDto"];

export const queryClient = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: false, retry: false } },
});

/**
 * Admin API client. The session is an httpOnly cookie set by /api/admin/auth/login; every request
 * also carries the CSRF header the API requires on state-changing calls.
 */
export const CSRF_HEADER = "X-ReelGuo-Admin";

export const api = createReelGuoClient({
  headers: () => ({ [CSRF_HEADER]: "1" }),
  onError(status) {
    if (status === 401) queryClient.setQueryData(SESSION_KEY, null);
  },
});

const SESSION_KEY = ["admin", "session"];

export function useSession() {
  return useQuery({
    queryKey: SESSION_KEY,
    queryFn: async (): Promise<AdminSession | null> => {
      const result = await api.GET("/api/admin/session");
      return result.response.status === 401 ? null : unwrap(result);
    },
    staleTime: Infinity,
  });
}

export function setSession(session: AdminSession | null) {
  queryClient.setQueryData(SESSION_KEY, session);
}

/**
 * Signs out, then shows the login form. The session entry is set to null, never removed: App watches that entry, and
 * queryClient.clear() would detach it (the page then stayed on the console until a reload). The other cached data
 * (the previous admin's lists) is dropped. The form comes back even if the server can't be reached.
 */
export async function signOut() {
  try {
    await api.POST("/api/admin/auth/logout");
  } finally {
    setSession(null);
    queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== SESSION_KEY[0] || query.queryKey[1] !== SESSION_KEY[1] });
  }
}
