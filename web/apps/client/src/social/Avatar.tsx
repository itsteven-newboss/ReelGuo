/** Round avatar: the account's colour with the first character of its nickname. */
export function Avatar({ name, color, size = 32 }: { name: string; color?: string | null; size?: number }) {
  const initial = Array.from(name.trim())[0] ?? "我";
  return (
    <span aria-hidden className="rounded-full shrink-0 inline-flex items-center justify-center font-bold text-white select-none"
      style={{ width: size, height: size, fontSize: Math.round(size * 0.42), background: color || "var(--p-s4)" }}>
      {initial}
    </span>
  );
}
