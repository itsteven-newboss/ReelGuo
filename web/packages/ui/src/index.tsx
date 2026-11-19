import { useState, type CSSProperties, type ReactNode } from "react";
import { markAspect, markFiles } from "./brand/mark";

export { BRAND, BRAND_SHORT } from "./brand/name";

export { markAspect };

/**
 * The brand mark (黄月短剧). Give its displayed width or height in CSS px, or `fit`: the largest size that fits a square
 * of that many px whatever the mark's shape. The browser picks the sharpest of the generated files for that size and
 * the screen's pixel density. To change the mark: `pnpm brand:icons`.
 */
export function BrandMark({ width, height, fit, className, style }: { width?: number; height?: number; fit?: number; className?: string; style?: CSSProperties }) {
  if (fit !== undefined) {
    width = markAspect >= 1 ? fit : fit * markAspect;
    height = undefined;
  }
  const w = Math.round(width ?? (height ?? 32) * markAspect);
  const h = Math.round(height ?? w / markAspect);
  return (
    <img src={(markFiles[1] ?? markFiles[0])?.url} srcSet={markFiles.map((f) => `${f.url} ${f.width}w`).join(", ")} sizes={`${w}px`}
      width={w} height={h} alt="" decoding="async" draggable={false} className={className} style={style} />
  );
}

export function Spinner({ size = 32 }: { size?: number }) {
  return <div className="spinner" style={{ width: size, height: size }} role="status" aria-label="加载中" />;
}

/** Empty / error state: icon tile, title, optional detail and one action. */
export function Empty({ icon, title, sub, action, onAction }: {
  icon: ReactNode;
  title: string;
  sub?: string;
  action?: string;
  onAction?: () => void;
}) {
  return (
    <div className="flex flex-col items-center justify-center py-16 px-8 text-center a-rise">
      <div className="w-16 h-16 rounded-(--r-md) tonal flex items-center justify-center mb-4 text-primary">{icon}</div>
      <div className="text-[15px] font-semibold text-on/90">{title}</div>
      {sub && <div className="text-xs text-mut mt-1.5 leading-relaxed">{sub}</div>}
      {action && (
        <button onClick={onAction} className="mt-5 px-6 h-10 rounded-(--r-ctl) bg-primary text-on-primary text-[13px] font-bold pressable">
          {action}
        </button>
      )}
    </div>
  );
}

/** Cover image that fills its (relatively positioned) parent, with a shimmer while loading. */
export function Img({ src, alt = "", eager = false }: { src?: string | null; alt?: string; eager?: boolean }) {
  const [loaded, setLoaded] = useState(false);
  const [failed, setFailed] = useState(!src);
  return (
    <div className="absolute inset-0 overflow-hidden">
      {!loaded && !failed && <div className="absolute inset-0 shimmer" />}
      {failed && <div className="absolute inset-0 bg-s3" />}
      {src && (
        <img
          src={src}
          alt={alt}
          loading={eager ? "eager" : "lazy"}
          draggable={false}
          onLoad={() => setLoaded(true)}
          onError={() => setFailed(true)}
          className={`absolute inset-0 w-full h-full object-cover transition-opacity duration-300 ${loaded ? "opacity-100" : "opacity-0"}`}
        />
      )}
    </div>
  );
}
