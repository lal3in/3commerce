"use client";

import Image from "next/image";
import { useTransition } from "react";
import type { CartItemDto } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { removeFromCart } from "@/lib/cart-actions";

export function CartItemRow({ item }: { item: CartItemDto }) {
  const [pending, start] = useTransition();

  return (
    <li className="flex items-center gap-4 py-4">
      <div className="w-16 h-16 bg-neutral-100 relative rounded overflow-hidden shrink-0">
        {item.imageUrl && <Image src={item.imageUrl} alt={item.title} fill sizes="64px" className="object-cover" />}
      </div>
      <div className="flex-1">
        <p className="text-sm font-medium">{item.title}</p>
        {item.variantSku && <p className="text-xs text-neutral-500">Variant: {item.variantSku}</p>}
        <p className="text-sm text-neutral-500">
          {item.quantity} × {formatMoney(item.unitPriceMinor, item.currency)}
        </p>
      </div>
      <button
        type="button"
        disabled={pending}
        onClick={() => start(() => removeFromCart(item.productId, item.variantId))}
        className="text-sm text-neutral-500 hover:text-red-600 disabled:opacity-50"
      >
        Remove
      </button>
    </li>
  );
}
