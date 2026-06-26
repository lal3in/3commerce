"use client";

import { useActionState, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { submitCheckout, updateCartQuantity, type CheckoutState } from "@/lib/cart-actions";
import type { AddressDto, CartDto, ProfileDto, SavedPaymentMethodDto } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";

interface CheckoutFormProps {
  cart: CartDto;
  profile: ProfileDto | null;
  addresses: AddressDto[];
  paymentMethods: SavedPaymentMethodDto[];
}

export function CheckoutForm({ cart, profile, addresses, paymentMethods }: CheckoutFormProps) {
  const [state, action, pending] = useActionState<CheckoutState, FormData>(submitCheckout, {});
  const [shippingId, setShippingId] = useState(defaultAddress(addresses, "Shipping")?.id ?? "new");
  const [billingId, setBillingId] = useState(defaultAddress(addresses, "Billing")?.id ?? "same");
  const shipping = addresses.find((address) => address.id === shippingId);
  const billing = addresses.find((address) => address.id === billingId);
  const name = profile ? [profile.givenName, profile.familyName].filter(Boolean).join(" ") : "";

  return (
    <form action={action} className="space-y-6">
      {state.error && (
        <p role="alert" className="rounded bg-red-50 text-red-700 px-3 py-2 text-sm">
          {state.error}
        </p>
      )}

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">Review items</h2>
        <ul className="divide-y divide-neutral-100">
          {cart.items.map((item) => (
            <CheckoutLine key={`${item.productId}:${item.variantId ?? "default"}`} item={item} />
          ))}
        </ul>
        <div className="space-y-1 border-t border-neutral-100 pt-3 text-sm">
          <Row label="Subtotal" value={formatMoney(cart.subtotalMinor, cart.currency)} />
          <Row label="Shipping" value="Calculated after authorization" muted />
          <Row label="Tax" value="Calculated after authorization" muted />
        </div>
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">Contact</h2>
        {profile ? (
          <>
            <p className="text-sm text-neutral-600">Signed in as {profile.email}. We use this verified account email for the order.</p>
            <input type="hidden" name="email" value={profile.email} />
          </>
        ) : (
          <Field name="email" label="Email" type="email" autoComplete="email" required title="We'll send your order confirmation here." />
        )}
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">Shipping address</h2>
        {addresses.length > 0 && (
          <AddressSelect
            id="shippingAddressId"
            label="Saved shipping address"
            value={shippingId}
            addresses={addresses.filter((address) => canUse(address, "Shipping"))}
            includeSame={false}
            onChange={setShippingId}
          />
        )}
        <AddressFields key={`shipping-${shippingId}`} prefix="shipping" defaults={shipping} fallbackName={name} />
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">Payment</h2>
        {profile && paymentMethods.length > 0 && (
          <label htmlFor="savedPaymentMethodId" className="block text-sm font-medium">
            Saved card
            <select
              id="savedPaymentMethodId"
              name="savedPaymentMethodId"
              defaultValue={paymentMethods.find((method) => method.isDefault)?.id ?? "new"}
              className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
            >
              <option value="new">Use a new card / wallet</option>
              {paymentMethods.map((method) => (
                <option key={method.id} value={method.id}>
                  {method.brand.toUpperCase()} ending {method.last4} · {method.expMonth}/{method.expYear}
                  {method.isDefault ? " · default" : ""}
                </option>
              ))}
            </select>
          </label>
        )}
        {profile && (
          <label className="flex items-center gap-2 text-sm text-neutral-700">
            <input name="savePaymentMethod" type="checkbox" className="h-4 w-4" />
            Save this new card for one-click checkout later
          </label>
        )}
        {!profile && <p className="text-sm text-neutral-500">Guests pay once. Sign in to save cards for future purchases.</p>}
        <p className="text-xs text-neutral-500">Apple Pay, Google Pay, and card entry are handled by the provider Payment Element; card numbers never touch 3commerce servers.</p>
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">Billing address</h2>
        <AddressSelect
          id="billingAddressId"
          label="Billing address"
          value={billingId}
          addresses={addresses.filter((address) => canUse(address, "Billing"))}
          includeSame
          onChange={setBillingId}
        />
        {billingId !== "same" && <AddressFields key={`billing-${billingId}`} prefix="billing" defaults={billing} fallbackName={name} />}
        {billingId === "same" && <p className="text-sm text-neutral-500">Using the shipping address for card billing/AVS.</p>}
      </section>

      <button
        type="submit"
        disabled={pending || cart.items.length === 0}
        className="w-full rounded-md bg-neutral-900 text-white py-3 text-sm font-medium disabled:opacity-50"
      >
        {pending ? "Authorizing…" : "Authorize & place order"}
      </button>
    </form>
  );
}

function CheckoutLine({ item }: { item: CartDto["items"][number] }) {
  const [updating, start] = useTransition();
  const router = useRouter();
  const changeQuantity = (quantity: number) => {
    start(async () => {
      await updateCartQuantity(item.productId, item.variantId, quantity);
      router.refresh();
    });
  };
  return (
    <li className="py-3 text-sm">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="font-medium">{item.title}</p>
          {item.variantSku && <p className="text-xs text-neutral-500">Variant: {item.variantSku}</p>}
          <p className="text-neutral-500">{formatMoney(item.unitPriceMinor, item.currency)} each</p>
        </div>
        <div className="flex items-center gap-2" aria-label={`Quantity for ${item.title}`}>
          <button
            type="button"
            disabled={updating}
            onClick={() => changeQuantity(item.quantity - 1)}
            className="h-8 w-8 rounded border border-neutral-300 disabled:opacity-50"
            aria-label="Decrease quantity"
          >
            −
          </button>
          <span className="min-w-6 text-center">{item.quantity}</span>
          <button
            type="button"
            disabled={updating}
            onClick={() => changeQuantity(item.quantity + 1)}
            className="h-8 w-8 rounded border border-neutral-300 disabled:opacity-50"
            aria-label="Increase quantity"
          >
            +
          </button>
        </div>
      </div>
    </li>
  );
}

function AddressFields({ prefix, defaults, fallbackName }: { prefix: "shipping" | "billing"; defaults?: AddressDto; fallbackName: string }) {
  return (
    <div className="space-y-3">
      <Field name={`${prefix}Name`} label="Full name" autoComplete="name" defaultValue={defaults?.name ?? fallbackName} required title="Full name for delivery, as it should appear on the parcel." />
      <Field name={`${prefix}Line1`} label="Address" autoComplete="address-line1" defaultValue={defaults?.line1 ?? ""} required title="Street address — number and street name." />
      <div className="grid grid-cols-2 gap-3">
        <Field name={`${prefix}City`} label="City" autoComplete="address-level2" defaultValue={defaults?.city ?? ""} required title="Town or city for delivery." />
        <Field name={`${prefix}Postcode`} label="Postcode" autoComplete="postal-code" defaultValue={defaults?.postcode ?? ""} required title="Postal/ZIP code — used to calculate shipping." />
      </div>
      <Field name={`${prefix}Country`} label="Country (2-letter)" autoComplete="country" maxLength={2} defaultValue={defaults?.country ?? ""} required title="2-letter country code (ISO 3166), e.g. DE, US, AU." />
    </div>
  );
}

function AddressSelect({ id, label, value, addresses, includeSame, onChange }: {
  id: string;
  label: string;
  value: string;
  addresses: AddressDto[];
  includeSame: boolean;
  onChange: (value: string) => void;
}) {
  return (
    <label htmlFor={id} className="block text-sm font-medium">
      {label}
      <select
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
      >
        {includeSame && <option value="same">Same as shipping</option>}
        {addresses.map((address) => (
          <option key={address.id} value={address.id}>
            {address.name} — {address.line1}, {address.city} {address.isDefault ? "(default)" : ""}
          </option>
        ))}
        <option value="new">Enter a new address</option>
      </select>
    </label>
  );
}

function Field({ name, label, type = "text", autoComplete, maxLength, defaultValue, required = false, title }: {
  name: string;
  label: string;
  type?: string;
  autoComplete?: string;
  maxLength?: number;
  defaultValue?: string;
  required?: boolean;
  title?: string;
}) {
  return (
    <div>
      <label htmlFor={name} className="block text-sm font-medium">{label}</label>
      <input
        id={name}
        name={name}
        type={type}
        required={required}
        autoComplete={autoComplete}
        maxLength={maxLength}
        defaultValue={defaultValue}
        title={title}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
      />
    </div>
  );
}

function Row({ label, value, muted = false }: { label: string; value: string; muted?: boolean }) {
  return (
    <div className={muted ? "flex justify-between text-neutral-500" : "flex justify-between"}>
      <span>{label}</span>
      <span>{value}</span>
    </div>
  );
}

function defaultAddress(addresses: AddressDto[], purpose: "Billing" | "Shipping") {
  return addresses.find((address) => address.isDefault && canUse(address, purpose))
    ?? addresses.find((address) => canUse(address, purpose));
}

function canUse(address: AddressDto, purpose: "Billing" | "Shipping") {
  return address.purpose === "Both" || address.purpose === purpose;
}
