import Link from "next/link";
import { getCart } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { CartItemRow } from "@/components/cart/CartItemRow";

export const metadata = { title: "Cart" };

// Dynamic (cookie-keyed cart), never cached.
export default async function CartPage() {
  const cart = await getCart();

  if (cart.items.length === 0) {
    return (
      <div className="text-center py-16">
        <h1 className="text-xl font-semibold">Your cart is empty</h1>
        <Link href="/search" className="mt-4 inline-block underline">
          Browse products
        </Link>
      </div>
    );
  }

  return (
    <div className="max-w-2xl mx-auto space-y-6">
      <h1 className="text-xl font-semibold">Your cart</h1>
      <ul className="divide-y divide-neutral-200">
        {cart.items.map((item) => (
          <CartItemRow key={item.productId} item={item} />
        ))}
      </ul>
      <div className="flex justify-between border-t border-neutral-200 pt-4">
        <span className="font-medium">Subtotal</span>
        <span className="font-semibold">{formatMoney(cart.subtotalMinor, cart.currency)}</span>
      </div>
      <p className="text-sm text-neutral-500">Shipping and tax are calculated at checkout.</p>
      <Link
        href="/checkout"
        className="block text-center rounded-md bg-neutral-900 text-white py-3 text-sm font-medium"
      >
        Checkout
      </Link>
    </div>
  );
}
