"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
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
}

export function AddToCartButton({ productId, variants }: AddToCartButtonProps) {
  const [selectedVariantId, setSelectedVariantId] = useState(variants[0]?.id ?? "");
  const [quantity, setQuantity] = useState(1);
  const [pending, start] = useTransition();
  const router = useRouter();
  const selected = variants.find((variant) => variant.id === selectedVariantId) ?? variants[0];

  return (
    <div className="mt-8 space-y-3">
      {variants.length > 1 && (
        <label className="block text-sm font-medium">
          Variant
          <select
            value={selectedVariantId}
            onChange={(event) => setSelectedVariantId(event.target.value)}
            className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
          >
            {variants.map((variant) => (
              <option key={variant.id} value={variant.id} disabled={!variant.inStock}>
                {variant.sku} — {formatMoney(variant.priceMinor, variant.currency)} {variant.inStock ? "" : "(out of stock)"}
              </option>
            ))}
          </select>
        </label>
      )}
      <div className="flex items-center gap-3">
        <span className="text-sm font-medium">Quantity</span>
        <div className="inline-flex items-center rounded-md border border-neutral-300">
          <button
            type="button"
            aria-label="Decrease quantity"
            disabled={quantity <= 1}
            onClick={() => setQuantity((q) => Math.max(1, q - 1))}
            className="px-3 py-1.5 text-sm disabled:opacity-40"
          >
            −
          </button>
          <span className="w-10 text-center text-sm tabular-nums">{quantity}</span>
          <button
            type="button"
            aria-label="Increase quantity"
            disabled={quantity >= 99}
            onClick={() => setQuantity((q) => Math.min(99, q + 1))}
            className="px-3 py-1.5 text-sm disabled:opacity-40"
          >
            +
          </button>
        </div>
      </div>
      <button
        type="button"
        disabled={pending || !selected?.inStock}
        onClick={() =>
          start(async () => {
            await addToCart(productId, selected?.id, quantity);
            router.push("/cart");
          })
        }
        className="w-full rounded-md bg-neutral-900 text-white py-3 text-sm font-medium disabled:opacity-50"
      >
        {pending ? "Adding…" : selected?.inStock === false ? "Out of stock" : "Add to cart"}
      </button>
    </div>
  );
}
