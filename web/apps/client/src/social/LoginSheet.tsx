import { useEffect, useState } from "react";
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
    if (!name || !password) return setErr("请输入账号与密码");
    if (mode === "register") {
      if (!USERNAME.test(name)) return setErr("账号需为 3-20 位字母、数字或下划线");
      if (password.length < 8) return setErr("密码至少 8 位");
      if (!adult) return setErr("请先确认已年满 18 周岁并同意用户协议");
    }
    setBusy(true);
    setErr("");
    try {
      const user = mode === "login"
        ? await login(name, password)
        : await register({ username: name, password, nickname: nickname.trim(), acceptTerms: adult });
      closeLogin();
      toast(mode === "login" ? `欢迎回来，${user.nickname}` : "注册成功", "ok");
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
      <div className="flex justify-center"><img src="/icon.svg" alt="" className="w-12 h-12 rounded-[14px] shadow-[0_0_40px_var(--p-glow)]" /></div>
      <h2 className="mt-4 text-center text-[20px] font-bold text-on">用户登录注册</h2>

      <div className="mt-5 flex bg-white/[.06] rounded-full p-1" role="tablist">
        {([["login", "登录"], ["register", "注册"]] as const).map(([k, l]) => (
          <button key={k} type="button" role="tab" aria-selected={mode === k} onClick={() => { setMode(k); setErr(""); }}
            className={`flex-1 h-9 rounded-full text-[14px] font-semibold ${mode === k ? "bg-primary text-on-primary" : "text-mut hover:text-on"}`}>{l}</button>
        ))}
      </div>

      <div className="mt-5 flex flex-col gap-4">
        <label><span className={label}>账号</span>
          <input className={input} value={username} onChange={(e) => setUsername(e.target.value)} placeholder="请输入账号"
            autoComplete="username" autoCapitalize="none" spellCheck={false} maxLength={20} /></label>
        <label><span className={label}>密码</span>
          <input className={input} value={password} onChange={(e) => setPassword(e.target.value)} type="password" placeholder="请输入密码"
            autoComplete={mode === "login" ? "current-password" : "new-password"} maxLength={128} /></label>
        {mode === "register" && (
          <>
            <label><span className={label}>昵称（可选）</span>
              <input className={input} value={nickname} onChange={(e) => setNickname(e.target.value)} placeholder="请输入昵称" maxLength={20} /></label>
            <label className="flex items-start gap-2 text-[12px] text-mut leading-relaxed cursor-pointer">
              <input type="checkbox" checked={adult} onChange={(e) => setAdult(e.target.checked)} className="mt-[3px] accent-[var(--p-primary)]" />
              <span>
                我已年满 18 周岁，并同意
                <a href="/terms" target="_blank" rel="noreferrer" className="text-primary">《用户协议》</a>
                <a href="/privacy" target="_blank" rel="noreferrer" className="text-primary">《隐私政策》</a>
              </span>
            </label>
          </>
        )}
        {err && <div role="alert" className="text-[12.5px] text-danger text-center">{err}</div>}
        <p className="text-center text-[12.5px] text-on/85">登录后可使用收藏、评论和个人中心功能。</p>
        <button type="submit" disabled={busy}
          className="h-11 rounded-full bg-primary text-on-primary text-[15px] font-bold disabled:opacity-60">
          {busy ? "处理中…" : mode === "login" ? "登录" : "注册并登录"}
        </button>
      </div>
    </form>
  );

  return <Modal open={open} onClose={closeLogin} layer="z-[75]" width={400}>{body}</Modal>;
}
