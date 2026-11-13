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

export async function signOut() {
  await api.POST("/api/admin/auth/logout");
  queryClient.clear();
  setSession(null);
}
