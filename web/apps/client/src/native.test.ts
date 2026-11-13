import { describe, expect, it } from "vitest";
import { parseNativeApp } from "./native";

describe("native shell detection", () => {
  it("reads the version and platform the shell appends to the user agent", () => {
    expect(parseNativeApp("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/141 Edg/141 ReelGuoApp/0.6.0 (desktop)"))
      .toEqual({ version: "0.6.0", platform: "desktop" });
    expect(parseNativeApp("Mozilla/5.0 (Linux; Android 14; wv) AppleWebKit/537.36 Chrome/141 Mobile Safari/537.36 ReelGuoApp/1.2.3 (android)"))
      .toEqual({ version: "1.2.3", platform: "android" });
  });
  it("is null in an ordinary browser", () => {
    expect(parseNativeApp("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1")).toBeNull();
    expect(parseNativeApp("ReelGuoApp/x (desktop)")).toBeNull();
  });
});
