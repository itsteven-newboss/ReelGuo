import { BrandMark } from "@reelguo/ui";
import { useEffect, useState } from "react";
import { Trans } from "react-i18next";
import { t } from "../i18n";
import { errorMessage, useAuth } from "../state/auth";
import { useUi } from "../state/ui";
import { CloseButton, Modal } from "../ui/Overlays";

const USERNAME = /^[A-Za-z0-9_]{3,20}$/;
const input = "w-full h-[42px] px-5 rounded-full bg-white/[.07] border border-white/[.08] text-[14px] text-on placeholder:text-dim outline-none focus:border-primary";

/** 登录 / 注册: a bottom sheet on phones, a centred dialog on desktop (UI-REFERENCE §12). */
export function LoginSheet(_props: { wide?: boolean }) {
  const open = useAuth((s) => s.loginOpen);
  const { closeLogin, login, register } = useAuth();
  const toast = useUi((s) => s.toast);
  const [mode, setMode] = useState<"login" | "register">("login");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [nickname, setNickname] = useState("");
  const [adult, setAdult] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState("");

  useEffect(() => {
    if (!open) {
      setPassword("");
      setErr("");
    }
  }, [open]);

  const submit = async () => {
    const name = username.trim();
    if (!name || !password) return setErr(t("auth.missing"));
    if (mode === "register") {
      if (!USERNAME.test(name)) return setErr(t("errors.invalid_username"));
      if (password.length < 8) return setErr(t("auth.passwordShort"));
      if (!adult) return setErr(t("errors.terms_required"));
    }
    setBusy(true);
    setErr("");
    try {
      const user = mode === "login"
        ? await login(name, password)
        : await register({ username: name, password, nickname: nickname.trim(), acceptTerms: adult });
      closeLogin();
      toast(mode === "login" ? t("auth.welcomeBack", { name: user.nickname }) : t("auth.registered"), "ok");
    } catch (e) {
      setErr(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const label = "block text-[14px] font-bold text-on mb-2";
  const body = (
    <form className="relative px-6 pb-6 pt-7" onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <span className="absolute right-4 top-4"><CloseButton onClick={closeLogin} /></span>
      <div className="flex justify-center"><BrandMark fit={60} /></div>
      <h2 className="mt-4 text-center text-[20px] font-bold text-on">{t("auth.title")}</h2>

      <div className="mt-5 flex bg-white/[.06] rounded-full p-1" role="tablist">
        {([["login", t("auth.login")], ["register", t("auth.register")]] as const).map(([k, l]) => (
          <button key={k} type="button" role="tab" aria-selected={mode === k} onClick={() => { setMode(k); setErr(""); }}
            className={`flex-1 h-9 rounded-full text-[14px] font-semibold ${mode === k ? "bg-primary text-on-primary" : "text-mut hover:text-on"}`}>{l}</button>
        ))}
      </div>

      <div className="mt-5 flex flex-col gap-4">
        <label><span className={label}>{t("auth.username")}</span>
          <input className={input} value={username} onChange={(e) => setUsername(e.target.value)} placeholder={t("auth.usernamePlaceholder")}
            autoComplete="username" autoCapitalize="none" spellCheck={false} maxLength={20} /></label>
        <label><span className={label}>{t("auth.password")}</span>
          <input className={input} value={password} onChange={(e) => setPassword(e.target.value)} type="password" placeholder={t("auth.passwordPlaceholder")}
            autoComplete={mode === "login" ? "current-password" : "new-password"} maxLength={128} /></label>
        {mode === "register" && (
          <>
            <label><span className={label}>{t("auth.nickname")}</span>
              <input className={input} value={nickname} onChange={(e) => setNickname(e.target.value)} placeholder={t("auth.nicknamePlaceholder")} maxLength={20} /></label>
            <label className="flex items-start gap-2 text-[12px] text-mut leading-relaxed cursor-pointer">
              <input type="checkbox" checked={adult} onChange={(e) => setAdult(e.target.checked)} className="mt-[3px] accent-[var(--p-primary)]" />
              <span>
                <Trans i18nKey="auth.adultTerms" components={{
                  terms: <a href="/terms" target="_blank" rel="noreferrer" className="text-primary" />,
                  privacy: <a href="/privacy" target="_blank" rel="noreferrer" className="text-primary" />,
                }} />
              </span>
            </label>
          </>
        )}
        {err && <div role="alert" className="text-[12.5px] text-danger text-center">{err}</div>}
        <p className="text-center text-[12.5px] text-on/85">{t("auth.hint")}</p>
        <button type="submit" disabled={busy}
          className="h-11 rounded-full bg-primary text-on-primary text-[15px] font-bold disabled:opacity-60">
          {busy ? t("common.processing") : mode === "login" ? t("auth.login") : t("auth.registerAndLogin")}
        </button>
      </div>
    </form>
  );

  return <Modal open={open} onClose={closeLogin} layer="z-[75]" width={400}>{body}</Modal>;
}
