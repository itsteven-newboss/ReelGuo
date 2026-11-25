import { describe, expect, it } from "vitest";
import id from "./id";
import ptBR from "./pt-BR";
import vi from "./vi";
import zhCN from "./zh-CN";

/** Every text of a dictionary by dotted key (lists by index), plural forms (`_one`) under their base key. */
function flatten(value: unknown, prefix = "", out = new Map<string, string[]>()): Map<string, string[]> {
  if (typeof value === "string") {
    const key = prefix.replace(/_(one|many)$/, "");
    out.set(key, [...(out.get(key) ?? []), value]);
  } else if (Array.isArray(value)) {
    value.forEach((v, i) => flatten(v, `${prefix}.${i}`, out));
  } else if (value && typeof value === "object") {
    for (const [k, v] of Object.entries(value)) flatten(v, prefix ? `${prefix}.${k}` : k, out);
  }
  return out;
}

const placeholders = (s: string) => new Set([...s.matchAll(/\{\{(\w+)\}\}/g)].map((m) => m[1]!));
const tags = (s: string) => [...s.matchAll(/<\/?(\w+)\s*\/?>/g)].map((m) => m[0]!.replace(/\s/g, "")).sort().join(" ");
// A translation may leave out the site's name; every other value must stay, and none be added.
const optional = new Set(["brand", "short"]);

describe.each([["id", id], ["vi", vi], ["pt-BR", ptBR]] as const)("%s dictionary", (_, dict) => {
  const source = flatten(zhCN);
  const target = flatten(dict);

  it("has every text of the Chinese one, and lists of the same length", () => {
    expect([...target.keys()].sort()).toEqual([...source.keys()].sort());
  });

  it("keeps the values and markup of each text", () => {
    for (const [key, [zh]] of source) {
      for (const text of target.get(key) ?? []) {
        const want = placeholders(zh!);
        const got = placeholders(text);
        for (const p of got) expect(want.has(p), `${key}: unknown {{${p}}} in "${text}"`).toBe(true);
        for (const p of want) if (!optional.has(p)) expect(got.has(p), `${key}: "${text}" lost {{${p}}}`).toBe(true);
        expect(tags(text), `${key}: markup of "${text}"`).toBe(tags(zh!));
      }
    }
  });

  it("has no Chinese left", () => {
    for (const [key, texts] of target) for (const t of texts) expect(/[一-鿿]/.test(t), `${key}: "${t}"`).toBe(false);
  });
});
