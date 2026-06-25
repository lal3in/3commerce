import type { Metadata } from "next";
import Link from "next/link";
import "./globals.css";
import ConsentBanner from "@/components/consent/ConsentBanner";

export const metadata: Metadata = {
  title: { default: "3commerce", template: "%s · 3commerce" },
  description: "A from-scratch e-commerce storefront.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body className="min-h-screen flex flex-col">
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
