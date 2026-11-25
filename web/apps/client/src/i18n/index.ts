import { ApiError } from "@reelguo/api-client";
import { SITE, uiText } from "@reelguo/ui";
import i18n from "i18next";
import { initReactI18next } from "react-i18next";
// The site's language only (web/sites/plugin.mjs points this at src/i18n/<locale>.ts): one dictionary per build.
import dictionary from "@reelguo/locale";

/** The site's language (web/sites/<key>/site.json): zh-CN, id, vi or pt-BR. Fixed for a build. */
export const locale = SITE.locale;

void i18n.use(initReactI18next).init({
  lng: locale,
  fallbackLng: false,
  resources: { [locale]: { translation: dictionary } },
  interpolation: { escapeValue: false }, // React already escapes.
  returnNull: false,
  returnEmptyString: true,
  // The dictionary is bundled, so everything is ready at once: module-level t() calls work.
  initAsync: false,
});

uiText.loading = i18n.t("common.loading");

/** For code outside components (and lists built once): the language never changes while the app runs. */
export const t = (key: string, options?: Record<string, unknown>): string => String(i18n.t(key, (options ?? {}) as never));

/** A list from the dictionary (FAQ items and the like), with its strings interpolated. */
export const tList = <T,>(key: string, options?: Record<string, unknown>): T[] =>
  i18n.t(key, { ...options, returnObjects: true } as never) as unknown as T[];

/** One question and answer of a page's FAQ (`t(key, { returnObjects: true })`). */
export type Faq = { q: string; a: string };

/**
 * What to tell the viewer about a failed request: the server's own message on the Chinese site (it writes Chinese), the
 * translation of its error code elsewhere (errors.<code>), and a network error when there was no answer.
 */
export function apiErrorText(e: unknown): string {
  if (!(e instanceof ApiError)) return t("errors.network");
  if (locale.startsWith("zh") && e.message) return e.message;
  return i18n.exists(`errors.${e.code}`) ? t(`errors.${e.code}`) : t("errors.generic");
}

export default i18n;
