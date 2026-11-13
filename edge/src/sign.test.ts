import { describe, expect, it } from "vitest";
import { importKey, isSafeKey, isValid, seriesIdOf, sign, signedPrefixOf } from "./sign";

// Same key and vector as MediaUrlSignerTests.Known_signature_vector_matches_the_edge_worker (C#).
const KEY = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
const VECTOR = "syg9SyrK7lpbfqYO02OCB_7N2KfqhU-X7HwmRLfv0Fw";

describe("edge signing", () => {
  it("matches the API's signature for the shared test vector", async () => {
    expect(await sign(await importKey(KEY), 1800000000, "s/12/e/3/v2/")).toBe(VECTOR);
  });

  it("accepts every file inside the signed episode folder", async () => {
    const key = await importKey(KEY);
    expect(await isValid(key, 1800000000, VECTOR, "s/12/e/3/v2/master.m3u8", 1700000000)).toBe(true);
    expect(await isValid(key, 1800000000, VECTOR, "s/12/e/3/v2/r1/seg_0004.m4s", 1700000000)).toBe(true);
  });

  it.each(["s/12/e/4/v2/master.m3u8", "s/12/e/3/v1/master.m3u8", "p/covers/x.jpg", "master.m3u8"])("rejects %s", async (path) => {
    expect(await isValid(await importKey(KEY), 1800000000, VECTOR, path, 1700000000)).toBe(false);
  });

  it("rejects expired and tampered URLs", async () => {
    const key = await importKey(KEY);
    expect(await isValid(key, 1800000000, VECTOR, "s/12/e/3/v2/master.m3u8", 1800000001)).toBe(false);
    expect(await isValid(key, 1800000001, VECTOR, "s/12/e/3/v2/master.m3u8", 1700000000)).toBe(false);
    expect(await isValid(key, 1800000000, VECTOR.slice(0, -1) + "x", "s/12/e/3/v2/master.m3u8", 1700000000)).toBe(false);
  });

  it("extracts the episode prefix", () => {
    expect(signedPrefixOf("s/1/e/2/v3/r0/index.m3u8")).toBe("s/1/e/2/v3/");
    expect(signedPrefixOf("s/x/e/2/v3/r0/index.m3u8")).toBeNull();
  });

  it.each([["p/covers/1-ab.jpg", true], ["../x", false], ["a//b", false], ["a/./b", false], ["a b", false]])("safe key %s", (k, ok) => {
    expect(isSafeKey(k)).toBe(ok);
  });

  it("reads the series id for takedown checks", () => {
    expect(seriesIdOf("s/12/e/3/v2/720p/seg_001.m4s")).toBe(12);
    expect(seriesIdOf("p/covers/12-ab.jpg")).toBeNull();
    expect(seriesIdOf("s/x/e/3/v2/master.m3u8")).toBeNull();
  });
});
