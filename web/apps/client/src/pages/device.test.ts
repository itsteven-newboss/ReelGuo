import { describe, expect, it } from "vitest";
import { detectDevice, installHint } from "./device";

// The same user agents as backend/tests/ReelGuo.Api.Tests/AppDownloadTests.cs.
const UA = {
  androidChrome: "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36",
  iphoneSafari: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1",
  iphoneChrome: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0.6723.90 Mobile/15E148 Safari/604.1",
  windowsEdge: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0",
  macSafari: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15",
  weChatAndroid: "Mozilla/5.0 (Linux; Android 13; V2148A Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/116.0.0.0 Mobile Safari/537.36 XWEB/1160065 MMWEBSDK/20231202 MMWEBID/2247 MicroMessenger/8.0.47.2560(0x28002F30) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64",
  qqAppIphone: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 QQ/9.0.0.611 V1_IPH_SQ_9.0.0_1_APP_A Core/WKWebView Device/Apple(iPhone 12) NetType/WIFI",
  qqBrowser: "Mozilla/5.0 (Linux; U; Android 12; zh-cn; PDEM10 Build/SKQ1.211019.001) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/109.0.5414.86 MQQBrowser/14.4 Mobile Safari/537.36",
  harmonyNext: "Mozilla/5.0 (Phone; OpenHarmony 5.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36 ArkWeb/4.1.6.1 Mobile HuaweiBrowser/5.0.4.300",
  linuxFirefox: "Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0",
  chromeOs: "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
};

describe("detectDevice", () => {
  it("reads the system", () => {
    expect(detectDevice(UA.androidChrome).os).toBe("android");
    expect(detectDevice(UA.iphoneSafari).os).toBe("ios");
    expect(detectDevice(UA.windowsEdge).os).toBe("windows");
    expect(detectDevice(UA.macSafari).os).toBe("mac");
    expect(detectDevice(UA.harmonyNext).os).toBe("harmony");
    expect(detectDevice(UA.linuxFirefox).os).toBe("linux");
    expect(detectDevice(UA.chromeOs).os).toBe("other");
    expect(detectDevice("").os).toBe("other");
  });

  it("tells an iPad asking for the desktop site by its touch screen", () => {
    expect(detectDevice(UA.macSafari, 5).os).toBe("ios");
    expect(detectDevice(UA.macSafari, 5).safari).toBe(true);
    expect(detectDevice(UA.macSafari, 0).os).toBe("mac");
  });

  it("knows chat apps' browsers, which block downloads", () => {
    expect(detectDevice(UA.weChatAndroid)).toMatchObject({ os: "android", inApp: true, weChat: true });
    expect(detectDevice(UA.qqAppIphone)).toMatchObject({ os: "ios", inApp: true, weChat: false, safari: false });
    expect(detectDevice(UA.qqBrowser).inApp).toBe(false);
    expect(detectDevice(UA.androidChrome).inApp).toBe(false);
  });

  it("knows Safari from the other iPhone browsers", () => {
    expect(detectDevice(UA.iphoneSafari).safari).toBe(true);
    expect(detectDevice(UA.iphoneChrome).safari).toBe(false);
    expect(detectDevice(UA.androidChrome).safari).toBe(false);
  });
});

describe("installHint", () => {
  it("accepts only the hints /app/download sends", () => {
    expect(installHint("?install=ios")).toBe("ios");
    expect(installHint("?install=mac-arm&x=1")).toBe("mac-arm");
    expect(installHint("?install=../../x")).toBeNull();
    expect(installHint("")).toBeNull();
  });
});
