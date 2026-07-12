"use client";

import { useActionState, useEffect, useRef, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { quoteCheckoutShipping, submitCheckout, updateCartQuantity, type CheckoutState, type ShippingRate } from "@/lib/cart-actions";
import type { AddressDto, CartDto, ProfileDto, SavedPaymentMethodDto } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";

interface CheckoutFormProps {
  cart: CartDto;
  profile: ProfileDto | null;
  addresses: AddressDto[];
  paymentMethods: SavedPaymentMethodDto[];
  // This storefront's configured tax rate in basis points (1000 = 10%); drives the tax line.
  taxRateBasisPoints: number;
  // ADR-0038: inclusive regimes (AU GST / EU VAT) — prices already contain the tax; it is shown
  // informationally and NOT added to the total. Exclusive regimes add it.
  taxInclusive: boolean;
}

const PAYMENT_OPTIONS = [
  { value: "CreditCard", label: "Credit card", icon: <CardIcon /> },
  { value: "Stripe", label: "Stripe Payment Element", icon: <StripeIcon /> },
  { value: "ApplePay", label: "Apple Pay", icon: <ApplePayIcon /> },
  { value: "GooglePay", label: "Google Pay", icon: <GooglePayIcon /> },
  { value: "PayPal", label: "PayPal", icon: <PayPalIcon /> },
];

export function CheckoutForm({ cart, profile, addresses, paymentMethods, taxRateBasisPoints, taxInclusive }: CheckoutFormProps) {
  const [state, action, pending] = useActionState<CheckoutState, FormData>(submitCheckout, {});
  const [shippingId, setShippingId] = useState(defaultAddress(addresses, "Shipping")?.id ?? "new");
  const [billingId, setBillingId] = useState(defaultAddress(addresses, "Billing")?.id ?? "same");
  const [shippingRates, setShippingRates] = useState<ShippingRate[]>([]);
  const [selectedRate, setSelectedRate] = useState<ShippingRate | null>(null);
  const [quoteError, setQuoteError] = useState<string | null>(null);
  const [paymentOption, setPaymentOption] = useState("CreditCard");
  const paymentGroupRef = useRef<HTMLDivElement>(null);
  const [quoting, startQuote] = useTransition();

  // pay_6: the payment radios are uncontrolled (defaultChecked) and visually hidden, so a click
  // that lands BEFORE hydration still checks the native radio — but React state (which drives the
  // selected border and the card-entry fields) never hears about it. Adopt whatever the DOM says
  // is checked once we mount, so pre-hydration clicks are not silently lost.
  useEffect(() => {
    const checked = paymentGroupRef.current?.querySelector<HTMLInputElement>('input[name="paymentOption"]:checked');
    if (checked) setPaymentOption((current) => (checked.value === current ? current : checked.value));
  }, []);
  const shipping = addresses.find((address) => address.id === shippingId);
  const billing = addresses.find((address) => address.id === billingId);
  const name = profile ? [profile.firstName, profile.lastName].filter(Boolean).join(" ") : "";
  // Tax from the storefront's configured rate — the same math Ordering charges (ADR-0038):
  // inclusive regimes extract the contained portion; exclusive regimes add on goods + shipping.
  const taxBaseMinor = cart.subtotalMinor + (selectedRate?.amountMinor ?? 0);
  const estimatedTaxMinor = taxInclusive
    ? Math.round(taxBaseMinor * taxRateBasisPoints / (10000 + taxRateBasisPoints))
    : Math.round(taxBaseMinor * taxRateBasisPoints / 10000);

  const quoteShipping = (form: HTMLFormElement) => {
    startQuote(async () => {
      setQuoteError(null);
      const result = await quoteCheckoutShipping(new FormData(form));
      if (result.error) {
        setShippingRates([]);
        setSelectedRate(null);
        setQuoteError(result.error);
        return;
      }
      const rates = result.rates ?? [];
      setShippingRates(rates);
      setSelectedRate(rates[0] ?? null);
    });
  };

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
          <Row label="Shipping" value={selectedRate ? formatMoney(selectedRate.amountMinor, selectedRate.currency) : "Choose a rate below"} muted={!selectedRate} />
          <Row
            label={taxInclusive ? `Includes tax (${(taxRateBasisPoints / 100).toFixed(taxRateBasisPoints % 100 === 0 ? 0 : 2)}%)` : "Tax (added)"}
            value={formatMoney(estimatedTaxMinor, cart.currency)}
            muted={estimatedTaxMinor === 0 || taxInclusive}
          />
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
        <h2 className="font-medium">Shipping method</h2>
        <button
          type="button"
          disabled={quoting}
          onClick={(event) => quoteShipping(event.currentTarget.form!)}
          className="rounded-md border border-neutral-300 px-3 py-2 text-sm disabled:opacity-50"
        >
          {quoting ? "Getting rates…" : "Get shipping rates"}
        </button>
        {quoteError && <p className="text-sm text-red-700">{quoteError}</p>}
        {shippingRates.length > 0 && (
          <div className="space-y-2">
            {shippingRates.map((rate) => (
              <label key={`${rate.carrier}:${rate.service}`} className="flex items-center justify-between gap-3 rounded border border-neutral-200 p-3 text-sm">
                <span className="flex items-center gap-2">
                  <input
                    type="radio"
                    name="shippingRateChoice"
                    checked={selectedRate?.service === rate.service && selectedRate.carrier === rate.carrier}
                    onChange={() => setSelectedRate(rate)}
                  />
                  <span>{rate.serviceName} · {rate.estimatedDays} day{rate.estimatedDays === 1 ? "" : "s"}</span>
                </span>
                <span>{formatMoney(rate.amountMinor, rate.currency)}</span>
              </label>
            ))}
          </div>
        )}
        {selectedRate && (
          <>
            <input type="hidden" name="selectedShippingService" value={selectedRate.service} />
            <input type="hidden" name="selectedShippingAmountMinor" value={selectedRate.amountMinor} />
            <input type="hidden" name="selectedShippingExpiresAt" value={selectedRate.expiresAt} />
          </>
        )}
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">Payment</h2>
        <div ref={paymentGroupRef} className="flex flex-wrap gap-2" role="radiogroup" aria-label="Payment options">
          {PAYMENT_OPTIONS.map((option) => {
            const selected = paymentOption === option.value;
            return (
              <label
                key={option.value}
                title={option.label}
                aria-label={option.label}
                data-selected={selected || undefined}
                className={selected
                  ? "flex h-11 min-w-20 cursor-pointer items-center justify-center rounded-md border-2 border-neutral-900 bg-white px-3 shadow-sm has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-neutral-500 has-[:focus-visible]:ring-offset-1"
                  : "flex h-11 min-w-20 cursor-pointer items-center justify-center rounded-md border border-neutral-200 bg-white px-3 hover:border-neutral-400 has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-neutral-500 has-[:focus-visible]:ring-offset-1"}
              >
                <input
                  type="radio"
                  name="paymentOption"
                  value={option.value}
                  defaultChecked={option.value === "CreditCard"}
                  onChange={() => setPaymentOption(option.value)}
                  // click (unlike change) also fires when the radio is ALREADY checked — e.g. after a
                  // pre-hydration click checked it natively — so re-clicking a tile always resyncs
                  // the React-driven selected styling instead of "doing nothing" (pay_6).
                  onClick={() => setPaymentOption(option.value)}
                  className="sr-only"
                />
                {option.icon}
                <span className="sr-only">{option.label}</span>
              </label>
            );
          })}
        </div>
        {profile && paymentMethods.length > 0 && paymentOption === "CreditCard" && (
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
        {paymentOption === "CreditCard" && (
          <div className="space-y-3 rounded-md bg-neutral-50 p-3">
            <Field name="paymentCardNumber" label="Card number" autoComplete="cc-number" title="Provider-hosted card entry in production; this dev field records only a masked summary." />
            <div className="grid grid-cols-2 gap-3">
              <Field name="paymentExpiry" label="Expiry" autoComplete="cc-exp" title="Expiry is used only by the provider in production." />
              <Field name="paymentCvv" label="CVV" autoComplete="cc-csc" title="CVV is provider-hosted in production and never stored." />
            </div>
          </div>
        )}
        {profile && paymentOption === "CreditCard" && (
          <label className="flex items-center gap-2 text-sm text-neutral-700">
            <input name="savePaymentMethod" type="checkbox" className="h-4 w-4" />
            Save this new card for one-click checkout later
          </label>
        )}
        {!profile && <p className="text-sm text-neutral-500">Guests can choose a payment option, but payment methods are not saved to an account. The order keeps the selected method and masked payment summary for audit, tracking, and notifications.</p>}
        <p className="text-xs text-neutral-500">Stripe, Apple Pay, Google Pay, PayPal, and card entry are handled by provider-hosted payment surfaces in production; raw payment credentials must not be stored by 3commerce.</p>
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

function Field({ name, label, type = "text", autoComplete, maxLength, defaultValue, required = false, title, onChange }: {
  name: string;
  label: string;
  type?: string;
  autoComplete?: string;
  maxLength?: number;
  defaultValue?: string;
  required?: boolean;
  title?: string;
  onChange?: (value: string) => void;
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
        onChange={onChange ? (event) => onChange(event.currentTarget.value) : undefined}
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

function CardIcon() {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect x="1" y="4" width="72" height="20" rx="4" fill="#f8fafc" stroke="#cbd5e1" />
      <rect x="8" y="10" width="18" height="4" rx="1" fill="#94a3b8" />
      <rect x="8" y="17" width="32" height="3" rx="1.5" fill="#64748b" />
      <circle cx="55" cy="16" r="5" fill="#ef4444" opacity=".9" />
      <circle cx="61" cy="16" r="5" fill="#f59e0b" opacity=".85" />
    </svg>
  );
}

function StripeIcon() {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect width="74" height="28" rx="5" fill="#635bff" />
      <text x="37" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="14" fontWeight="700" fill="white">stripe</text>
    </svg>
  );
}

function ApplePayIcon() {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect width="74" height="28" rx="5" fill="#000" />
      <text x="37" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="13" fontWeight="700" fill="white"> Pay</text>
    </svg>
  );
}

function GooglePayIcon() {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect width="74" height="28" rx="5" fill="#fff" stroke="#dadce0" />
      <text x="17" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="15" fontWeight="700" fill="#4285f4">G</text>
      <text x="43" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="13" fontWeight="700" fill="#202124">Pay</text>
    </svg>
  );
}

function PayPalIcon() {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect width="74" height="28" rx="5" fill="#ffc439" />
      <text x="37" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="13" fontWeight="700" fill="#003087">PayPal</text>
    </svg>
  );
}
