import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { CouponStatus } from "@/lib/gateway";

interface CouponBoxProps {
  // What the shopper currently has entered (from the ?coupon= query), or null when the box is empty.
  code: string | null;
  // Ordering's verdict on that code — the SAME validation checkout runs, so the box can never claim a
  // code applies when the charge would disagree (ADR-0052 "shown == charged").
  status: CouponStatus;
  // The promotion the code unlocked, shown on the success line so the shopper sees WHAT they got.
  promotionName: string;
}

/**
 * Apply / remove a coupon code at checkout (ADR-0052).
 *
 * Deliberately a plain GET form to `/checkout?coupon=…` rather than client state: the code has to reach
 * the SERVER for Ordering to validate and price it, the resulting URL is shareable and refresh-safe, and
 * "remove" is just a link back to `/checkout`. The storefront never decides whether a coupon is valid —
 * it renders the status Ordering returned, and only a status of Applied is submitted with the checkout.
 */
export async function CouponBox({ code, status, promotionName }: CouponBoxProps) {
  const t = await getTranslations("checkout.coupon");
  const applied = status === CouponStatus.Applied;
  // One message per refusal, so the shopper reads a reason they can act on rather than a blanket
  // "invalid coupon". Written as literal keys (not a computed template) so next-intl type-checks them.
  const error = ((): string | null => {
    const values = { code: code ?? "" };
    switch (status) {
      case CouponStatus.UnknownCode:
        return t("errors.unknownCode", values);
      case CouponStatus.Inactive:
        return t("errors.inactive", values);
      case CouponStatus.NotStarted:
        return t("errors.notStarted", values);
      case CouponStatus.Expired:
        return t("errors.expired", values);
      case CouponStatus.WrongStorefront:
        return t("errors.wrongStorefront", values);
      case CouponStatus.ThresholdNotMet:
        return t("errors.thresholdNotMet", values);
      case CouponStatus.UsageLimitReached:
        return t("errors.usageLimitReached", values);
      case CouponStatus.CustomerLimitReached:
        return t("errors.customerLimitReached", values);
      default:
        return null;
    }
  })();

  return (
    <section className="rounded-md border border-neutral-200 p-4 text-sm space-y-2" data-testid="coupon-box">
      <h2 className="font-medium">{t("title")}</h2>
      {applied ? (
        <div className="flex items-center justify-between gap-3">
          <p className="text-green-700" data-testid="coupon-applied">
            {t("applied", { code: code ?? "", name: promotionName })}
          </p>
          {/* Removing is a navigation, not a mutation: drop the query and the page re-prices without it. */}
          <Link href="/checkout" className="underline text-neutral-600" data-testid="coupon-remove">
            {t("remove")}
          </Link>
        </div>
      ) : (
        <form method="get" action="/checkout" className="flex gap-2">
          <label className="sr-only" htmlFor="coupon">
            {t("label")}
          </label>
          <input
            id="coupon"
            name="coupon"
            type="text"
            defaultValue={code ?? ""}
            placeholder={t("placeholder")}
            autoComplete="off"
            maxLength={40}
            className="flex-1 rounded border border-neutral-300 px-3 py-2"
            data-testid="coupon-input"
          />
          <button type="submit" className="rounded bg-neutral-900 text-white px-4 py-2" data-testid="coupon-apply">
            {t("apply")}
          </button>
        </form>
      )}
      {error && (
        <p role="alert" className="text-red-700" data-testid="coupon-error">
          {error}
        </p>
      )}
    </section>
  );
}
