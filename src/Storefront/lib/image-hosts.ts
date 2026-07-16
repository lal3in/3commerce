// Remote image hosts next/image is allowed to optimize. Single source of truth shared by
// next.config's `images.remotePatterns` and the runtime SafeImage guard so the two never drift.
// picsum.photos: bulk catalog seed. placehold.co: E2E-scenario demo products (--data full).
export const ALLOWED_IMAGE_HOSTS = ["picsum.photos", "placehold.co"] as const;

/**
 * True when `src` is something next/image can render without throwing: a root-relative path
 * (served by the app itself) or a remote URL whose host is allow-listed above. Anything else —
 * an unexpected CDN, a malformed URL — returns false so callers can degrade instead of 500ing.
 */
export function isAllowedImageSrc(src: string | null | undefined): boolean {
  if (!src) return false;
  if (src.startsWith("/")) return true;
  try {
    return (ALLOWED_IMAGE_HOSTS as readonly string[]).includes(new URL(src).hostname);
  } catch {
    return false;
  }
}

// Hosts that serve SVG by default. next/image's optimizer refuses SVG (returns 400), so these must
// bypass the optimizer (rendered unoptimized) or the image comes back broken. placehold.co returns
// SVG unless an explicit raster extension is requested.
const SVG_HOSTS = ["placehold.co"];

export function servesSvg(src: string | null | undefined): boolean {
  if (!src || src.startsWith("/")) return false;
  try {
    return SVG_HOSTS.includes(new URL(src).hostname);
  } catch {
    return false;
  }
}
