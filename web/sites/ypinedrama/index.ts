// The site this build is for ("@reelguo/site", chosen by REELGUO_SITE in web/sites/plugin.mjs).
import type { SiteInfo } from "../types";
import site from "./site.json";

export const SITE = site as SiteInfo;
export { markAspect, markFiles } from "./mark/mark";
