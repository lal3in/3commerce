"use client";

import { useActionState, useState } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { submitReview, submitReply, type ReviewState } from "@/lib/reviews-actions";
import type { ProductReview, ReviewSummary } from "@/lib/gateway";
import { LocalTime } from "@/components/LocalTime";

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

      {/* A signed-in verified member can also add a plain comment (no rating) to the product. */}
      {canReview && (
        <ReplyForm productId={productId} slug={slug} defaultName={defaultName} label={t("addComment")} placeholder={t("commentPlaceholder")} submitLabel={t("postComment")} />
      )}

      <ul className="mt-6 space-y-4">
        {summary.items.map((r) => (
          <ReviewItem key={r.id} review={r} productId={productId} slug={slug} canReply={canReview} defaultName={defaultName} />
        ))}
      </ul>
    </section>
  );
}

// One top-level review/comment plus its replies and (for a verified member) a collapsible reply box.
function ReviewItem({
  review,
  productId,
  slug,
  canReply,
  defaultName,
}: {
  review: ProductReview;
  productId: string;
  slug: string;
  canReply: boolean;
  defaultName: string;
}) {
  const t = useTranslations("reviews");
  const [open, setOpen] = useState(false);
  return (
    <li className="border-b border-neutral-100 pb-3">
      <div className="flex items-center gap-2 text-sm">
        {review.rating != null && <Stars value={review.rating} />}
        <span className="font-medium">{review.authorName}</span>
        <span className="text-neutral-400">· <LocalTime iso={review.createdAt} dateOnly /></span>
      </div>
      {review.comment && <p className="mt-1 whitespace-pre-wrap text-sm text-neutral-700">{review.comment}</p>}

      {review.replies.length > 0 && (
        <ul className="mt-2 space-y-2 border-l-2 border-neutral-100 pl-4">
          {review.replies.map((rep) => (
            <li key={rep.id} className="text-sm">
              <span className="font-medium">{rep.authorName}</span>
              <span className="text-neutral-400"> · <LocalTime iso={rep.createdAt} dateOnly /></span>
              <p className="mt-0.5 whitespace-pre-wrap text-neutral-700">{rep.message}</p>
            </li>
          ))}
        </ul>
      )}

      {canReply && (
        <div className="mt-2 pl-4">
          {open ? (
            <ReplyForm productId={productId} slug={slug} parentId={review.id} defaultName={defaultName} label={t("replyTitle")} placeholder={t("replyPlaceholder")} submitLabel={t("postReply")} onDone={() => setOpen(false)} />
          ) : (
            <button type="button" onClick={() => setOpen(true)} className="text-xs font-medium text-neutral-600 underline">
              {t("reply")}
            </button>
          )}
        </div>
      )}
    </li>
  );
}

// Shared form for a reply (parentId set) or a top-level comment (parentId omitted). No rating.
function ReplyForm({
  productId,
  slug,
  parentId,
  defaultName,
  label,
  placeholder,
  submitLabel,
  onDone,
}: {
  productId: string;
  slug: string;
  parentId?: string;
  defaultName: string;
  label: string;
  placeholder: string;
  submitLabel: string;
  onDone?: () => void;
}) {
  const t = useTranslations("reviews");
  const [state, action, pending] = useActionState<ReviewState, FormData>(submitReply, {});
  if (state.ok && onDone) onDone();
  return (
    <form action={action} className="mt-1 space-y-2 rounded-md border border-neutral-200 p-3">
      <h3 className="text-sm font-medium">{label}</h3>
      {state.ok && <p className="text-sm text-green-700">{t("posted")}</p>}
      {state.error && <p className="text-sm text-red-600">{state.error}</p>}
      <input type="hidden" name="productId" value={productId} />
      <input type="hidden" name="slug" value={slug} />
      <input type="hidden" name="authorName" value={defaultName} />
      {parentId && <input type="hidden" name="parentId" value={parentId} />}
      <textarea
        name="comment"
        rows={2}
        placeholder={placeholder}
        aria-label={placeholder}
        className="w-full rounded border border-neutral-300 px-3 py-2 text-sm"
      />
      <button type="submit" disabled={pending} className="rounded-md bg-neutral-900 px-4 py-2 text-sm text-white disabled:opacity-50">
        {pending ? t("submitting") : submitLabel}
      </button>
    </form>
  );
}
