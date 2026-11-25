// Builds the web app and admin console for one site (web/sites/<key>/site.json): REELGUO_SITE=yelrama pnpm build.
// Without REELGUO_SITE it is 黄月短剧 (huangyue). Each site gets its own name, icons and language; the code is the same.
//
//   - "@reelguo/site" (web/sites/<key>/index.ts): the site's facts and its mark, for the code (BRAND, BrandMark, i18n)
//   - public files: the app's own public/ plus web/sites/<key>/public/ (favicon, install icons), merged into one folder
//   - index.html: the site's language and name
// Plain JavaScript (with plugin.d.mts) because the web apps have no Node type definitions.
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const sitesDir = dirname(fileURLToPath(import.meta.url));
export const DEFAULT_SITE = "huangyue";

export function siteKey() {
  return (process.env.REELGUO_SITE || DEFAULT_SITE).trim().toLowerCase();
}

export function loadSite(key = siteKey()) {
  const file = join(sitesDir, key, "site.json");
  if (!/^[a-z0-9-]+$/.test(key) || !existsSync(file)) throw new Error(`REELGUO_SITE=${key}: there is no web/sites/${key}/site.json`);
  return JSON.parse(readFileSync(file, "utf8"));
}

const escapeHtml = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

/**
 * `htmlLang: false` keeps index.html's own language (the admin console is Chinese on every site). `locales`: the folder
 * (from the app root) with one dictionary per language, `<locale>.ts`; "@reelguo/locale" then is the site's.
 */
export function reelguoSite(site = loadSite(), { htmlLang = true, locales } = {}) {
  const siteDir = join(sitesDir, site.key);
  return {
    name: "reelguo-site",
    enforce: "pre",
    config(user) {
      const root = user.root ? resolve(user.root) : process.cwd();
      const own = user.publicDir === false ? null : resolve(root, user.publicDir || "public");
      const merged = join(root, "node_modules", ".reelguo-site", site.key, "public");
      rmSync(merged, { recursive: true, force: true });
      mkdirSync(merged, { recursive: true });
      if (own && existsSync(own)) cpSync(own, merged, { recursive: true });
      if (existsSync(join(siteDir, "public"))) cpSync(join(siteDir, "public"), merged, { recursive: true, force: true });
      const alias = { "@reelguo/site": join(siteDir, "index.ts") };
      if (locales) {
        const dictionary = join(root, locales, `${site.locale}.ts`);
        if (!existsSync(dictionary)) throw new Error(`${site.key}: there is no ${locales}/${site.locale}.ts`);
        alias["@reelguo/locale"] = dictionary;
      }
      return { publicDir: merged, resolve: { alias } };
    },
    transformIndexHtml(html) {
      const page = htmlLang ? html.replace(/<html lang="[^"]*">/, `<html lang="${escapeHtml(site.locale)}">`) : html;
      return page.replaceAll("__BRAND__", escapeHtml(site.name));
    },
  };
}
