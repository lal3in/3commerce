import { redirect } from "next/navigation";
import { getCart } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { CheckoutForm } from "@/components/checkout/CheckoutForm";

export const metadata = { title: "Checkout" };

export default async function CheckoutPage() {
  const cart = await getCart();
  if (cart.items.length === 0) {
    redirect("/cart");
  }

  return (
    <div className="max-w-xl mx-auto space-y-6">
      <h1 className="text-xl font-semibold">Checkout</h1>
      <div className="rounded-md border border-neutral-200 p-4 text-sm">
        <div className="flex justify-between">
          <span>Subtotal ({cart.items.length} item{cart.items.length === 1 ? "" : "s"})</span>
          <span>{formatMoney(cart.subtotalMinor, cart.currency)}</span>
        </div>
        <p className="mt-1 text-neutral-500">+ shipping and tax, shown after you place the order.</p>
      </div>
      <CheckoutForm />
    </div>
  );
}
