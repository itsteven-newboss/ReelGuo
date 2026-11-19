import { describe, expect, it } from "vitest";
import { statusBadge } from "./badges";

describe("statusBadge", () => {
  it("follows the admin's choice", () => {
    expect(statusBadge("new", true, false)).toBe("new");
    expect(statusBadge("hot", false, true)).toBe("hot");
    expect(statusBadge("none", true, true)).toBeNull();
  });

  it("is automatic for older series: 热播 first, then 新片 in the first week", () => {
    expect(statusBadge("auto", true, true)).toBe("hot");
    expect(statusBadge("auto", false, true)).toBe("new");
    expect(statusBadge("auto", false, false)).toBeNull();
    expect(statusBadge(undefined, false, true)).toBe("new");
  });
});
