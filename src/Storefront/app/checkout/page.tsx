import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { CouponStatus, getAddresses, getCart, getCartSummary, getProfile, getSavedPaymentMethods, PromotionBasis } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { resolveShippingBasis } from "@/lib/shipping-basis";
import { formatMoney } from "@/lib/money";
import { CheckoutForm } from "@/components/checkout/CheckoutForm";
import { CouponBox } from "@/components/checkout/CouponBox";

export const metadata = { title: "Checkout" };

export default async function CheckoutPage({
  searchParams,
}: {
  // The coupon the shopper applied (ADR-0052). It lives in the URL so the SERVER can price with it, so a
  // refresh keeps it, and so "remove" is just a link back to /checkout with no query.
  searchParams: Promise<{ coupon?: string }>;
}) {
  const t = await getTranslations("checkout");
  const enteredCoupon = (await searchParams).coupon?.trim().toUpperCase() || null;
  const cart = await getCart();
  const profile = await getProfile();
  const [addresses, paymentMethods, storefront] = profile
    ? await Promise.all([getAddresses(), getSavedPaymentMethods(), resolveStorefront()])
    : [[], [], await resolveStorefront()];
  // Tax context is THIS storefront's, or nothing (rev_tax). There used to be a by-currency fallback
  // here, mirroring the unscoped lookup Ordering did: it picked whichever live store happened to share
  // the cart's currency and showed its rate and its inclusiveness. That is a rate for an order that
  // cannot even be placed — the checkout POST carries `storefront?.id`, and Ordering refuses a checkout
  // with no resolved store — so the only honest estimate with no storefront context is no tax at all.
  const taxSource = storefront;
  if (cart.items.length === 0) {
    redirect("/cart");
  }

  const taxRateBasisPoints = taxSource?.taxRateBasisPoints ?? 0;
  // ADR-0038: AU GST / EU VAT shelf prices already include tax; US adds it at checkout.
  const taxInclusive = taxSource?.taxRegime === "AuGst" || taxSource?.taxRegime === "EuVat";
  // Ship-to allowlist (empty = worldwide) restricts the checkout country picker to served destinations.
  const shipToCountries = taxSource?.shipToCountries ?? [];
  // Storefront-wide discount (bps; 0 = none) — deducted from the items' subtotal only, shown as its own line.
  const discountBps = taxSource?.discountBps ?? 0;
  // Threshold promotions (ADR-0051): decided by Ordering with the SAME evaluator checkout runs, so the
  // estimate matches the charge. Null (unavailable) → no promotion rows and today's local math.
  // The entered coupon rides the same call: Ordering validates it with the SAME rules checkout applies
  // and prices the cart with it, so what this page shows is what the shopper is charged (ADR-0052).
  // Scored against the REAL carrier rate whenever a destination is known (saved default address, or the
  // one already entered here — a guest counts): a free-shipping promotion is worth exactly the shipping
  // amount, so the winner must be decided on the amount checkout will charge (ADR-0051). With no address
  // yet the verdict comes back provisional, and picking a rate below re-prices it client-side.
  const shippingBasis = await resolveShippingBasis(cart, storefront?.id ?? null);
  const summary = await getCartSummary(storefront?.id, enteredCoupon ?? undefined, shippingBasis);
  // Same basis rule as the cart: the summary's subtotal is the OFFER-RESOLVED item value actually
  // charged, so the estimate lines add up even when an offer overrides a line's catalog price.
  const subtotalMinor = summary?.subtotalMinor ?? cart.subtotalMinor;
  const promotionDiscountMinor = summary?.promotionDiscountMinor ?? 0;
  const appliedPromotions = summary?.appliedPromotions ?? [];
  const freeShippingApplied = summary?.freeShippingApplied ?? false;
  const promotionBasis = summary?.basis ?? PromotionBasis.Settled;
  // No verdict when the summary itself is unavailable: the whole page has already fallen back to local
  // math, and claiming "we don't recognise that code" would assert something we did not check. The box
  // then simply shows the code still un-applied, which is the truth.
  const couponStatus = summary?.couponStatus ?? CouponStatus.None;
  // ONLY a validated coupon is submitted with the checkout. A refused one is shown as its reason and left
  // out of the POST, so the charge always equals the total rendered above it.
  const appliedCouponCode = couponStatus === CouponStatus.Applied ? (summary?.couponCode ?? enteredCoupon) : null;

  return (
    <div className="max-w-xl mx-auto space-y-6">
      <h1 className="text-xl font-semibold">{t("title")}</h1>
      <div className="rounded-md border border-neutral-200 p-4 text-sm">
        <div className="flex justify-between">
          <span>{t("subtotalItems", { count: cart.items.length })}</span>
          <span>{formatMoney(subtotalMinor, cart.currency)}</span>
        </div>
        <p className="mt-1 text-neutral-500">{t("taxNote")}</p>
      </div>
      <CouponBox code={enteredCoupon} status={couponStatus} promotionName={summary?.couponPromotionName ?? ""} />
      <CheckoutForm cart={cart} profile={profile} addresses={addresses} paymentMethods={paymentMethods} taxRateBasisPoints={taxRateBasisPoints} taxInclusive={taxInclusive} shipToCountries={shipToCountries} discountBps={discountBps} subtotalMinor={subtotalMinor} promotionDiscountMinor={promotionDiscountMinor} appliedPromotions={appliedPromotions} freeShippingApplied={freeShippingApplied} promotionBasis={promotionBasis} appliedCouponCode={appliedCouponCode} />
    </div>
  );
}
