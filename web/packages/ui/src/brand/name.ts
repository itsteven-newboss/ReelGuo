import { SITE } from "@reelguo/site";

/**
 * The site's name as visitors see it, for the web app and the admin console: web/sites/<key>/site.json of the site the
 * build is for (REELGUO_SITE; the server gets the same from its Brand settings). "ReelGuo" stays only where software
 * reads it: the app's user agent, headers, app_name in /api/v1/config, code names.
 */
export const BRAND = SITE.name;

/** Short form inside compound labels: 黄月原创, 黄月APP, 黄月吃瓜. The full name for sites whose language has none. */
export const BRAND_SHORT = SITE.short;

/** The site's address without https://, as the logo shows it. */
export const BRAND_DOMAIN = SITE.domain;
