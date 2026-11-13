import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
// Production response headers (also in infra/Caddyfile); `vite preview` serves the build with them.
import securityHeaders from "../../security-headers.json" with { type: "json" };

// Node types aren't installed in this app; the config only reads one variable.
declare const process: { env: Record<string, string | undefined> };

// The API on :5080 by default; REELGUO_API_URL points the dev proxy elsewhere.
const apiUrl = process.env.REELGUO_API_URL ?? "http://localhost:5080";
const apiProxy = { "/api": apiUrl, "/media": apiUrl };

const previewHeaders = Object.fromEntries(Object.entries(securityHeaders).filter(([k]) => !k.startsWith("//")));

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { port: 5174, strictPort: true, proxy: apiProxy },
  preview: { port: 4174, strictPort: true, proxy: apiProxy, headers: previewHeaders },
});
