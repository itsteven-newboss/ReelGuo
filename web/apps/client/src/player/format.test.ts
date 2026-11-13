import { describe, expect, it } from "vitest";
import { fmtAgo, fmtCount, fmtTime } from "./format";

describe("format", () => {
  it.each([[0, "0"], [9999, "9999"], [12000, "1.2万"], [10000, "1万"], [350_000_000, "3.5亿"]])("count %d", (n, s) => {
    expect(fmtCount(n)).toBe(s);
  });

  it.each([[0, "0:00"], [75, "1:15"], [3725, "1:02:05"], [-3, "0:00"], [Number.NaN, "0:00"]])("time %d", (n, s) => {
    expect(fmtTime(n)).toBe(s);
  });

  it("relative comment times", () => {
    const now = new Date(2026, 10, 10, 12, 0, 0).getTime();
    const ago = (s: number) => new Date(now - s * 1000).toISOString();
    expect(fmtAgo(ago(5), now)).toBe("刚刚");
    expect(fmtAgo(ago(5 * 60), now)).toBe("5分钟前");
    expect(fmtAgo(ago(3 * 3600), now)).toBe("3小时前");
    expect(fmtAgo(ago(2 * 86_400), now)).toBe("2天前");
    expect(fmtAgo(new Date(2026, 9, 2).toISOString(), now)).toBe("10-02");
    expect(fmtAgo(new Date(2025, 10, 2).toISOString(), now)).toBe("2025-11-02");
    expect(fmtAgo(new Date(now + 60_000).toISOString(), now)).toBe("刚刚");
  });
});
