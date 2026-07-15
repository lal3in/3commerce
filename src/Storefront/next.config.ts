import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";
import { ALLOWED_IMAGE_HOSTS } from "./lib/image-hosts";

const nextConfig: NextConfig = {
  // Self-contained server bundle for a slim production container (BL-10).
  output: "standalone",
  // Optimize seed/demo image hosts (see lib/image-hosts). Unknown hosts don't 500 the page —
  // SafeImage degrades them to a plain <img> — but only these are run through the optimizer.
  images: {
    remotePatterns: ALLOWED_IMAGE_HOSTS.map((hostname) => ({ protocol: "https" as const, hostname })),
  },
};

// i18n_1: no locale URL segment — the request locale comes from the session cookie / storefront
// default / Accept-Language (i18n/request.ts), so /au, /products/... keep their existing shape.
export default createNextIntlPlugin("./i18n/request.ts")(nextConfig);
