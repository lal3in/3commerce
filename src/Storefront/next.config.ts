import type { NextConfig } from "next";
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

export default nextConfig;
