"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import type { CardBillingAddressDto } from "@/lib/gateway";
import { COUNTRIES, COMMON_COUNTRIES, regionLabel } from "@/lib/countries";

interface CardBillingAddressFormProps {
  methodId: string;
  // The card's own billing address (Payments, mem_2), or null when none is saved.
  address: CardBillingAddressDto | null;
}

/**
 * One saved card's billing address (mem_2): shown under the card, edited in place, removable. Checkout fills the
 * order's bill-to from it when the shopper pays with this card. The form posts to the card's route handler
 * (`/account/payment-method/{id}`, action `billing` / `clear-billing`), like the card's other actions, and the
 * result comes back as the page's `?card=` status. No card data is involved — only the address.
 */
export function CardBillingAddressForm({ methodId, address }: CardBillingAddressFormProps) {
  const t = useTranslations("account");
  const tc = useTranslations("checkout");
  const [country, setCountry] = useState(address?.country ?? "AU");
  const prefix = `card-${methodId}`;
  const countryName = (code: string) => COUNTRIES.find((x) => x.code === code)?.name ?? code;

  return (
    <div className="mt-1 w-full text-xs" data-testid={`card-billing-${methodId}`}>
      {address ? (
        <p className="text-neutral-600" data-testid="card-billing-address">
          {t("cardBilling.heading")}: {[address.name, address.line1, address.city, address.region, address.postcode, address.country].filter(Boolean).join(", ")}
        </p>
      ) : (
        <p className="text-neutral-500">{t("cardBilling.none")}</p>
      )}
      <details className="mt-1">
        <summary className="cursor-pointer text-neutral-700 underline underline-offset-2" title={t("cardBilling.tips.edit")}>
          {address ? t("cardBilling.edit") : t("cardBilling.add")}
        </summary>
        <form action={`/account/payment-method/${methodId}`} method="post" className="mt-2 space-y-2 rounded-md border border-neutral-200 p-3">
          <input type="hidden" name="action" value="billing" />
          <Field id={`${prefix}-name`} name="name" label={tc("fullName")} title={tc("tips.fullName")} defaultValue={address?.name ?? ""} autoComplete="name" maxLength={100} required />
          <Field id={`${prefix}-line1`} name="line1" label={tc("address")} title={tc("tips.address")} defaultValue={address?.line1 ?? ""} autoComplete="address-line1" maxLength={200} required />
          <div className="grid grid-cols-2 gap-2">
            <Field id={`${prefix}-city`} name="city" label={tc("city")} title={tc("tips.city")} defaultValue={address?.city ?? ""} autoComplete="address-level2" maxLength={100} required />
            <Field id={`${prefix}-region`} name="region" label={regionLabel(country)} title={regionLabel(country)} defaultValue={address?.region ?? ""} autoComplete="address-level1" maxLength={120} />
          </div>
          <div className="grid grid-cols-2 gap-2">
            <Field id={`${prefix}-postcode`} name="postcode" label={tc("postcode")} title={tc("tips.postcode")} defaultValue={address?.postcode ?? ""} autoComplete="postal-code" maxLength={20} required />
            <div>
              <label htmlFor={`${prefix}-country`} className="block font-medium" title={tc("tips.country")}>{tc("country")}</label>
              <select
                id={`${prefix}-country`}
                name="country"
                value={country}
                onChange={(event) => setCountry(event.currentTarget.value)}
                required
                autoComplete="country"
                title={tc("tips.country")}
                aria-describedby={`${prefix}-country-tip`}
                className="mt-1 w-full rounded-md border border-neutral-300 px-2 py-1"
              >
                <optgroup label="Common">
                  {COMMON_COUNTRIES.map((code) => <option key={`c-${code}`} value={code}>{countryName(code)}</option>)}
                </optgroup>
                <optgroup label="All countries">
                  {COUNTRIES.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
                </optgroup>
              </select>
              <span id={`${prefix}-country-tip`} className="sr-only">{tc("tips.country")}</span>
            </div>
          </div>
          <button type="submit" title={t("cardBilling.tips.save")} className="rounded-md bg-neutral-900 px-3 py-1 font-medium text-white">
            {t("cardBilling.save")}
          </button>
        </form>
        {address && (
          <form action={`/account/payment-method/${methodId}`} method="post" className="mt-2">
            <button
              type="submit"
              name="action"
              value="clear-billing"
              title={t("cardBilling.tips.clear")}
              className="rounded-md border border-red-300 px-3 py-1 text-red-700"
            >
              {t("cardBilling.clear")}
            </button>
          </form>
        )}
      </details>
    </div>
  );
}

// A visible label, the localized tooltip as `title` and as sr-only help text tied via aria-describedby.
function Field({ id, name, label, title, defaultValue, autoComplete, maxLength, required = false }: {
  id: string;
  name: string;
  label: string;
  title: string;
  defaultValue: string;
  autoComplete?: string;
  maxLength?: number;
  required?: boolean;
}) {
  return (
    <div>
      <label htmlFor={id} className="block font-medium" title={title}>{label}</label>
      <input
        id={id}
        name={name}
        type="text"
        defaultValue={defaultValue}
        required={required}
        autoComplete={autoComplete}
        maxLength={maxLength}
        title={title}
        aria-describedby={`${id}-tip`}
        className="mt-1 w-full rounded-md border border-neutral-300 px-2 py-1"
      />
      <span id={`${id}-tip`} className="sr-only">{title}</span>
    </div>
  );
}
