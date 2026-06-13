"use client";

import { useTransition } from "react";
import { useRouter } from "next/navigation";
import { addToCart } from "@/lib/cart-actions";

export function AddToCartButton({ productId }: { productId: string }) {
  const [pending, start] = useTransition();
  const router = useRouter();

  return (
    <button
      type="button"
      disabled={pending}
      onClick={() => start(async () => { await addToCart(productId); router.push("/cart"); })}
      className="mt-8 w-full rounded-md bg-neutral-900 text-white py-3 text-sm font-medium disabled:opacity-50"
    >
      {pending ? "Adding…" : "Add to cart"}
    </button>
  );
}
