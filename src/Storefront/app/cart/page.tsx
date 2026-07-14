import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { getCart } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { CartItemRow } from "@/components/cart/CartItemRow";

export const metadata = { title: "Cart" };

// Dynamic (cookie-keyed cart), never cached.
export default async function CartPage() {
  const t = await getTranslations("cart");
  const cart = await getCart();

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
      <div className="flex justify-between border-t border-neutral-200 pt-4">
        <span className="font-medium">{t("subtotal")}</span>
        <span className="font-semibold">{formatMoney(cart.subtotalMinor, cart.currency)}</span>
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
