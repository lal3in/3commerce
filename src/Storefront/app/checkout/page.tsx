import { redirect } from "next/navigation";
import { getAddresses, getCart, getProfile, getSavedPaymentMethods, getStorefrontConfig } from "@/lib/gateway";
import { resolveStorefront } from "@/lib/storefront-context";
import { formatMoney } from "@/lib/money";
import { CheckoutForm } from "@/components/checkout/CheckoutForm";

export const metadata = { title: "Checkout" };

export default async function CheckoutPage() {
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

  return (
    <div className="max-w-xl mx-auto space-y-6">
      <h1 className="text-xl font-semibold">Checkout</h1>
      <div className="rounded-md border border-neutral-200 p-4 text-sm">
        <div className="flex justify-between">
          <span>Subtotal ({cart.items.length} item{cart.items.length === 1 ? "" : "s"})</span>
          <span>{formatMoney(cart.subtotalMinor, cart.currency)}</span>
        </div>
        <p className="mt-1 text-neutral-500">Choose a shipping rate before authorization; tax follows this storefront&apos;s regime — included in AU/EU prices, added at checkout for US.</p>
      </div>
      <CheckoutForm cart={cart} profile={profile} addresses={addresses} paymentMethods={paymentMethods} taxRateBasisPoints={taxRateBasisPoints} taxInclusive={taxInclusive} />
    </div>
  );
}
