/** One site (web/sites/<key>/site.json): the same app under its own name, icons and language. */
export interface SiteInfo {
  /** Folder name and code name, e.g. "yelrama". */
  key: string;
  /** The name visitors see: page titles, the installed app, legal pages. */
  name: string;
  /** Short form inside compound labels (黄月原创); the full name where the language has no such form. */
  short: string;
  /** BCP 47 language of the viewer site (the admin console stays Chinese): zh-CN, id, vi, pt-BR. */
  locale: string;
  /** The site's address without https://, shown in the logo. */
  domain: string;
  /** Latin logo text in two parts, drawn white then yellow ("yel" + "rama"). Absent: the Chinese lockup. */
  wordmark?: [string, string];
  /** The installed app's description. */
  description: string;
  /** Source picture for scripts/brand-icons.mjs. */
  icon: string;
  iconStyle: "mark" | "tile";
  /** First-screen pictures of the app download page (public paths); without them it shows the icon and name. */
  heroArt?: { desktop: string; phone: string };
  /** Whether this site has its own desktop and Android apps (native/). */
  nativeApps: boolean;
}
