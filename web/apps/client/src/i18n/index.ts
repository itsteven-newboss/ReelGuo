import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import zhCN from "./zh-CN";

export const defaultLocale = "zh-CN";

void i18n.use(initReactI18next).init({
  lng: defaultLocale,
  fallbackLng: defaultLocale,
  resources: { [defaultLocale]: { translation: zhCN } },
  interpolation: { escapeValue: false }, // React already escapes.
  returnNull: false,
});

export default i18n;
