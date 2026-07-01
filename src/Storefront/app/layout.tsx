import type { Metadata } from "next";
import Link from "next/link";
import "./globals.css";
import ConsentBanner from "@/components/consent/ConsentBanner";
import { organizationJsonLd, siteUrl, webSiteJsonLd } from "@/lib/seo";
import { ThemeStyle } from "@/components/theme/ThemeStyle";
import { mergeTheme } from "@/lib/theme";

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl()),
  title: { default: "3commerce", template: "%s · 3commerce" },
  description: "A from-scratch e-commerce storefront.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  // Per-storefront theme overrides (sanitized) merge over the default; tenant config wiring is mt5_6 follow-up.
  const theme = mergeTheme(null);
  return (
    <html lang="en">
      <head>
        <ThemeStyle theme={theme} />
      </head>
      <body
        className="min-h-screen flex flex-col"
        style={{ background: "var(--color-bg)", color: "var(--color-text)", fontFamily: "var(--font-sans)" }}
      >
        <script
          type="application/ld+json"
          // Site-wide Organization JSON-LD (mt5_8). Emit one object per script for validators/extensions.
          dangerouslySetInnerHTML={{ __html: JSON.stringify(organizationJsonLd()) }}
        />
        <script
          type="application/ld+json"
          // Site-wide WebSite + SearchAction JSON-LD (mt5_8).
          dangerouslySetInnerHTML={{ __html: JSON.stringify(webSiteJsonLd()) }}
        />
        <header className="border-b border-neutral-200">
          <div className="mx-auto max-w-6xl px-4 h-16 flex items-center justify-between gap-6">
            <Link href="/" className="text-xl font-semibold tracking-tight">
              3commerce
            </Link>
            <form action="/search" className="flex-1 max-w-md">
              <input
                type="search"
                name="q"
                placeholder="Search products…"
                aria-label="Search products"
                className="w-full rounded-md border border-neutral-300 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-neutral-400"
              />
            </form>
            <nav className="flex items-center gap-4 text-sm">
              <Link href="/search" className="hover:underline">
                Shop
              </Link>
              <Link href="/cart" className="hover:underline">
                Cart
              </Link>
              <Link href="/account" className="hover:underline">
                Account
              </Link>
            </nav>
          </div>
        </header>
        <main className="flex-1 mx-auto w-full max-w-6xl px-4 py-8">{children}</main>
        <footer className="border-t border-neutral-200 py-6 text-center text-sm text-neutral-500">
          3commerce — demo storefront
        </footer>
        <ConsentBanner />
      </body>
    </html>
  );
}
