"use client";

import { useActionState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { submitReview, type ReviewState } from "@/lib/reviews-actions";
import type { ReviewSummary } from "@/lib/gateway";

function Stars({ value }: { value: number }) {
  const full = Math.round(value);
  return (
    <span aria-hidden className="text-amber-500">
      {"★".repeat(full)}
      <span className="text-neutral-300">{"★".repeat(5 - full)}</span>
    </span>
  );
}

// Product ratings & reviews. The list + aggregate are shown to everyone; the write form appears only
// for a signed-in, email-verified customer (the server enforces the same rule). `reason` drives the
// prompt shown to everyone else.
export function ProductReviews({
  productId,
  slug,
  summary,
  canReview,
  reason,
  defaultName,
}: {
  productId: string;
  slug: string;
  summary: ReviewSummary;
  canReview: boolean;
  reason: "anon" | "unverified" | null;
  defaultName: string;
}) {
  const t = useTranslations("reviews");
  const [state, action, pending] = useActionState<ReviewState, FormData>(submitReview, {});

  return (
    <section className="col-span-full mt-10 border-t border-neutral-200 pt-6">
      <h2 className="text-lg font-semibold">{t("title")}</h2>
      <div className="mt-1 flex items-center gap-2 text-sm">
        {summary.count > 0 ? (
          <>
            <Stars value={summary.average} />
            <span className="font-medium">{summary.average.toFixed(1)}</span>
            <span className="text-neutral-500">{t("count", { count: summary.count })}</span>
          </>
        ) : (
          <span className="text-neutral-500">{t("none")}</span>
        )}
      </div>

      {canReview ? (
        <form action={action} className="mt-4 space-y-2 rounded-md border border-neutral-200 p-4">
          <h3 className="text-sm font-medium">{t("writeTitle")}</h3>
          {state.ok && <p className="text-sm text-green-700">{t("thanks")}</p>}
          {state.error && <p className="text-sm text-red-600">{state.error}</p>}
          <input type="hidden" name="productId" value={productId} />
          <input type="hidden" name="slug" value={slug} />
          <input type="hidden" name="authorName" value={defaultName} />
          <label className="block text-sm">
            {t("yourRating")}
            <select name="rating" defaultValue="5" className="mt-1 block rounded border border-neutral-300 px-2 py-1 text-sm">
              {[5, 4, 3, 2, 1].map((n) => (
                <option key={n} value={n}>
                  {"★".repeat(n)} ({n})
                </option>
              ))}
            </select>
          </label>
          <textarea
            name="comment"
            rows={3}
            placeholder={t("commentPlaceholder")}
            aria-label={t("commentPlaceholder")}
            className="w-full rounded border border-neutral-300 px-3 py-2 text-sm"
          />
          <button
            type="submit"
            disabled={pending}
            className="rounded-md bg-neutral-900 px-4 py-2 text-sm text-white disabled:opacity-50"
          >
            {pending ? t("submitting") : t("submit")}
          </button>
        </form>
      ) : reason ? (
        <p className="mt-4 rounded-md border border-neutral-200 bg-neutral-50 px-3 py-2 text-sm text-neutral-600">
          {reason === "anon" ? (
            <>
              <Link href="/login" className="underline">
                {t("signIn")}
              </Link>{" "}
              {t("notice.anon")}
            </>
          ) : (
            t("notice.unverified")
          )}
        </p>
      ) : null}

      <ul className="mt-6 space-y-4">
        {summary.items.map((r) => (
          <li key={r.id} className="border-b border-neutral-100 pb-3">
            <div className="flex items-center gap-2 text-sm">
              <Stars value={r.rating} />
              <span className="font-medium">{r.authorName}</span>
              <span className="text-neutral-400">· {new Date(r.createdAt).toLocaleDateString()}</span>
            </div>
            {r.comment && <p className="mt-1 whitespace-pre-wrap text-sm text-neutral-700">{r.comment}</p>}
          </li>
        ))}
      </ul>
    </section>
  );
}
