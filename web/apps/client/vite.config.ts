import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
// Production response headers (also in infra/Caddyfile); `vite preview` serves the build with them.
import securityHeaders from "../../security-headers.json" with { type: "json" };
import { VitePWA } from "vite-plugin-pwa";
import pkg from "./package.json" with { type: "json" };

// In production the app and API share one origin (/api/* is routed to ASP.NET), so locally
// the dev server proxies the same paths to the API on :5080 (REELGUO_API_URL points it elsewhere).
// Node types aren't installed in this app; the config only reads one variable.
declare const process: { env: Record<string, string | undefined> };
const apiUrl = process.env.REELGUO_API_URL ?? "http://localhost:5080";
const apiProxy = { "/api": apiUrl, "/media": apiUrl, "/health": apiUrl };

const previewHeaders = Object.fromEntries(Object.entries(securityHeaders).filter(([k]) => !k.startsWith("//")));

export default defineConfig({
  define: { __APP_VERSION__: JSON.stringify(pkg.version) },
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      registerType: "autoUpdate",
      includeAssets: ["favicon.ico", "icons/apple-touch-icon.png"],
      manifest: {
        // The name under the installed app's icon (src/brand.ts has the same).
        name: "黄约短剧",
        short_name: "黄约短剧",
        description: "黄约短剧 · AI 短剧与原创短剧在线观看",
        lang: "zh-CN",
        start_url: "/",
        display: "standalone",
        orientation: "portrait",
        theme_color: "#000000",
        background_color: "#000000",
        // Generated from the brand mark by scripts/brand-icons.mjs (`pnpm brand:icons`).
        icons: [
          { src: "icons/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
          { src: "icons/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
          { src: "icons/maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
        ],
      },
      workbox: {
        // Cache the app shell only. API responses and video are never cached by the service worker.
        navigateFallback: "/index.html",
        navigateFallbackDenylist: [/^\/api\//, /^\/media\//, /^\/health/],
      },
    }),
  ],
  server: { port: 5173, strictPort: true, proxy: apiProxy },
  preview: { port: 4173, strictPort: true, proxy: apiProxy, headers: previewHeaders },
});
