import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { getCart, getCartSummary } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { formatMoney } from "@/lib/money";
import { CartItemRow } from "@/components/cart/CartItemRow";

export const metadata = { title: "Cart" };

// 1000 bp → "10", 825 bp → "8.25" (the catalog string supplies the % sign and word order).
function formatRate(basisPoints: number): string {
  return (basisPoints / 100).toFixed(basisPoints % 100 === 0 ? 0 : 2);
}

// Dynamic (cookie-keyed cart), never cached.
export default async function CartPage() {
  const t = await getTranslations("cart");
  const cart = await getCart();
  // Storefront-wide discount (bps; 0 = none) — deducted from the items' subtotal only, shown as its own
  // line so what the cart shows matches what checkout charges. Null storefront (no context) → no discount.
  const storefront = await resolveStorefront();
  const discountBps = storefront?.discountBps ?? 0;
  // Threshold promotions (ADR-0051) are decided by Ordering, never here: /cart/summary runs the same
  // evaluator checkout runs, so shown == charged. When it is unavailable (null) the page falls back to
  // today's local storefront-discount math and simply shows no promotion rows.
  const summary = await getCartSummary(storefront?.id);
  const storefrontDiscountMinor = summary
    ? summary.storefrontDiscountMinor
    : discountBps > 0
      ? Math.min(Math.round(cart.subtotalMinor * discountBps / 10000), cart.subtotalMinor)
      : 0;
  const promotions = summary?.appliedPromotions ?? [];
  const freeShippingApplied = summary?.freeShippingApplied ?? false;
  const discountedSubtotalMinor = summary
    ? summary.itemsTotalMinor
    : cart.subtotalMinor - storefrontDiscountMinor;
  const anyDeduction = storefrontDiscountMinor > 0 || promotions.length > 0;

  if (cart.items.length === 0) {
    return (
      <div className="text-center py-16">
        <h1 className="text-xl font-semibold">{t("emptyTitle")}</h1>
        <Link href="/search" title={t("tips.browseProducts")} className="mt-4 inline-block underline">
          {t("browseProducts")}
        </Link>
      </div>
    );
  }

  return (
    <div className="max-w-2xl mx-auto space-y-6">
      <h1 className="text-xl font-semibold">{t("title")}</h1>
      <ul className="divide-y divide-neutral-200">
        {cart.items.map((item) => (
          <CartItemRow key={`${item.productId}:${item.variantId ?? "default"}`} item={item} />
        ))}
      </ul>
      <div className="space-y-2 border-t border-neutral-200 pt-4">
        <div className="flex justify-between">
          <span className="font-medium">{t("subtotal")}</span>
          <span className="font-semibold">{formatMoney(cart.subtotalMinor, cart.currency)}</span>
        </div>
        {storefrontDiscountMinor > 0 && (
          <div className="flex justify-between text-emerald-700">
            <span>{t("discount", { percent: formatRate(discountBps) })}</span>
            <span>−{formatMoney(storefrontDiscountMinor, cart.currency)}</span>
          </div>
        )}
        {promotions.map((promotion) => (
          <div key={promotion.promotionId} className="flex justify-between text-emerald-700">
            <span>{t("promotion", { name: promotion.name })}</span>
            <span>−{formatMoney(promotion.discountMinor, cart.currency)}</span>
          </div>
        ))}
        {freeShippingApplied && (
          <div className="flex justify-between text-emerald-700">
            <span>{t("freeShipping")}</span>
            <span>−</span>
          </div>
        )}
        {anyDeduction && (
          <div className="flex justify-between border-t border-neutral-100 pt-2">
            <span className="font-medium">{t("itemsTotal")}</span>
            <span className="font-semibold">{formatMoney(discountedSubtotalMinor, cart.currency)}</span>
          </div>
        )}
      </div>
      <p className="text-sm text-neutral-500">{t("taxNote")}</p>
      <Link
        href="/checkout"
        title={t("tips.checkout")}
        className="block text-center rounded-md bg-neutral-900 text-white py-3 text-sm font-medium"
      >
        {t("checkout")}
      </Link>
    </div>
  );
}
