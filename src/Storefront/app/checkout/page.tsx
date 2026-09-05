import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { getAddresses, getCart, getCartSummary, getProfile, getSavedPaymentMethods, getStorefrontConfig } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { formatMoney } from "@/lib/money";
import { CheckoutForm } from "@/components/checkout/CheckoutForm";
import { CouponBox } from "@/components/checkout/CouponBox";
import { CouponStatus } from "@/lib/gateway";

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
  // Tax context: the resolved storefront (cookie/host) wins; fall back to a by-currency lookup so a
  // context-less session still shows the right rate for whatever currency its cart is in.
  const [addresses, paymentMethods, storefront] = profile
    ? await Promise.all([getAddresses(), getSavedPaymentMethods(), resolveStorefront()])
    : [[], [], await resolveStorefront()];
  const taxSource = storefront ?? (await getStorefrontConfig({ currency: cart.currency }));
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
  const summary = await getCartSummary(storefront?.id, enteredCoupon ?? undefined);
  // Same basis rule as the cart: the summary's subtotal is the OFFER-RESOLVED item value actually
  // charged, so the estimate lines add up even when an offer overrides a line's catalog price.
  const subtotalMinor = summary?.subtotalMinor ?? cart.subtotalMinor;
  const promotionDiscountMinor = summary?.promotionDiscountMinor ?? 0;
  const appliedPromotions = summary?.appliedPromotions ?? [];
  const freeShippingApplied = summary?.freeShippingApplied ?? false;
  const couponStatus = summary?.couponStatus ?? (enteredCoupon ? CouponStatus.UnknownCode : CouponStatus.None);
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
      <CheckoutForm cart={cart} profile={profile} addresses={addresses} paymentMethods={paymentMethods} taxRateBasisPoints={taxRateBasisPoints} taxInclusive={taxInclusive} shipToCountries={shipToCountries} discountBps={discountBps} subtotalMinor={subtotalMinor} promotionDiscountMinor={promotionDiscountMinor} appliedPromotions={appliedPromotions} freeShippingApplied={freeShippingApplied} appliedCouponCode={appliedCouponCode} />
    </div>
  );
}
