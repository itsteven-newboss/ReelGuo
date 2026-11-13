import type { Schemas } from "@reelguo/api-client";

/**
 * The viewer's access token lives only in memory (never in storage), so a script that reads storage can't steal
 * it. The long-lived refresh token is an httpOnly cookie scoped to /api/v1/auth that scripts can't read at all.
 */
export type AuthResponse = Schemas["AuthResponse"];

/** Required on the cookie-authenticated /auth endpoints (CSRF protection). */
export const APP_HEADER = "X-ReelGuo-App";

let access: { token: string; expiresAt: number } | null = null;

export function accessToken(): string | null {
  return access?.token ?? null;
}

/** True when the token expires within a minute, so it's refreshed before use instead of failing with 401. */
export function accessExpiring(): boolean {
  return !!access && access.expiresAt - Date.now() < 60_000;
}

export function setAccess(token: string, expiresInSeconds: number) {
  access = { token, expiresAt: Date.now() + expiresInSeconds * 1000 };
}

export function clearAccess() {
  access = null;
}

export type RefreshResult =
  | { kind: "ok"; auth: AuthResponse }
  /** No valid session (never signed in, signed out elsewhere, expired, disabled). */
  | { kind: "signed_out" }
  /** Network trouble: keep the current state and try again later. */
  | { kind: "offline" };

const listeners = new Set<(r: RefreshResult) => void>();

/** The auth store listens so a refresh anywhere (boot, a 401 retry) updates the signed-in user. */
export function onRefresh(listener: (r: RefreshResult) => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

let inflight: Promise<RefreshResult> | null = null;

/** Exchanges the refresh cookie for a new access token. Concurrent callers share one request. */
export function refreshAccess(): Promise<RefreshResult> {
  inflight ??= (async (): Promise<RefreshResult> => {
    let result: RefreshResult;
    try {
      const res = await fetch("/api/v1/auth/refresh", { method: "POST", headers: { [APP_HEADER]: "1" }, credentials: "same-origin" });
      if (res.ok) {
        const auth = (await res.json()) as AuthResponse;
        setAccess(auth.access_token, auth.expires_in);
        result = { kind: "ok", auth };
      } else if (res.status === 401 || res.status === 403) {
        clearAccess();
        result = { kind: "signed_out" };
      } else {
        result = { kind: "offline" };
      }
    } catch {
      result = { kind: "offline" };
    }
    for (const l of listeners) l(result);
    return result;
  })().finally(() => {
    inflight = null;
  });
  return inflight;
}
