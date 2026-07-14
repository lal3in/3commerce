"use client";

import { useTransition } from "react";
import { useTranslations } from "next-intl";
import type { CartItemDto } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { removeFromCart, updateCartQuantity } from "@/lib/cart-actions";
import { SafeImage } from "@/components/SafeImage";

export function CartItemRow({ item }: { item: CartItemDto }) {
  const t = useTranslations("cart");
  const [pending, start] = useTransition();
  const variantId = item.variantId ?? null;

  // Minus at 1 removes the line (the backend treats quantity 0 as removal).
  const decrement = () =>
    start(() =>
      item.quantity <= 1
        ? removeFromCart(item.productId, variantId)
        : updateCartQuantity(item.productId, variantId, item.quantity - 1),
    );
  const increment = () => start(() => updateCartQuantity(item.productId, variantId, item.quantity + 1));

  return (
    <li className="flex items-center gap-4 py-4">
      <div className="w-16 h-16 bg-neutral-100 relative rounded overflow-hidden shrink-0">
        {item.imageUrl && <SafeImage src={item.imageUrl} alt={item.title} fill sizes="64px" className="object-cover" />}
      </div>
      <div className="flex-1">
        <p className="text-sm font-medium">{item.title}</p>
        {item.variantSku && <p className="text-xs text-neutral-500">{t("variant", { sku: item.variantSku })}</p>}
        <p className="text-sm text-neutral-500">
          {t("each", { price: formatMoney(item.unitPriceMinor, item.currency) })}
        </p>
      </div>
      <div className="inline-flex items-center rounded-md border border-neutral-300">
        <button
          type="button"
          aria-label={t("decreaseQuantity")}
          title={t("tips.decreaseQuantity")}
          disabled={pending}
          onClick={decrement}
          className="px-3 py-1.5 text-sm disabled:opacity-40"
        >
          −
        </button>
        <span className="w-10 text-center text-sm tabular-nums">{item.quantity}</span>
        <button
          type="button"
          aria-label={t("increaseQuantity")}
          title={t("tips.increaseQuantity")}
          disabled={pending || item.quantity >= 99}
          onClick={increment}
          className="px-3 py-1.5 text-sm disabled:opacity-40"
        >
          +
        </button>
      </div>
      <button
        type="button"
        disabled={pending}
        title={t("tips.remove")}
        onClick={() => start(() => removeFromCart(item.productId, variantId))}
        className="text-sm text-neutral-500 hover:text-red-600 disabled:opacity-50"
      >
        {t("remove")}
      </button>
    </li>
  );
}
