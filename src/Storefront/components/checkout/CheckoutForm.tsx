"use client";

import { useActionState, useEffect, useRef, useState, useTransition, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { quoteCheckoutShipping, submitCheckout, updateCartQuantity, type CheckoutState, type ShippingRate } from "@/lib/cart-actions";
import type { AddressDto, CartDto, ProfileDto, SavedPaymentMethodDto } from "@/lib/gateway";
import { formatMoney } from "@/lib/money";
import { COUNTRIES, COMMON_COUNTRIES, regionLabel } from "@/lib/countries";

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

// Wire values are fixed; the visible label comes from the `checkout.methods.*` catalog (i18n_1).
const PAYMENT_OPTIONS = [
  { value: "CreditCard", labelKey: "creditCard", icon: <CardIcon /> },
  { value: "Stripe", labelKey: "stripe", icon: <StripeIcon /> },
  { value: "ApplePay", labelKey: "applePay", icon: <ApplePayIcon /> },
  { value: "GooglePay", labelKey: "googlePay", icon: <GooglePayIcon /> },
  { value: "PayPal", labelKey: "payPal", icon: <PayPalIcon /> },
];

export function CheckoutForm({ cart, profile, addresses, paymentMethods, taxRateBasisPoints, taxInclusive }: CheckoutFormProps) {
  const t = useTranslations("checkout");
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
        <h2 className="font-medium">{t("reviewItems")}</h2>
        <ul className="divide-y divide-neutral-100">
          {cart.items.map((item) => (
            <CheckoutLine key={`${item.productId}:${item.variantId ?? "default"}`} item={item} />
          ))}
        </ul>
        <div className="space-y-1 border-t border-neutral-100 pt-3 text-sm">
          <Row label={t("subtotal")} value={formatMoney(cart.subtotalMinor, cart.currency)} />
          <Row
            label={t("shipping")}
            value={selectedRate ? formatMoney(selectedRate.amountMinor, selectedRate.currency) : t("chooseRate")}
            muted={!selectedRate}
          />
          <Row
            label={taxInclusive ? t("includesTax", { percent: formatRate(taxRateBasisPoints) }) : t("taxAdded")}
            value={formatMoney(estimatedTaxMinor, cart.currency)}
            muted={estimatedTaxMinor === 0 || taxInclusive}
          />
        </div>
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">{t("contact")}</h2>
        {profile ? (
          <>
            <p className="text-sm text-neutral-600">{t("signedInAs", { email: profile.email })}</p>
            <input type="hidden" name="email" value={profile.email} />
          </>
        ) : (
          <Field name="email" label={t("email")} type="email" autoComplete="email" required title={t("tips.email")} />
        )}
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">{t("shippingAddress")}</h2>
        {addresses.length > 0 && (
          <AddressSelect
            id="shippingAddressId"
            label={t("savedShippingAddress")}
            title={t("tips.savedShippingAddress")}
            value={shippingId}
            addresses={addresses.filter((address) => canUse(address, "Shipping"))}
            includeSame={false}
            onChange={setShippingId}
          />
        )}
        <AddressFields key={`shipping-${shippingId}`} prefix="shipping" defaults={shipping} fallbackName={name} />
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">{t("shippingMethod")}</h2>
        <button
          type="button"
          disabled={quoting}
          title={t("tips.getRates")}
          onClick={(event) => quoteShipping(event.currentTarget.form!)}
          className="rounded-md border border-neutral-300 px-3 py-2 text-sm disabled:opacity-50"
        >
          {quoting ? t("gettingRates") : t("getRates")}
        </button>
        {quoteError && <p className="text-sm text-red-700">{quoteError}</p>}
        {shippingRates.length > 0 && (
          <div className="space-y-2" role="radiogroup" aria-label={t("shippingRateChoice")}>
            {shippingRates.map((rate) => (
              <label
                key={`${rate.carrier}:${rate.service}`}
                title={t("tips.shippingRateChoice")}
                className="flex items-center justify-between gap-3 rounded border border-neutral-200 p-3 text-sm"
              >
                <span className="flex items-center gap-2">
                  <input
                    type="radio"
                    name="shippingRateChoice"
                    title={t("tips.shippingRateChoice")}
                    checked={selectedRate?.service === rate.service && selectedRate.carrier === rate.carrier}
                    onChange={() => setSelectedRate(rate)}
                  />
                  <span>{t("rateOption", { service: rate.serviceName, days: rate.estimatedDays })}</span>
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
        <h2 className="font-medium">{t("payment")}</h2>
        <div ref={paymentGroupRef} className="flex flex-wrap gap-2" role="radiogroup" aria-label={t("paymentOptions")}>
          {PAYMENT_OPTIONS.map((option) => {
            const selected = paymentOption === option.value;
            const label = t(`methods.${option.labelKey}`);
            return (
              <label
                key={option.value}
                title={`${label} — ${t("tips.paymentOption")}`}
                aria-label={label}
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
                <span className="sr-only">{label}</span>
              </label>
            );
          })}
        </div>
        {profile && paymentMethods.length > 0 && paymentOption === "CreditCard" && (
          <label htmlFor="savedPaymentMethodId" className="block text-sm font-medium" title={t("tips.savedCard")}>
            {t("savedCard")}
            <select
              id="savedPaymentMethodId"
              name="savedPaymentMethodId"
              title={t("tips.savedCard")}
              defaultValue={paymentMethods.find((method) => method.isDefault)?.id ?? "new"}
              className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
            >
              <option value="new">{t("newCard")}</option>
              {paymentMethods.map((method) => (
                <option key={method.id} value={method.id}>
                  {t(method.isDefault ? "savedCardOptionDefault" : "savedCardOption", {
                    brand: method.brand.toUpperCase(),
                    last4: method.last4,
                    expMonth: method.expMonth,
                    expYear: method.expYear,
                  })}
                </option>
              ))}
            </select>
          </label>
        )}
        {paymentOption === "CreditCard" && (
          <div className="space-y-3 rounded-md bg-neutral-50 p-3">
            <Field name="paymentCardNumber" label={t("cardNumber")} autoComplete="cc-number" title={t("tips.cardNumber")} />
            <div className="grid grid-cols-2 gap-3">
              <Field name="paymentExpiry" label={t("expiry")} autoComplete="cc-exp" title={t("tips.expiry")} />
              <Field name="paymentCvv" label={t("cvv")} autoComplete="cc-csc" title={t("tips.cvv")} />
            </div>
          </div>
        )}
        {profile && paymentOption === "CreditCard" && (
          <label className="flex items-center gap-2 text-sm text-neutral-700" title={t("tips.saveCard")}>
            <input name="savePaymentMethod" type="checkbox" className="h-4 w-4" title={t("tips.saveCard")} />
            {t("saveCard")}
          </label>
        )}
        {!profile && <p className="text-sm text-neutral-500">{t("guestPaymentNote")}</p>}
        <p className="text-xs text-neutral-500">{t("providerNote")}</p>
      </section>

      <section className="rounded-md border border-neutral-200 p-4 space-y-3">
        <h2 className="font-medium">{t("billingAddress")}</h2>
        <AddressSelect
          id="billingAddressId"
          label={t("billingAddress")}
          title={t("tips.billingAddress")}
          value={billingId}
          addresses={addresses.filter((address) => canUse(address, "Billing"))}
          includeSame
          onChange={setBillingId}
        />
        {billingId !== "same" && <AddressFields key={`billing-${billingId}`} prefix="billing" defaults={billing} fallbackName={name} />}
        {billingId === "same" && <p className="text-sm text-neutral-500">{t("usingShippingForBilling")}</p>}
      </section>

      <button
        type="submit"
        disabled={pending || cart.items.length === 0}
        title={t("tips.placeOrder")}
        className="w-full rounded-md bg-neutral-900 text-white py-3 text-sm font-medium disabled:opacity-50"
      >
        {pending ? t("authorizing") : t("placeOrder")}
      </button>
    </form>
  );
}

// 1000 bp → "10", 825 bp → "8.25" (the catalog string supplies the % sign and word order).
function formatRate(basisPoints: number): string {
  return (basisPoints / 100).toFixed(basisPoints % 100 === 0 ? 0 : 2);
}

function CheckoutLine({ item }: { item: CartDto["items"][number] }) {
  const t = useTranslations("checkout");
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
          {item.variantSku && <p className="text-xs text-neutral-500">{t("variant", { sku: item.variantSku })}</p>}
          <p className="text-neutral-500">{t("each", { price: formatMoney(item.unitPriceMinor, item.currency) })}</p>
        </div>
        <div className="flex items-center gap-2" aria-label={t("quantityFor", { title: item.title })} title={t("tips.quantity")}>
          <button
            type="button"
            disabled={updating}
            onClick={() => changeQuantity(item.quantity - 1)}
            className="h-8 w-8 rounded border border-neutral-300 disabled:opacity-50"
            aria-label={t("decreaseQuantity")}
            title={t("decreaseQuantity")}
          >
            −
          </button>
          <span className="min-w-6 text-center">{item.quantity}</span>
          <button
            type="button"
            disabled={updating}
            onClick={() => changeQuantity(item.quantity + 1)}
            className="h-8 w-8 rounded border border-neutral-300 disabled:opacity-50"
            aria-label={t("increaseQuantity")}
            title={t("increaseQuantity")}
          >
            +
          </button>
        </div>
      </div>
    </li>
  );
}

function AddressFields({ prefix, defaults, fallbackName }: { prefix: "shipping" | "billing"; defaults?: AddressDto; fallbackName: string }) {
  const t = useTranslations("checkout");
  // Country is controlled so the region field's label adapts to the selected country (State/Province/…).
  const [country, setCountry] = useState(defaults?.country || "AU");
  return (
    <div className="space-y-3">
      <Field name={`${prefix}Name`} label={t("fullName")} autoComplete="name" defaultValue={defaults?.name ?? fallbackName} required title={t("tips.fullName")} />
      <Field name={`${prefix}Line1`} label={t("address")} autoComplete="address-line1" defaultValue={defaults?.line1 ?? ""} required title={t("tips.address")} />
      <div className="grid grid-cols-2 gap-3">
        <Field name={`${prefix}City`} label={t("city")} autoComplete="address-level2" defaultValue={defaults?.city ?? ""} required title={t("tips.city")} />
        <Field name={`${prefix}Region`} label={regionLabel(country)} autoComplete="address-level1" defaultValue={defaults?.region ?? ""} title={regionLabel(country)} />
      </div>
      <div className="grid grid-cols-2 gap-3">
        <Field name={`${prefix}Postcode`} label={t("postcode")} autoComplete="postal-code" defaultValue={defaults?.postcode ?? ""} required title={t("tips.postcode")} />
        <CountryField name={`${prefix}Country`} label={t("country")} value={country} onChange={setCountry} title={t("tips.country")} />
      </div>
    </div>
  );
}

function CountryField({ name, label, value, onChange, title }: { name: string; label: string; value: string; onChange: (v: string) => void; title: string }) {
  return (
    <div>
      <label htmlFor={name} className="block text-sm font-medium" title={title}>{label}</label>
      <select
        id={name}
        name={name}
        value={value}
        required
        autoComplete="country"
        title={title}
        aria-describedby={`${name}-tip`}
        onChange={(e) => onChange(e.currentTarget.value)}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
      >
        <optgroup label="Common">
          {COMMON_COUNTRIES.map((c) => <option key={`c-${c}`} value={c}>{COUNTRIES.find((x) => x.code === c)?.name ?? c}</option>)}
        </optgroup>
        <optgroup label="All countries">
          {COUNTRIES.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
        </optgroup>
      </select>
      <span id={`${name}-tip`} className="sr-only">{title}</span>
    </div>
  );
}

function AddressSelect({ id, label, title, value, addresses, includeSame, onChange }: {
  id: string;
  label: string;
  title: string;
  value: string;
  addresses: AddressDto[];
  includeSame: boolean;
  onChange: (value: string) => void;
}) {
  const t = useTranslations("checkout");
  return (
    <label htmlFor={id} className="block text-sm font-medium" title={title}>
      {label}
      <select
        id={id}
        value={value}
        title={title}
        aria-describedby={`${id}-tip`}
        onChange={(event) => onChange(event.target.value)}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
      >
        {includeSame && <option value="same">{t("sameAsShipping")}</option>}
        {addresses.map((address) => (
          <option key={address.id} value={address.id}>
            {t(address.isDefault ? "savedAddressOptionDefault" : "savedAddressOption", {
              name: address.name,
              line1: address.line1,
              city: address.city,
            })}
          </option>
        ))}
        <option value="new">{t("newAddress")}</option>
      </select>
      <span id={`${id}-tip`} className="sr-only">
        {title}
      </span>
    </label>
  );
}

// Every field carries its localized tooltip twice over: `title` (hover) and an sr-only
// `aria-describedby` help text (screen readers / keyboard-only users) — i18n_1 tooltip coverage.
function Field({ name, label, type = "text", autoComplete, maxLength, defaultValue, required = false, title, onChange }: {
  name: string;
  label: string;
  type?: string;
  autoComplete?: string;
  maxLength?: number;
  defaultValue?: string;
  required?: boolean;
  title: string;
  onChange?: (value: string) => void;
}) {
  return (
    <div>
      <label htmlFor={name} className="block text-sm font-medium" title={title}>{label}</label>
      <input
        id={name}
        name={name}
        type={type}
        required={required}
        autoComplete={autoComplete}
        maxLength={maxLength}
        defaultValue={defaultValue}
        title={title}
        aria-describedby={`${name}-tip`}
        onChange={onChange ? (event) => onChange(event.currentTarget.value) : undefined}
        className="mt-1 w-full rounded-md border border-neutral-300 px-3 py-2 text-sm"
      />
      <span id={`${name}-tip`} className="sr-only">
        {title}
      </span>
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

function CardIcon(): ReactNode {
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

function StripeIcon(): ReactNode {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect width="74" height="28" rx="5" fill="#635bff" />
      <text x="37" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="14" fontWeight="700" fill="white">stripe</text>
    </svg>
  );
}

function ApplePayIcon(): ReactNode {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect width="74" height="28" rx="5" fill="#000" />
      <text x="37" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="13" fontWeight="700" fill="white"> Pay</text>
    </svg>
  );
}

function GooglePayIcon(): ReactNode {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect width="74" height="28" rx="5" fill="#fff" stroke="#dadce0" />
      <text x="17" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="15" fontWeight="700" fill="#4285f4">G</text>
      <text x="43" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="13" fontWeight="700" fill="#202124">Pay</text>
    </svg>
  );
}

function PayPalIcon(): ReactNode {
  return (
    <svg viewBox="0 0 74 28" width="74" height="28" role="img" aria-hidden="true" className="h-7 w-auto">
      <rect width="74" height="28" rx="5" fill="#ffc439" />
      <text x="37" y="18" textAnchor="middle" fontFamily="Arial, Helvetica, sans-serif" fontSize="13" fontWeight="700" fill="#003087">PayPal</text>
    </svg>
  );
}
