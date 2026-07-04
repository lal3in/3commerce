import type { Metadata } from "next";
import { GATEWAY_URL } from "@/lib/gateway";

// Signed draft preview (def_5 / mt5_7): read-only, and ALWAYS noindex — preview URLs are
// short-lived operator links, never search-indexable pages.
export const metadata: Metadata = { title: "Content preview", robots: { index: false, follow: false } };

export const dynamic = "force-dynamic";

export default async function ContentPreviewPage({
  params,
  searchParams,
}: {
  params: Promise<{ contentId: string; version: string }>;
  searchParams: Promise<{ token?: string }>;
}) {
  const { contentId, version } = await params;
  const { token } = await searchParams;

  let payload: string | null = null;
  if (token) {
    const response = await fetch(
      `${GATEWAY_URL}/api/marketing/content/preview/${contentId}/${version}?token=${encodeURIComponent(token)}`,
      { cache: "no-store" },
    );
    if (response.ok) {
      payload = ((await response.json()) as { payload: string }).payload;
    }
  }

  return (
    <main className="mx-auto max-w-3xl px-4 py-10">
      <p className="mb-4 rounded bg-amber-50 px-3 py-2 text-sm text-amber-800">
        Draft preview — version {version}. This link expires and is not indexed.
      </p>
      {payload === null ? (
        <p className="text-sm text-red-700">
          This preview link is invalid or has expired. Mint a new one from the content editor.
        </p>
      ) : (
        <pre className="whitespace-pre-wrap rounded-md border border-neutral-200 bg-white p-4 text-sm">{payload}</pre>
      )}
    </main>
  );
}
