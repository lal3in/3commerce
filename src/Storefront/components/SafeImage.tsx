import Image, { type ImageProps } from "next/image";
import { isAllowedImageSrc } from "@/lib/image-hosts";

type SafeImageProps = Omit<ImageProps, "src"> & { src: string };

/**
 * next/image throws at render time for any remote host not listed in next.config's
 * `images.remotePatterns`, which turns a single stray image URL (e.g. an E2E-scenario seed
 * image on an unexpected CDN) into a full-page 500. SafeImage optimizes allow-listed hosts and
 * falls back to a plain <img> for everything else, so an unknown image URL degrades to an
 * unoptimized load instead of crashing the whole storefront page.
 */
export function SafeImage({ src, alt, fill, className, style, priority, ...rest }: SafeImageProps) {
  if (!isAllowedImageSrc(src)) {
    return (
      // eslint-disable-next-line @next/next/no-img-element -- deliberate unoptimized fallback for non-allow-listed hosts
      <img
        src={src}
        alt={alt}
        className={className}
        loading={priority ? "eager" : "lazy"}
        style={fill ? { position: "absolute", inset: 0, height: "100%", width: "100%", ...style } : style}
      />
    );
  }
  return <Image src={src} alt={alt} fill={fill} className={className} style={style} priority={priority} {...rest} />;
}
