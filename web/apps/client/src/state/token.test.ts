import { afterEach, describe, expect, it, vi } from "vitest";
import { accessExpiring, accessToken, APP_HEADER, clearAccess, onRefresh, refreshAccess, setAccess } from "./token";

const okBody = { access_token: "tok-2", expires_in: 900, user: { id: 1, username: "u", nickname: "n", avatar_color: "#fff", sign: null, created_at: "" } };

afterEach(() => {
  vi.unstubAllGlobals();
  clearAccess();
});

describe("token refresh", () => {
  it("shares one request between concurrent callers and stores the new token", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(okBody), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const seen: string[] = [];
    const off = onRefresh((r) => seen.push(r.kind));

    const [a, b] = await Promise.all([refreshAccess(), refreshAccess()]);
    off();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect((init.headers as Record<string, string>)[APP_HEADER]).toBe("1");
    expect(a).toBe(b);
    expect(a.kind).toBe("ok");
    expect(accessToken()).toBe("tok-2");
    expect(seen).toEqual(["ok"]);
  });

  it("treats 401 as signed out and clears the token", async () => {
    setAccess("old", 900);
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 401 })));
    expect((await refreshAccess()).kind).toBe("signed_out");
    expect(accessToken()).toBeNull();
  });

  it("keeps the token when the network fails", async () => {
    setAccess("old", 900);
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("offline"); }));
    expect((await refreshAccess()).kind).toBe("offline");
    expect(accessToken()).toBe("old");
  });

  it("flags a token that expires within a minute", () => {
    setAccess("t", 30);
    expect(accessExpiring()).toBe(true);
    setAccess("t", 900);
    expect(accessExpiring()).toBe(false);
  });
});
