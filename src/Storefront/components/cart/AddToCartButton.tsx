"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { addToCart } from "@/lib/cart-actions";
import { formatMoney } from "@/lib/money";

interface VariantOption {
  id: string;
  sku: string;
  priceMinor: number;
  currency: string;
  inStock: boolean;
}

interface AddToCartButtonProps {
  productId: string;
  variants: VariantOption[];
  // Active storefront currency — the cart line is priced in it (tenant per-currency price).
  currency?: string;
}

export function AddToCartButton({ productId, variants, currency }: AddToCartButtonProps) {
  const t = useTranslations("product");
  const [selectedVariantId, setSelectedVariantId] = useState(variants[0]?.id ?? "");
  const [quantity, setQuantity] = useState(1);
  const [pending, start] = useTransition();
  const router = useRouter();
  const selected = variants.find((variant) => variant.id === selectedVariantId) ?? variants[0];

  return (
    <div className="mt-8 space-y-3">
      {variants.length > 1 && (
        <label htmlFor="variant" className="block text-sm font-medium" title={t("tips.variant")}>
          {t("variant")}
          <select
            id="variant"
            value={selectedVariantId}
            onChange={(event) => setSelectedVariantId(event.target.value)}
            title={t("tips.variant")}
            aria-describedby="variant-tip"
            className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
          >
            {variants.map((variant) => (
              <option key={variant.id} value={variant.id} disabled={!variant.inStock}>
                {variant.inStock
                  ? t("variantOption", { sku: variant.sku, price: formatMoney(variant.priceMinor, variant.currency) })
                  : t("variantOptionOutOfStock", { sku: variant.sku, price: formatMoney(variant.priceMinor, variant.currency) })}
              </option>
            ))}
          </select>
          <span id="variant-tip" className="sr-only">
            {t("tips.variant")}
          </span>
        </label>
      )}
      <div className="flex items-center gap-3">
        <span className="text-sm font-medium" title={t("tips.quantity")}>
          {t("quantity")}
        </span>
        <div className="inline-flex items-center rounded-md border border-neutral-300" aria-describedby="quantity-tip">
          <button
            type="button"
            aria-label={t("decreaseQuantity")}
            title={t("tips.decreaseQuantity")}
            disabled={quantity <= 1}
            onClick={() => setQuantity((q) => Math.max(1, q - 1))}
            className="px-3 py-1.5 text-sm disabled:opacity-40"
          >
            −
          </button>
          <span className="w-10 text-center text-sm tabular-nums">{quantity}</span>
          <button
            type="button"
            aria-label={t("increaseQuantity")}
            title={t("tips.increaseQuantity")}
            disabled={quantity >= 99}
            onClick={() => setQuantity((q) => Math.min(99, q + 1))}
            className="px-3 py-1.5 text-sm disabled:opacity-40"
          >
            +
          </button>
        </div>
        <span id="quantity-tip" className="sr-only">
          {t("tips.quantity")}
        </span>
      </div>
      <button
        type="button"
        disabled={pending || !selected?.inStock}
        title={selected?.inStock === false ? t("tips.outOfStock") : t("tips.addToCart")}
        onClick={() =>
          start(async () => {
            await addToCart(productId, selected?.id, quantity, currency);
            router.push("/cart");
          })
        }
        className="w-full rounded-md bg-neutral-900 text-white py-3 text-sm font-medium disabled:opacity-50"
      >
        {pending ? t("adding") : selected?.inStock === false ? t("outOfStock") : t("addToCart")}
      </button>
    </div>
  );
}
