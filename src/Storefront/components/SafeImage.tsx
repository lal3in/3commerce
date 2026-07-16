"use client";

import Image, { type ImageProps } from "next/image";
import { useState } from "react";
import { isAllowedImageSrc, servesSvg } from "@/lib/image-hosts";

type SafeImageProps = Omit<ImageProps, "src"> & { src: string };

// Neutral local placeholder (inline, no network) shown when an image fails to load — so a
// blocked/offline/rejected remote image degrades to a placeholder instead of a broken-link icon.
const PLACEHOLDER =
  "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='800' height='600' viewBox='0 0 800 600'%3E%3Crect width='800' height='600' fill='%23f3f4f6'/%3E%3Cg fill='none' stroke='%23cbd5e1' stroke-width='8'%3E%3Crect x='300' y='230' width='200' height='150' rx='10'/%3E%3Ccircle cx='355' cy='285' r='18'/%3E%3Cpath d='M320 370l55-55 45 40 40-35 40 50' stroke-linejoin='round'/%3E%3C/g%3E%3C/svg%3E";

/**
 * next/image throws at render time for any remote host not listed in next.config's
 * `images.remotePatterns`, and its optimizer 400s on hosts that serve SVG (e.g. placehold.co) —
 * either way the shopper sees a broken image. SafeImage:
 *  - bypasses the optimizer (`unoptimized`) for allow-listed SVG hosts so the real image still loads,
 *  - degrades non-allow-listed hosts to a plain <img>,
 *  - and, on ANY load error, swaps to a local inline placeholder so a broken-link icon never shows.
 */
export function SafeImage({ src, alt, fill, className, style, priority, ...rest }: SafeImageProps) {
  const [failed, setFailed] = useState(false);

  // On failure (or for non-allow-listed hosts) render a plain <img>; a data-URI placeholder never errors.
  if (failed || !isAllowedImageSrc(src)) {
    return (
      // eslint-disable-next-line @next/next/no-img-element -- deliberate unoptimized fallback
      <img
        src={failed ? PLACEHOLDER : src}
        alt={alt}
        className={className}
        loading={priority ? "eager" : "lazy"}
        onError={() => setFailed(true)}
        style={fill ? { position: "absolute", inset: 0, height: "100%", width: "100%", objectFit: "cover", ...style } : style}
      />
    );
  }

  return (
    <Image
      src={src}
      alt={alt}
      fill={fill}
      className={className}
      style={style}
      priority={priority}
      unoptimized={servesSvg(src)}
      onError={() => setFailed(true)}
      {...rest}
    />
  );
}
