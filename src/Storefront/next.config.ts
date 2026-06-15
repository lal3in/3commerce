import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Self-contained server bundle for a slim production container (BL-10).
  output: "standalone",
  // Product images come from the seed importer (picsum) in dev; real CDNs added later.
  images: {
    remotePatterns: [{ protocol: "https", hostname: "picsum.photos" }],
  },
};

export default nextConfig;
