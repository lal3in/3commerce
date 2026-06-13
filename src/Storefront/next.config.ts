import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Product images come from the seed importer (picsum) in dev; real CDNs added later.
  images: {
    remotePatterns: [{ protocol: "https", hostname: "picsum.photos" }],
  },
};

export default nextConfig;
