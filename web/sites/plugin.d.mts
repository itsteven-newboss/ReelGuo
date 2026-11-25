import type { Plugin } from "vite";
import type { SiteInfo } from "./types";

export declare const DEFAULT_SITE: string;
export declare function siteKey(): string;
export declare function loadSite(key?: string): SiteInfo;
export declare function reelguoSite(site?: SiteInfo, options?: { htmlLang?: boolean; locales?: string }): Plugin;
